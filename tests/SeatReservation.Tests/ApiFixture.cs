using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace SeatReservation.Tests;

/// <summary>
/// Real PostgreSQL (Testcontainers, or TEST_DATABASE_URL if set) behind the real HTTP pipeline.
/// Concurrency tests are only meaningful against the real database's locking behaviour.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
        }
        else
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithCommand("-c", "max_connections=300")
                .Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", ConnectionString);
            b.UseSetting("DB_MAX_POOL_SIZE", "60");
            b.UseSetting("Logging:LogLevel:Default", "Warning");
        });

        using var client = Factory.CreateClient();
        await WaitUntilReadyAsync(client);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    public static async Task WaitUntilReadyAsync(HttpClient client)
    {
        for (var i = 0; i < 120; i++)
        {
            if ((await client.GetAsync("/health/ready")).IsSuccessStatusCode) return;
            await Task.Delay(500);
        }
        throw new TimeoutException("API never became ready");
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

public sealed record ApiResponse(HttpStatusCode Status, JsonNode? Body, HttpResponseHeaders Headers)
{
    public string? ErrorCode => Body?["error"]?["code"]?.GetValue<string>();
    public string? ReservationId => Body?["reservation_id"]?.GetValue<string>();
}

public static class Api
{
    public static HttpRequestMessage Request(HttpMethod method, string url, string? token, object? body = null, string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    public static async Task<ApiResponse> CallAsync(this HttpClient client, HttpRequestMessage req)
    {
        using var res = await client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return new ApiResponse(res.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null, res.Headers);
    }

    public static async Task<string> CreateShowAsync(this HttpClient client, IEnumerable<string> seats, int? perUserLimit = null, long price = 25000)
    {
        var res = await client.CallAsync(Request(HttpMethod.Post, "/shows", "admin", new
        {
            name = "test-show",
            seats = seats.ToArray(),
            price_paise = price,
            per_user_limit = perUserLimit,
        }));
        Assert.Equal(HttpStatusCode.Created, res.Status);
        return res.Body!["id"]!.GetValue<string>();
    }

    public static Task<ApiResponse> ReserveAsync(this HttpClient client, string showId, string user, string[] seats, string? key = null, object? extra = null)
    {
        var body = new JsonObject
        {
            ["seats"] = new JsonArray(seats.Select(s => (JsonNode)s!).ToArray()),
            ["idempotency_key"] = key ?? Guid.NewGuid().ToString(),
        };
        if (extra is not null)
            foreach (var (k, v) in JsonSerializer.SerializeToNode(extra)!.AsObject()) body[k] = v?.DeepClone();
        return client.CallAsync(Request(HttpMethod.Post, $"/shows/{showId}/reserve", user, body));
    }

    public static Task<ApiResponse> CancelAsync(this HttpClient client, string reservationId, string user) =>
        client.CallAsync(Request(HttpMethod.Post, $"/reservations/{reservationId}/cancel", user));

    public static async Task<ShowSnapshot> GetShowAsync(this HttpClient client, string showId)
    {
        var res = await client.CallAsync(Request(HttpMethod.Get, $"/shows/{showId}", "admin"));
        Assert.Equal(HttpStatusCode.OK, res.Status);
        return new ShowSnapshot(res.Body!);
    }

    public static IEnumerable<string> Seats(int count, string row = "A") =>
        Enumerable.Range(1, count).Select(i => $"{row}{i}");
}

public sealed class ShowSnapshot(JsonNode body)
{
    public int Total => body["total_seats"]!.GetValue<int>();
    public int Available => body["available_seats"]!.GetValue<int>();
    public int Held => body["held_seats"]!.GetValue<int>();
    public int Confirmed => body["confirmed_seats"]!.GetValue<int>();
    public bool Reconciled => body["reconciled"]!.GetValue<bool>();

    public IReadOnlyList<(string Seat, string Status, string? UserId, string? ReservationId)> Seats =>
        body["seats"]!.AsArray().Select(s => (
            s!["seat"]!.GetValue<string>(),
            s["status"]!.GetValue<string>(),
            s["user_id"]?.GetValue<string>(),
            s["reservation_id"]?.GetValue<string>())).ToList();

    public string StatusOf(string seat) => Seats.Single(s => s.Seat == seat).Status;
    public string? OwnerOf(string seat) => Seats.Single(s => s.Seat == seat).UserId;

    public void AssertReconciled()
    {
        Assert.Equal(Total, Available + Held + Confirmed);
        Assert.True(Reconciled);
        Assert.Equal(Confirmed, Seats.Count(s => s.Status == "confirmed"));
        Assert.All(Seats.Where(s => s.Status == "confirmed"), s => Assert.NotNull(s.UserId));
    }
}
