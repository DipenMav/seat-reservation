using System.Net;

namespace SeatReservation.Tests;

[Collection(ApiCollection.Name)]
public sealed class ConcurrencyTests(ApiFixture fx)
{
    private readonly HttpClient _client = fx.Factory.CreateClient();

    [Fact]
    public async Task HotSeat_500ConcurrentUsers_ExactlyOneWinner_RestCleanConflicts()
    {
        var show = await _client.CreateShowAsync(Api.Seats(20));

        var results = await Task.WhenAll(Enumerable.Range(0, 500)
            .Select(i => _client.ReserveAsync(show, $"hot-user-{i}", ["A12"])));

        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.Equal(499, results.Count(r => r.Status == HttpStatusCode.Conflict && r.ErrorCode == "SEAT_UNAVAILABLE"));
        Assert.DoesNotContain(results, r => (int)r.Status >= 500);

        var winner = results.Single(r => r.Status == HttpStatusCode.Created);
        var state = await _client.GetShowAsync(show);
        state.AssertReconciled();
        Assert.Equal(1, state.Confirmed);
        Assert.Equal(winner.Body!["user_id"]!.GetValue<string>(), state.OwnerOf("A12"));
    }

    [Fact]
    public async Task PerUserLimit_10ParallelRequests_EndsWithAtMostFourSeats()
    {
        var show = await _client.CreateShowAsync(Api.Seats(20), perUserLimit: 4);

        var results = await Task.WhenAll(Enumerable.Range(1, 10)
            .Select(i => _client.ReserveAsync(show, "greedy", [$"A{i}"])));

        Assert.Equal(4, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(6, results.Count(r => r.ErrorCode == "USER_LIMIT_EXCEEDED"));

        var state = await _client.GetShowAsync(show);
        state.AssertReconciled();
        Assert.Equal(4, state.Seats.Count(s => s.UserId == "greedy"));
    }

    [Fact]
    public async Task PerUserLimit_ParallelMultiSeatRequests_NeverExceedLimit()
    {
        var show = await _client.CreateShowAsync(Api.Seats(40), perUserLimit: 4);

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => _client.ReserveAsync(show, "pair-user", [$"A{2 * i + 1}", $"A{2 * i + 2}", $"A{2 * i + 21}"])));

        // 3 seats per request with limit 4: exactly one request can fit.
        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        var state = await _client.GetShowAsync(show);
        state.AssertReconciled();
        Assert.Equal(3, state.Seats.Count(s => s.UserId == "pair-user"));
    }

    [Fact]
    public async Task IdempotencyStorm_100ConcurrentSameKey_OneReservation()
    {
        var show = await _client.CreateShowAsync(Api.Seats(10));

        var results = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => _client.ReserveAsync(show, "retrier", ["A3", "A4"], key: "storm-key")));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Single(results.Select(r => r.ReservationId).Distinct());
        Assert.Equal(99, results.Count(r => r.Headers.Contains("Idempotent-Replayed")));

        var state = await _client.GetShowAsync(show);
        state.AssertReconciled();
        Assert.Equal(2, state.Confirmed);
    }

    [Fact]
    public async Task IdempotencyStorm_MixedBodiesOnSameKey_OneWinnerOthersConflict()
    {
        var show = await _client.CreateShowAsync(Api.Seats(10));

        var results = await Task.WhenAll(Enumerable.Range(0, 60)
            .Select(i => _client.ReserveAsync(show, "mixed", [i % 2 == 0 ? "A1" : "A2"], key: "same-key")));

        var created = results.Where(r => r.Status == HttpStatusCode.Created).ToList();
        Assert.Single(created.Select(r => r.ReservationId).Distinct());
        var winnerSeat = created[0].Body!["seats"]![0]!.GetValue<string>();
        // Everyone who sent the other body is rejected as an idempotency conflict, never booked.
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created),
            r => Assert.Equal("IDEMPOTENCY_KEY_REUSED", r.ErrorCode));

        var state = await _client.GetShowAsync(show);
        Assert.Equal(1, state.Confirmed);
        Assert.Equal("confirmed", state.StatusOf(winnerSeat));
    }

    [Fact]
    public async Task OverlappingMultiSeatRequests_AllOrNothing_NoDeadlockErrors()
    {
        var seats = Api.Seats(8).ToArray();
        var show = await _client.CreateShowAsync(seats);
        var rng = new Random(42);

        var requests = Enumerable.Range(0, 300).Select(i =>
        {
            var pick = seats.OrderBy(_ => rng.Next()).Take(rng.Next(2, 4)).ToArray();
            return (User: $"multi-{i}", Seats: pick);
        }).ToList();

        var results = await Task.WhenAll(requests.Select(r => _client.ReserveAsync(show, r.User, r.Seats)));

        Assert.DoesNotContain(results, r => (int)r.Status >= 500);
        var state = await _client.GetShowAsync(show);
        state.AssertReconciled();

        // Every successful request owns ALL of its seats; every failed one owns NONE.
        for (var i = 0; i < requests.Count; i++)
        {
            var owned = state.Seats.Count(s => s.UserId == requests[i].User);
            Assert.Equal(results[i].Status == HttpStatusCode.Created ? requests[i].Seats.Length : 0, owned);
        }
    }

    [Fact]
    public async Task CancelRacingWithStorm_NeverResurrectsOrDoubleSells()
    {
        var show = await _client.CreateShowAsync(Api.Seats(5));
        var original = await _client.ReserveAsync(show, "owner", ["A1"]);
        Assert.Equal(HttpStatusCode.Created, original.Status);

        var storm = Enumerable.Range(0, 200).Select(i => _client.ReserveAsync(show, $"racer-{i}", ["A1"])).ToList();
        var cancel = _client.CancelAsync(original.ReservationId!, "owner");
        var results = await Task.WhenAll(storm);
        Assert.Equal(HttpStatusCode.OK, (await cancel).Status);

        Assert.DoesNotContain(results, r => (int)r.Status >= 500);
        var winners = results.Where(r => r.Status == HttpStatusCode.Created).ToList();
        Assert.True(winners.Count <= 1);

        var state = await _client.GetShowAsync(show);
        state.AssertReconciled();
        if (winners.Count == 1)
            Assert.Equal(winners[0].Body!["user_id"]!.GetValue<string>(), state.OwnerOf("A1"));
        else
            Assert.Equal("available", state.StatusOf("A1"));
    }

    [Fact]
    public async Task ConcurrentDoubleCancel_ReleasesOnce()
    {
        var show = await _client.CreateShowAsync(Api.Seats(5), perUserLimit: 2);
        var r = await _client.ReserveAsync(show, "canceller", ["A1", "A2"]);

        var cancels = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _client.CancelAsync(r.ReservationId!, "canceller")));
        Assert.All(cancels, c => Assert.Equal(HttpStatusCode.OK, c.Status));

        // If the counter had been decremented twice it would be negative (CHECK fails) or allow > limit.
        var again = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => _client.ReserveAsync(show, "canceller", [$"A{i}"])));
        Assert.Equal(2, again.Count(x => x.Status == HttpStatusCode.Created));
        (await _client.GetShowAsync(show)).AssertReconciled();
    }
}
