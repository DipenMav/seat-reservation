using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SeatReservation.Tests;

[Collection(ApiCollection.Name)]
public sealed class BehaviourTests(ApiFixture fx)
{
    private readonly HttpClient _client = fx.Factory.CreateClient();

    [Fact]
    public async Task CreateShow_AllSeatsAvailable()
    {
        var res = await _client.CallAsync(Api.Request(HttpMethod.Post, "/shows", "admin",
            new { name = "friday-night", seats = new[] { "A1", "A2", "A3" }, price_paise = 25000 }));

        Assert.Equal(HttpStatusCode.Created, res.Status);
        Assert.Equal(3, res.Body!["available_seats"]!.GetValue<int>());
        Assert.Equal(4, res.Body["per_user_limit"]!.GetValue<int>());
        Assert.All(res.Body["seats"]!.AsArray(), s => Assert.Equal("available", s!["status"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("some-user", HttpStatusCode.Forbidden)]
    public async Task CreateShow_RequiresAdmin(string? token, HttpStatusCode expected)
    {
        var res = await _client.CallAsync(Api.Request(HttpMethod.Post, "/shows", token,
            new { name = "x", seats = new[] { "A1" }, price_paise = 100 }));
        Assert.Equal(expected, res.Status);
    }

    [Theory]
    [InlineData("""{"name":"x","seats":["A1"],"price_paise":250.5}""")]
    [InlineData("""{"name":"x","seats":["A1"],"price_paise":"25000"}""")]
    [InlineData("""{"name":"x","seats":["A1","A1"],"price_paise":100}""")]
    [InlineData("""{"name":"x","seats":[],"price_paise":100}""")]
    [InlineData("""{"name":"x","seats":["A1"],"price_paise":-1}""")]
    public async Task CreateShow_RejectsInvalidInput(string json)
    {
        var req = Api.Request(HttpMethod.Post, "/shows", "admin");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var res = await _client.CallAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.Status);
        Assert.Equal("VALIDATION_ERROR", res.ErrorCode);
    }

    [Fact]
    public async Task Reserve_ReturnsContractShape_AndIntegerPaise()
    {
        var show = await _client.CreateShowAsync(Api.Seats(5), price: 25000);
        var res = await _client.ReserveAsync(show, "shape-user", ["A2", "A1"]);

        Assert.Equal(HttpStatusCode.Created, res.Status);
        Assert.Equal(show, res.Body!["show_id"]!.GetValue<string>());
        Assert.Equal("shape-user", res.Body["user_id"]!.GetValue<string>());
        Assert.Equal(["A1", "A2"], res.Body["seats"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(50000L, res.Body["amount_paise"]!.GetValue<long>());
        Assert.Equal("confirmed", res.Body["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reserve_RequiresAuthentication()
    {
        var show = await _client.CreateShowAsync(Api.Seats(2));
        var res = await _client.CallAsync(Api.Request(HttpMethod.Post, $"/shows/{show}/reserve", null,
            new { seats = new[] { "A1" }, idempotency_key = "k" }));
        Assert.Equal(HttpStatusCode.Unauthorized, res.Status);
    }

    [Fact]
    public async Task Reserve_SpoofedUserIdInBody_ActsAsTokenUser()
    {
        var show = await _client.CreateShowAsync(Api.Seats(3));
        var res = await _client.ReserveAsync(show, "real-user", ["A1"], extra: new { user_id = "victim" });

        Assert.Equal(HttpStatusCode.Created, res.Status);
        Assert.Equal("real-user", res.Body!["user_id"]!.GetValue<string>());
        Assert.Equal("real-user", (await _client.GetShowAsync(show)).OwnerOf("A1"));
    }

    [Fact]
    public async Task Idempotency_RetryReturnsOriginal_DifferentBodyIs409_SeatOrderIrrelevant()
    {
        var show = await _client.CreateShowAsync(Api.Seats(5));
        var first = await _client.ReserveAsync(show, "idem", ["A1", "A2"], key: "abc");
        var retry = await _client.ReserveAsync(show, "idem", ["A2", "A1"], key: "abc");
        var different = await _client.ReserveAsync(show, "idem", ["A3"], key: "abc");

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(first.ReservationId, retry.ReservationId);
        Assert.True(retry.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(HttpStatusCode.Conflict, different.Status);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", different.ErrorCode);

        var state = await _client.GetShowAsync(show);
        Assert.Equal(2, state.Confirmed);
        Assert.Equal("available", state.StatusOf("A3"));
    }

    [Fact]
    public async Task Idempotency_KeyIsScopedToUser()
    {
        var show = await _client.CreateShowAsync(Api.Seats(5));
        var a = await _client.ReserveAsync(show, "user-a", ["A1"], key: "shared");
        var b = await _client.ReserveAsync(show, "user-b", ["A2"], key: "shared");
        Assert.Equal(HttpStatusCode.Created, a.Status);
        Assert.Equal(HttpStatusCode.Created, b.Status);
        Assert.NotEqual(a.ReservationId, b.ReservationId);
    }

    [Fact]
    public async Task Idempotency_KeyViaHeader_MatchesBody()
    {
        var show = await _client.CreateShowAsync(Api.Seats(3));
        var viaHeader = await _client.CallAsync(Api.Request(HttpMethod.Post, $"/shows/{show}/reserve", "hdr",
            new { seats = new[] { "A1" } }, idempotencyKey: "h-1"));
        var viaBody = await _client.ReserveAsync(show, "hdr", ["A1"], key: "h-1");
        var missing = await _client.CallAsync(Api.Request(HttpMethod.Post, $"/shows/{show}/reserve", "hdr",
            new { seats = new[] { "A2" } }));

        Assert.Equal(HttpStatusCode.Created, viaHeader.Status);
        Assert.Equal(viaHeader.ReservationId, viaBody.ReservationId);
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
    }

    [Fact]
    public async Task MultiSeat_OneUnavailable_NothingReserved()
    {
        var show = await _client.CreateShowAsync(Api.Seats(5));
        await _client.ReserveAsync(show, "first", ["A2"]);

        var res = await _client.ReserveAsync(show, "second", ["A1", "A2", "A3"]);

        Assert.Equal(HttpStatusCode.Conflict, res.Status);
        var state = await _client.GetShowAsync(show);
        Assert.Equal("available", state.StatusOf("A1"));
        Assert.Equal("available", state.StatusOf("A3"));
        Assert.Equal("first", state.OwnerOf("A2"));
    }

    [Fact]
    public async Task Reserve_DuplicateOrUnknownSeats_400_UnknownShow_404()
    {
        var show = await _client.CreateShowAsync(Api.Seats(3));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.ReserveAsync(show, "u", ["A1", "A1"])).Status);
        Assert.Equal("UNKNOWN_SEAT", (await _client.ReserveAsync(show, "u", ["Z9"])).ErrorCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.ReserveAsync(Guid.NewGuid().ToString(), "u", ["A1"])).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.ReserveAsync("not-a-guid", "u", ["A1"])).Status);
    }

    [Fact]
    public async Task OverLimitInOneRequest_IsCleanDecline()
    {
        var show = await _client.CreateShowAsync(Api.Seats(10), perUserLimit: 2);
        var res = await _client.ReserveAsync(show, "u", ["A1", "A2", "A3"]);
        Assert.Equal(HttpStatusCode.Conflict, res.Status);
        Assert.Equal("USER_LIMIT_EXCEEDED", res.ErrorCode);
    }

    [Fact]
    public async Task Cancel_OnlyOwner_ReleasesAndSeatIsRebookable()
    {
        var show = await _client.CreateShowAsync(Api.Seats(3));
        var mine = await _client.ReserveAsync(show, "alice", ["A1"]);

        var stolen = await _client.CancelAsync(mine.ReservationId!, "mallory");
        Assert.Equal(HttpStatusCode.Forbidden, stolen.Status);
        Assert.Equal("alice", (await _client.GetShowAsync(show)).OwnerOf("A1"));

        var cancelled = await _client.CancelAsync(mine.ReservationId!, "alice");
        Assert.Equal(HttpStatusCode.OK, cancelled.Status);
        Assert.Equal("cancelled", cancelled.Body!["status"]!.GetValue<string>());
        Assert.Equal("available", (await _client.GetShowAsync(show)).StatusOf("A1"));

        var rebook = await _client.ReserveAsync(show, "bob", ["A1"]);
        Assert.Equal(HttpStatusCode.Created, rebook.Status);

        // Cancelling alice's (already cancelled) reservation again must not touch bob's seat.
        Assert.Equal(HttpStatusCode.OK, (await _client.CancelAsync(mine.ReservationId!, "alice")).Status);
        var state = await _client.GetShowAsync(show);
        Assert.Equal("bob", state.OwnerOf("A1"));
        state.AssertReconciled();
    }

    [Fact]
    public async Task Cancel_UnknownReservation_404()
    {
        var res = await _client.CancelAsync(Guid.NewGuid().ToString(), "someone");
        Assert.Equal(HttpStatusCode.NotFound, res.Status);
    }

    [Fact]
    public async Task Errors_CarryCorrelationId_EchoedInHeader()
    {
        var req = Api.Request(HttpMethod.Get, $"/shows/{Guid.NewGuid()}", null);
        req.Headers.Add("X-Correlation-ID", "trace-me-123");
        var res = await _client.CallAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.Status);
        Assert.Equal("trace-me-123", res.Body!["error"]!["correlation_id"]!.GetValue<string>());
        Assert.Equal("trace-me-123", res.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Metrics_ExposeRequiredSeries_AndNoReconciliationDrift()
    {
        var show = await _client.CreateShowAsync(Api.Seats(3));
        await _client.ReserveAsync(show, "m", ["A1"]);

        var text = await _client.GetStringAsync("/metrics");
        foreach (var series in new[]
                 {
                     "reservations_confirmed_total", "reservations_declined_total{reason=\"seat_taken\"}",
                     "reservations_declined_total{reason=\"per_user_limit\"}",
                     "reservations_declined_total{reason=\"idempotent_replay\"}",
                     "seats_available", "seats_held", "seats_confirmed",
                 })
            Assert.Contains(series, text);

        Assert.Contains("reconciliation_drift{check=\"status_sum_vs_total\"} 0", text);
        Assert.Contains("reconciliation_drift{check=\"confirmed_vs_reservation_seats\"} 0", text);
        Assert.Contains("reconciliation_drift{check=\"confirmed_vs_user_counts\"} 0", text);
    }

    [Fact]
    public async Task ApiPrefix_RoutesToSameEndpoints()
    {
        var show = await _client.CreateShowAsync(Api.Seats(2));
        var res = await _client.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>($"/api/shows/{show}");
        Assert.Equal(2, res!["total_seats"]!.GetValue<int>());
    }
}
