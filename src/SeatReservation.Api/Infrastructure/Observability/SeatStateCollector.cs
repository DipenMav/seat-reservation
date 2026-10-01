using Prometheus;
using SeatReservation.Api.Infrastructure.Persistence;

namespace SeatReservation.Api.Infrastructure.Observability;

/// <summary>
/// Seat gauges are read from Postgres at scrape time (not tracked in memory), so they always
/// match what GET /shows/{id} reports and survive restarts / multiple instances.
///
/// The global numbers come from ONE SQL statement = one snapshot, which makes the
/// reconciliation_drift cross-checks meaningful: three independently maintained counts
/// (seat status, active reservation_seats, per-user counters) must agree to the unit.
/// </summary>
public sealed class SeatStateCollector(OpsDataSource ops, ILogger<SeatStateCollector> logger)
{
    private const int PerShowLimit = 20;

    private static readonly Gauge SeatsAvailable = Metrics.CreateGauge("seats_available", "Seats currently available (all shows).");
    private static readonly Gauge SeatsHeld = Metrics.CreateGauge("seats_held", "Seats currently held (all shows).");
    private static readonly Gauge SeatsConfirmed = Metrics.CreateGauge("seats_confirmed", "Seats currently confirmed (all shows).");
    private static readonly Gauge SeatsTotal = Metrics.CreateGauge("seats_total", "Seats that exist (all shows).");
    private static readonly Gauge Shows = Metrics.CreateGauge("shows_total", "Shows that exist.");

    private static readonly Gauge Drift = Metrics.CreateGauge("reconciliation_drift",
        "Absolute disagreement between independent seat counts. Anything other than 0 is a correctness bug.",
        "check");

    private static readonly Gauge ShowSeats = Metrics.CreateGauge("show_seats",
        $"Seats by status for the {PerShowLimit} most recently created shows.", "show_id", "status");

    private static readonly Counter CollectErrors = Metrics.CreateCounter(
        "metrics_db_collect_errors_total", "Failed attempts to read seat state for /metrics.");

    private static readonly Gauge LastSuccess = Metrics.CreateGauge(
        "metrics_db_collect_last_success_timestamp_seconds", "Unix time of the last successful seat-state read.");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _publishedShows = [];
    private DateTimeOffset _lastCollected = DateTimeOffset.MinValue;

    public void Register(CollectorRegistry registry) => registry.AddBeforeCollectCallback(CollectAsync);

    private async Task CollectAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return; // a scrape is already refreshing
        try
        {
            if (DateTimeOffset.UtcNow - _lastCollected < TimeSpan.FromSeconds(1)) return;
            await CollectGlobalAsync(ct);
            await CollectPerShowAsync(ct);
            _lastCollected = DateTimeOffset.UtcNow;
            LastSuccess.SetToCurrentTimeUtc();
        }
        catch (Exception ex)
        {
            // Keep the previous values; staleness is visible via the last-success timestamp.
            CollectErrors.Inc();
            logger.LogWarning("Seat-state metrics collection failed: {Error}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CollectGlobalAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT
              (SELECT count(*) FROM seats WHERE status = 'available'),
              (SELECT count(*) FROM seats WHERE status = 'held'),
              (SELECT count(*) FROM seats WHERE status = 'confirmed'),
              (SELECT count(*) FROM seats),
              (SELECT coalesce(sum(total_seats), 0) FROM shows),
              (SELECT count(*) FROM shows),
              (SELECT count(*) FROM reservation_seats rs
                 JOIN reservations r ON r.id = rs.reservation_id
                WHERE r.status = 'confirmed'),
              (SELECT coalesce(sum(reserved_count), 0) FROM show_user_limits)
            """;
        await using var cmd = ops.DataSource.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        long available = reader.GetInt64(0), held = reader.GetInt64(1), confirmed = reader.GetInt64(2);
        long seatRows = reader.GetInt64(3), declaredTotal = reader.GetInt64(4), shows = reader.GetInt64(5);
        long activeReservationSeats = reader.GetInt64(6), userCounted = reader.GetInt64(7);

        SeatsAvailable.Set(available);
        SeatsHeld.Set(held);
        SeatsConfirmed.Set(confirmed);
        SeatsTotal.Set(seatRows);
        Shows.Set(shows);

        Drift.WithLabels("status_sum_vs_total").Set(Math.Abs(available + held + confirmed - declaredTotal));
        Drift.WithLabels("confirmed_vs_reservation_seats").Set(Math.Abs(confirmed - activeReservationSeats));
        Drift.WithLabels("confirmed_vs_user_counts").Set(Math.Abs(confirmed - userCounted));
    }

    private async Task CollectPerShowAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT s.id,
                   count(*) FILTER (WHERE se.status = 'available'),
                   count(*) FILTER (WHERE se.status = 'held'),
                   count(*) FILTER (WHERE se.status = 'confirmed')
              FROM (SELECT id FROM shows ORDER BY created_at DESC LIMIT @limit) s
              JOIN seats se ON se.show_id = s.id
             GROUP BY s.id
            """;
        await using var cmd = ops.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("limit", PerShowLimit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var seen = new HashSet<string>();
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0).ToString();
            seen.Add(id);
            ShowSeats.WithLabels(id, "available").Set(reader.GetInt64(1));
            ShowSeats.WithLabels(id, "held").Set(reader.GetInt64(2));
            ShowSeats.WithLabels(id, "confirmed").Set(reader.GetInt64(3));
        }

        foreach (var stale in _publishedShows.Except(seen).ToList())
        {
            foreach (var status in new[] { "available", "held", "confirmed" })
                ShowSeats.RemoveLabelled(stale, status);
            _publishedShows.Remove(stale);
        }
        _publishedShows.UnionWith(seen);
    }
}
