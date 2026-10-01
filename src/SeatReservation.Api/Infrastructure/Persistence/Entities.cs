namespace SeatReservation.Api.Infrastructure.Persistence;

public static class SeatStatus
{
    public const string Available = "available";
    public const string Held = "held";
    public const string Confirmed = "confirmed";
}

public static class ReservationStatus
{
    public const string Confirmed = "confirmed";
    public const string Cancelled = "cancelled";
}

public sealed class Show
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public long PricePaise { get; set; }
    public int PerUserLimit { get; set; }

    // Fixed at creation. Reconciliation compares live seat counts against this,
    // so a lost or duplicated seat row shows up as drift.
    public int TotalSeats { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Seat
{
    public Guid Id { get; set; }
    public Guid ShowId { get; set; }
    public string SeatNumber { get; set; } = "";

    // Order the seat was listed in when the show was created (for display only).
    public int Position { get; set; }
    public string Status { get; set; } = SeatStatus.Available;

    // The reservation that currently owns this seat. NULL exactly when the seat is available
    // (enforced by a CHECK constraint). A seat row can only point at one reservation, so a
    // double-sell is unrepresentable, not just unlikely.
    public Guid? ReservationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Reservation
{
    public Guid Id { get; set; }
    public Guid ShowId { get; set; }
    public string UserId { get; set; } = "";
    public long AmountPaise { get; set; }
    public string Status { get; set; } = ReservationStatus.Confirmed;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

public sealed class ReservationSeat
{
    public Guid ReservationId { get; set; }
    public Guid SeatId { get; set; }
}

public sealed class ShowUserLimit
{
    public Guid ShowId { get; set; }
    public string UserId { get; set; } = "";
    public int ReservedCount { get; set; }
}

public sealed class IdempotencyKey
{
    public Guid Id { get; set; }
    public Guid ShowId { get; set; }
    public string UserId { get; set; } = "";
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid ReservationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
