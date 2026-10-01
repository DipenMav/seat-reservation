using Prometheus;

namespace SeatReservation.Api.Infrastructure.Observability;

public static class DeclineReason
{
    public const string SeatTaken = "seat_taken";
    public const string PerUserLimit = "per_user_limit";
    public const string IdempotentReplay = "idempotent_replay";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string InvalidRequest = "invalid_request";
    public const string ShowNotFound = "show_not_found";

    public static readonly string[] All =
        [SeatTaken, PerUserLimit, IdempotentReplay, IdempotencyConflict, InvalidRequest, ShowNotFound];
}

/// <summary>
/// Event counters. Labels are low-cardinality only (never user / reservation / idempotency key).
/// Seat-unit counters let you reconcile metrics with state:
///   seats_reserved_total - seats_released_total == change in seats_confirmed since process start.
/// </summary>
public static class ReservationMetrics
{
    public static readonly Counter Confirmed = Metrics.CreateCounter(
        "reservations_confirmed_total", "Reservations committed (new, not idempotent replays).");

    public static readonly Counter Declined = Metrics.CreateCounter(
        "reservations_declined_total", "Reserve requests that did not create a new reservation, by reason.",
        "reason");

    public static readonly Counter Cancelled = Metrics.CreateCounter(
        "reservations_cancelled_total", "Reservations cancelled by their owner.");

    public static readonly Counter SeatsReserved = Metrics.CreateCounter(
        "seats_reserved_total", "Seats moved available -> confirmed.");

    public static readonly Counter SeatsReleased = Metrics.CreateCounter(
        "seats_released_total", "Seats moved confirmed -> available by cancellation.");

    public static readonly Counter TransactionRetries = Metrics.CreateCounter(
        "reservation_transaction_retries_total", "Reserve/cancel transactions retried after a deadlock or serialization failure.");

    public static readonly Histogram Duration = Metrics.CreateHistogram(
        "reservation_duration_seconds", "Server-side latency of reserve requests, by outcome.",
        new HistogramConfiguration
        {
            LabelNames = ["outcome"],
            Buckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30],
        });

    public static void Initialize()
    {
        // Publish zero-valued series up front so dashboards and rate() work before the first decline.
        foreach (var reason in DeclineReason.All) Declined.WithLabels(reason);
        foreach (var outcome in new[] { "confirmed" }.Concat(DeclineReason.All)) Duration.WithLabels(outcome);
    }
}
