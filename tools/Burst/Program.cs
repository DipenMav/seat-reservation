// On-sale stampede against a live deployment, then verification of every correctness property.
//
//   dotnet run -c Release --project tools/Burst -- <BASE_URL> [--requests 20000] [--concurrency 500]
//        [--seats 2000] [--hot-seats 5] [--admin-token admin] [--limit 4]
//
// Exit code 0 = every check passed.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var opts = Options.Parse(args);
if (opts is null)
{
    Console.Error.WriteLine("usage: burst <BASE_URL> [--requests N] [--concurrency C] [--seats S] [--hot-seats H] [--admin-token T] [--limit L]");
    return 2;
}

using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = opts.Concurrency,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(30),
})
{
    BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(120),
};

// Separate client for the during-burst invariant sampler so the stampede can't starve it.
using var probe = new HttpClient { BaseAddress = http.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
var report = new Report();
Console.WriteLine($"Target            {opts.BaseUrl}");
Console.WriteLine($"Requests          {opts.Requests:N0} (concurrency {opts.Concurrency}, seats {opts.Seats:N0}, hot seats {opts.HotSeats}, limit {opts.Limit})");

// ---- 0. Wait for readiness (survives a cold start) -------------------------------------------
var readyWatch = Stopwatch.StartNew();
while (true)
{
    try
    {
        using var r = await http.GetAsync("health/ready");
        if (r.IsSuccessStatusCode) break;
    }
    catch (Exception) when (readyWatch.Elapsed < TimeSpan.FromSeconds(180)) { }
    if (readyWatch.Elapsed > TimeSpan.FromSeconds(180)) { Console.Error.WriteLine("Service never became ready."); return 1; }
    await Task.Delay(2000);
}
Console.WriteLine($"Ready after       {readyWatch.Elapsed.TotalSeconds:F1}s");

// ---- 1. Create a fresh show --------------------------------------------------------------------
// Rows of 50: A1..A50, B1..B50, …, Z50, AA1, … The hot seats start at the classic "A12".
static string RowName(int r) => r < 26 ? ((char)('A' + r)).ToString() : RowName(r / 26 - 1) + (char)('A' + r % 26);
var seatNames = Enumerable.Range(0, opts.Seats).Select(i => $"{RowName(i / 50)}{i % 50 + 1}").ToArray();
var hotSeats = seatNames.Skip(11).Take(opts.HotSeats).ToArray();
var coldSeats = seatNames.Except(hotSeats).ToArray();

var create = await Send(HttpMethod.Post, "shows", opts.AdminToken, new JsonObject
{
    ["name"] = $"burst-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}",
    ["seats"] = new JsonArray(seatNames.Select(s => (JsonNode)s!).ToArray()),
    ["price_paise"] = 25000,
    ["per_user_limit"] = opts.Limit,
});
if (create.Status != 201) { Console.Error.WriteLine($"Create show failed: {create.Status} {create.Raw}"); return 1; }
var showId = create.Body!["id"]!.GetValue<string>();
Console.WriteLine($"Show              {showId}");
Console.WriteLine($"Hot seats         {string.Join(", ", hotSeats)}");

var metricsBefore = await ScrapeMetrics();

// ---- 2. Build the stampede ----------------------------------------------------------------------
var rng = new Random(20261002);
var plan = new List<Planned>();
string NewKey() => Guid.NewGuid().ToString("N");

int hotCount = (int)(opts.Requests * 0.40);
int generalCount = (int)(opts.Requests * 0.35);
int limitGroups = Math.Max(1, (int)(opts.Requests * 0.05) / 10);
int spoofCount = (int)(opts.Requests * 0.02);

// 40%: hot-seat storm, many distinct users per hot seat.
for (var i = 0; i < hotCount; i++)
{
    var seat = hotSeats[i % hotSeats.Length];
    plan.Add(new Planned("hot", $"hot-{i}", NewKey(), [seat]));
}

// 35%: general on-sale traffic, 1-2 random seats, users drawn from a pool so some collide on limits.
var userPool = Math.Max(10, generalCount / 3);
for (var i = 0; i < generalCount; i++)
{
    var n = rng.Next(1, 3);
    var seats = Enumerable.Range(0, n).Select(_ => coldSeats[rng.Next(coldSeats.Length)]).Distinct().ToArray();
    plan.Add(new Planned("general", $"fan-{rng.Next(userPool)}", NewKey(), seats));
}

// 5%: same-user stampede, 10 parallel single-seat requests per user (limit check).
for (var g = 0; g < limitGroups; g++)
{
    foreach (var seat in coldSeats.OrderBy(_ => rng.Next()).Take(10))
        plan.Add(new Planned("same_user", $"greedy-{g}", NewKey(), [seat]));
}

// 2%: identity spoofing, body claims to be someone else.
for (var i = 0; i < spoofCount; i++)
    plan.Add(new Planned("spoof", $"spoofer-{i}", NewKey(), [coldSeats[rng.Next(coldSeats.Length)]], SpoofAs: $"victim-{i}"));

// Remaining ~18%: retries. Two thirds exact retries (same key, same body), one third same key + different seats.
var originals = plan.ToArray();
var remaining = opts.Requests - plan.Count;
for (var i = 0; i < remaining; i++)
{
    var o = originals[rng.Next(originals.Length)];
    if (i % 3 != 2)
        plan.Add(o with { Kind = "retry_same" });
    else
    {
        // Must really be a different body: a random seat equal to the original would be a
        // legitimate same-body retry (correctly replayed), not a same-key/different-body case.
        string other;
        do other = coldSeats[rng.Next(coldSeats.Length)];
        while (o.Seats.Length == 1 && o.Seats[0] == other);
        plan.Add(o with { Kind = "retry_diff", Seats = [other] });
    }
}

var shuffled = plan.OrderBy(_ => rng.Next()).ToArray();

// ---- 3. Fire everything at once, sampling the invariant while it runs ---------------------------
var results = new Result[shuffled.Length];
var gate = new SemaphoreSlim(opts.Concurrency);
var start = new TaskCompletionSource();
var samplerCts = new CancellationTokenSource();
var sampler = Task.Run(() => SampleInvariant(samplerCts.Token));

var tasks = shuffled.Select((p, i) => Task.Run(async () =>
{
    await start.Task;
    await gate.WaitAsync();
    try { results[i] = await Reserve(p); }
    finally { gate.Release(); }
})).ToArray();

Console.WriteLine();
Console.WriteLine($"Firing {shuffled.Length:N0} reservations ...");
var burstWatch = Stopwatch.StartNew();
start.SetResult();
await Task.WhenAll(tasks);
burstWatch.Stop();
samplerCts.Cancel();
var samples = await sampler;

// ---- 4. Outcome distribution --------------------------------------------------------------------
var dist = new SortedDictionary<string, int>();
foreach (var r in results) dist[r.Outcome] = dist.GetValueOrDefault(r.Outcome) + 1;
var lat = results.Select(r => r.LatencyMs).OrderBy(x => x).ToArray();

Console.WriteLine();
Console.WriteLine("Outcome distribution");
Console.WriteLine("--------------------");
foreach (var key in new[] { "confirmed", "idempotent_replay", "seat_taken", "per_user_limit", "idempotency_conflict", "other_4xx", "5xx", "transport_error" })
    Console.WriteLine($"  {key,-22}{dist.GetValueOrDefault(key),8:N0}");
Console.WriteLine($"  {"total",-22}{results.Length,8:N0}");
Console.WriteLine();
Console.WriteLine($"Duration          {burstWatch.Elapsed.TotalSeconds:F2}s  ({results.Length / burstWatch.Elapsed.TotalSeconds:N0} req/s)");
Console.WriteLine($"Latency (client)  p50 {Pct(lat, 50):N0} ms   p95 {Pct(lat, 95):N0} ms   p99 {Pct(lat, 99):N0} ms   max {lat[^1]:N0} ms");
Console.WriteLine();
Console.WriteLine("By scenario");
foreach (var g in results.GroupBy(r => r.Plan.Kind).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key,-12} " + string.Join("  ", g.GroupBy(r => r.Outcome).OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Count()}")));

// ---- 5. Verify ----------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Checks");
Console.WriteLine("------");

var created = results.Where(r => r.Status == 201 && !r.Replayed).ToList();
var allSuccess = results.Where(r => r.Status == 201).ToList();

report.Check("zero 5xx", dist.GetValueOrDefault("5xx") == 0, $"{dist.GetValueOrDefault("5xx")} responses");
report.Check("zero transport errors", dist.GetValueOrDefault("transport_error") == 0, $"{dist.GetValueOrDefault("transport_error")} errors");

foreach (var seat in hotSeats)
{
    var winners = created.Count(r => r.Seats.Contains(seat));
    var losers = results.Where(r => r.Plan.Kind == "hot" && r.Plan.Seats[0] == seat && r.Status != 201).ToList();
    var clean = losers.All(r => r.Status == 409);
    report.Check($"hot seat {seat}: exactly one 201", winners == 1 && clean,
        $"{winners} winner(s), {losers.Count} declined ({losers.Count(r => r.Status == 409)} x 409)");
}

var seatToReservations = new Dictionary<string, HashSet<string>>();
foreach (var r in allSuccess)
    foreach (var s in r.Seats)
        (seatToReservations.TryGetValue(s, out var set) ? set : seatToReservations[s] = []).Add(r.ReservationId!);
var doubleSold = seatToReservations.Where(kv => kv.Value.Count > 1).ToList();
report.Check("no seat granted to two reservations (responses)", doubleSold.Count == 0,
    doubleSold.Count == 0 ? $"{seatToReservations.Count:N0} seats granted once each" : $"{doubleSold.Count} seats: {string.Join(",", doubleSold.Take(5).Select(k => k.Key))}");

var keyGroups = allSuccess.GroupBy(r => (r.Plan.User, r.Plan.Key)).ToList();
// (a) a key never maps to two different reservations;
// (b) a 201 always carries exactly the seats that request asked for — a same-key/different-body
//     request must get 409, never someone's earlier reservation for other seats.
var multiReservationKeys = keyGroups.Where(g => g.Select(r => r.ReservationId).Distinct().Count() > 1).ToList();
var wrongBody = allSuccess.Where(r => !r.Plan.Seats.Order(StringComparer.Ordinal).SequenceEqual(r.Seats)).ToList();
var idemExamples = multiReservationKeys.Take(3).Select(g => $"key {g.Key.Key} -> {string.Join("/", g.Select(r => r.ReservationId).Distinct())}")
    .Concat(wrongBody.Take(3).Select(r => $"asked [{string.Join(",", r.Plan.Seats)}] got [{string.Join(",", r.Seats)}]"));
report.Check("idempotency: one reservation per key", multiReservationKeys.Count == 0 && wrongBody.Count == 0,
    multiReservationKeys.Count == 0 && wrongBody.Count == 0
        ? $"{keyGroups.Count:N0} keys succeeded, {results.Count(r => r.Replayed):N0} replays, {dist.GetValueOrDefault("idempotency_conflict"):N0} same-key/different-body rejected"
        : $"{multiReservationKeys.Count} keys with >1 reservation, {wrongBody.Count} 201s for a different body: {string.Join("; ", idemExamples)}");

report.Check("identity: every 201 belongs to the token's user", allSuccess.All(r => r.UserId == r.Plan.User),
    $"{results.Count(r => r.Plan.SpoofAs is not null && r.Status == 201)} spoofed bodies booked as the caller");

var state = await GetState();
var invariantOk = state.Available + state.Held + state.Confirmed == state.Total;
report.Check("final reconciliation: available + held + confirmed == total", invariantOk,
    $"{state.Available} + {state.Held} + {state.Confirmed} = {state.Available + state.Held + state.Confirmed} (total {state.Total})");
report.Check("reconciliation held during the burst", samples.Violations == 0 && samples.Count > 0,
    $"{samples.Count} snapshots, {samples.Violations} violations");

var expectedConfirmed = created.Sum(r => r.Seats.Length);
report.Check("confirmed seats == seats in successful reservations", state.Confirmed == expectedConfirmed,
    $"state {state.Confirmed}, responses {expectedConfirmed}");

var ownerMismatch = created.SelectMany(r => r.Seats.Select(s => (s, r.UserId))).Count(x => state.Owners.GetValueOrDefault(x.s) != x.UserId);
report.Check("every granted seat is owned by the user it was granted to", ownerMismatch == 0, $"{ownerMismatch} mismatches");

var perUser = state.Owners.Values.GroupBy(u => u).Select(g => (User: g.Key, Count: g.Count())).ToList();
var maxPerUser = perUser.Count == 0 ? 0 : perUser.Max(x => x.Count);
var greedy = perUser.Where(x => x.User.StartsWith("greedy-")).ToList();
report.Check($"per-user limit ({opts.Limit}) holds", maxPerUser <= opts.Limit,
    $"max seats held by one user = {maxPerUser}; same-user stampede users hold {string.Join(",", greedy.Select(g => g.Count).Distinct().OrderBy(x => x))}");
report.Check("no seat owned by a spoofed identity", !state.Owners.Values.Any(u => u.StartsWith("victim-")), "victims own 0 seats");

// Metrics must agree with what the API did and with the state endpoint.
var metricsAfter = await ScrapeMetrics();
var confirmedDelta = metricsAfter.Get("reservations_confirmed_total") - metricsBefore.Get("reservations_confirmed_total");
var takenDelta = metricsAfter.Get("reservations_declined_total{reason=\"seat_taken\"}") - metricsBefore.Get("reservations_declined_total{reason=\"seat_taken\"}");
var replayDelta = metricsAfter.Get("reservations_declined_total{reason=\"idempotent_replay\"}") - metricsBefore.Get("reservations_declined_total{reason=\"idempotent_replay\"}");
var showGauge = metricsAfter.Get($"show_seats{{show_id=\"{showId}\",status=\"confirmed\"}}");
report.Check("metrics: show_seats gauge == API state", (long)showGauge == state.Confirmed, $"gauge {showGauge}, state {state.Confirmed}");
report.Check("metrics: counters match observed outcomes", confirmedDelta == created.Count && takenDelta == dist.GetValueOrDefault("seat_taken") && replayDelta == dist.GetValueOrDefault("idempotent_replay"),
    $"confirmed {confirmedDelta} vs {created.Count}, seat_taken {takenDelta} vs {dist.GetValueOrDefault("seat_taken")}, replay {replayDelta} vs {dist.GetValueOrDefault("idempotent_replay")} (exact only if no one else is hitting the service)",
    warnOnly: true);
var drift = metricsAfter.Entries.Where(kv => kv.Key.StartsWith("reconciliation_drift")).ToList();
report.Check("metrics: reconciliation_drift == 0", drift.Count > 0 && drift.All(kv => kv.Value == 0),
    string.Join(", ", drift.Select(kv => $"{kv.Key[(kv.Key.IndexOf('"') + 1)..kv.Key.LastIndexOf('"')]}={kv.Value}")));

// ---- 6. Cancellation: only the owner, and the seat becomes cleanly re-bookable ------------------
var toCancel = created.Where(r => r.Plan.Kind == "hot").Take(Math.Min(opts.HotSeats, 5)).ToList();
var forbidden = 0; var cancelledOk = 0; var rebooked = 0;
foreach (var r in toCancel)
{
    var thief = await Send(HttpMethod.Post, $"reservations/{r.ReservationId}/cancel", "thief-1", null);
    if (thief.Status == 403) forbidden++;
    var own = await Send(HttpMethod.Post, $"reservations/{r.ReservationId}/cancel", r.Plan.User, null);
    if (own.Status == 200) cancelledOk++;
    var again = await Send(HttpMethod.Post, $"shows/{showId}/reserve", $"second-chance-{r.Seats[0]}",
        new JsonObject { ["seats"] = new JsonArray(r.Seats.Select(s => (JsonNode)s!).ToArray()), ["idempotency_key"] = NewKey() });
    if (again.Status == 201) rebooked++;
}
report.Check("cancel: non-owner gets 403", forbidden == toCancel.Count, $"{forbidden}/{toCancel.Count}");
report.Check("cancel: owner cancels, seat re-bookable", cancelledOk == toCancel.Count && rebooked == toCancel.Count,
    $"cancelled {cancelledOk}/{toCancel.Count}, re-booked {rebooked}/{toCancel.Count}");
var finalState = await GetState();
report.Check("reconciliation after cancel + re-book", finalState.Available + finalState.Held + finalState.Confirmed == finalState.Total && finalState.Confirmed == state.Confirmed,
    $"{finalState.Available} + {finalState.Held} + {finalState.Confirmed} = {finalState.Total}");

Console.WriteLine();
Console.WriteLine(report.Failed == 0 ? $"RESULT: PASS ({report.Passed} checks)" : $"RESULT: FAIL ({report.Failed} failed, {report.Passed} passed)");

Directory.CreateDirectory("burst-results");
var summaryPath = Path.Combine("burst-results", $"burst-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(new
{
    target = opts.BaseUrl, show_id = showId, requests = results.Length, concurrency = opts.Concurrency,
    duration_s = burstWatch.Elapsed.TotalSeconds, distribution = dist,
    latency_ms = new { p50 = Pct(lat, 50), p95 = Pct(lat, 95), p99 = Pct(lat, 99), max = lat[^1] },
    checks = report.Items,
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Summary written to {summaryPath}");
return report.Failed == 0 ? 0 : 1;

// ================================================================================================

async Task<Result> Reserve(Planned p)
{
    var body = new JsonObject
    {
        ["seats"] = new JsonArray(p.Seats.Select(s => (JsonNode)s!).ToArray()),
        ["idempotency_key"] = p.Key,
    };
    if (p.SpoofAs is not null) body["user_id"] = p.SpoofAs;

    var sw = Stopwatch.StartNew();
    try
    {
        var res = await Send(HttpMethod.Post, $"shows/{showId}/reserve", p.User, body);
        sw.Stop();
        var code = res.Body?["error"]?["code"]?.GetValue<string>();
        var outcome = res.Status switch
        {
            201 when res.Replayed => "idempotent_replay",
            201 => "confirmed",
            409 when code == "SEAT_UNAVAILABLE" => "seat_taken",
            409 when code == "USER_LIMIT_EXCEEDED" => "per_user_limit",
            409 when code == "IDEMPOTENCY_KEY_REUSED" => "idempotency_conflict",
            >= 500 => "5xx",
            _ => "other_4xx",
        };
        string[] seats = res.Status == 201 ? res.Body!["seats"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray() : [];
        return new Result(p, res.Status, outcome, res.Replayed, res.Body?["reservation_id"]?.GetValue<string>(),
            res.Body?["user_id"]?.GetValue<string>(), seats, sw.Elapsed.TotalMilliseconds);
    }
    catch (Exception)
    {
        return new Result(p, 0, "transport_error", false, null, null, [], sw.Elapsed.TotalMilliseconds);
    }
}

Task<Response> Send(HttpMethod method, string path, string? token, JsonNode? body) => SendWith(http, method, path, token, body);

async Task<Response> SendWith(HttpClient client, HttpMethod method, string path, string? token, JsonNode? body)
{
    using var req = new HttpRequestMessage(method, path);
    if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    using var res = await client.SendAsync(req);
    var raw = await res.Content.ReadAsStringAsync();
    JsonNode? parsed = null;
    try { parsed = raw.Length > 0 ? JsonNode.Parse(raw) : null; } catch (JsonException) { }
    return new Response((int)res.StatusCode, parsed, raw, res.Headers.Contains("Idempotent-Replayed"));
}

async Task<ShowState> GetState(HttpClient? client = null)
{
    var res = await SendWith(client ?? http, HttpMethod.Get, $"shows/{showId}", opts.AdminToken, null);
    var b = res.Body!;
    var owners = b["seats"]!.AsArray()
        .Where(s => s!["status"]!.GetValue<string>() == "confirmed")
        .ToDictionary(s => s!["seat"]!.GetValue<string>(), s => s!["user_id"]?.GetValue<string>() ?? "?");
    return new ShowState(b["total_seats"]!.GetValue<int>(), b["available_seats"]!.GetValue<int>(),
        b["held_seats"]!.GetValue<int>(), b["confirmed_seats"]!.GetValue<int>(), owners);
}

async Task<(int Count, int Violations)> SampleInvariant(CancellationToken ct)
{
    int count = 0, violations = 0;
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var s = await GetState(probe);
            count++;
            if (s.Available + s.Held + s.Confirmed != s.Total || s.Owners.Count != s.Confirmed) violations++;
        }
        catch (Exception) { /* the burst may saturate the client; skip this sample */ }
        try { await Task.Delay(250, ct); } catch (OperationCanceledException) { }
    }
    return (count, violations);
}

async Task<MetricsSnapshot> ScrapeMetrics()
{
    try
    {
        var text = await http.GetStringAsync("metrics");
        var entries = new Dictionary<string, double>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var idx = line.LastIndexOf(' ');
            if (idx > 0 && double.TryParse(line[(idx + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                entries[line[..idx]] = v;
        }
        return new MetricsSnapshot(entries);
    }
    catch (Exception) { return new MetricsSnapshot([]); }
}

static double Pct(double[] sorted, int p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1)];

sealed record Planned(string Kind, string User, string Key, string[] Seats, string? SpoofAs = null);

sealed record Result(Planned Plan, int Status, string Outcome, bool Replayed, string? ReservationId, string? UserId, string[] Seats, double LatencyMs);

sealed record Response(int Status, JsonNode? Body, string Raw, bool Replayed);

sealed record ShowState(int Total, int Available, int Held, int Confirmed, Dictionary<string, string> Owners);

sealed record MetricsSnapshot(Dictionary<string, double> Entries)
{
    public double Get(string key) => Entries.GetValueOrDefault(key);
}

sealed class Report
{
    public List<object> Items { get; } = [];
    public int Passed { get; private set; }
    public int Failed { get; private set; }

    public void Check(string name, bool ok, string detail, bool warnOnly = false)
    {
        var tag = ok ? "PASS" : warnOnly ? "WARN" : "FAIL";
        if (ok) Passed++; else if (!warnOnly) Failed++;
        Items.Add(new { check = name, result = tag, detail });
        Console.WriteLine($"  [{tag}] {name} — {detail}");
    }
}

sealed record Options(string BaseUrl, int Requests, int Concurrency, int Seats, int HotSeats, string AdminToken, int Limit)
{
    public static Options? Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--")) return null;
        string Get(string name, string fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : Environment.GetEnvironmentVariable(name.TrimStart('-').Replace('-', '_').ToUpperInvariant()) ?? fallback;
        }
        return new Options(args[0],
            int.Parse(Get("--requests", "20000")),
            int.Parse(Get("--concurrency", "500")),
            int.Parse(Get("--seats", "2000")),
            int.Parse(Get("--hot-seats", "5")),
            Get("--admin-token", "admin"),
            int.Parse(Get("--limit", "4")));
    }
}
