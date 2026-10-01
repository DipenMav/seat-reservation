using System.Text.Json.Serialization;

namespace SeatReservation.Api.Features.Shows;

public sealed record SeatState(
    string Seat,
    string Status,
    // Owner details are only included for admin callers.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ReservationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserId = null);

public sealed record ShowStateResponse(
    Guid Id,
    string Name,
    long PricePaise,
    int PerUserLimit,
    int TotalSeats,
    int AvailableSeats,
    int HeldSeats,
    int ConfirmedSeats,
    // available + held + confirmed == total_seats, computed from the same snapshot of seat rows.
    bool Reconciled,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SeatState> Seats)
{
    public static ShowStateResponse From(
        Guid id, string name, long pricePaise, int perUserLimit, int totalSeats, DateTimeOffset createdAt,
        IReadOnlyList<SeatState> seats)
    {
        int available = 0, held = 0, confirmed = 0;
        foreach (var s in seats)
        {
            switch (s.Status)
            {
                case "available": available++; break;
                case "held": held++; break;
                case "confirmed": confirmed++; break;
            }
        }

        return new ShowStateResponse(id, name, pricePaise, perUserLimit, totalSeats, available, held, confirmed,
            Reconciled: available + held + confirmed == totalSeats && seats.Count == totalSeats,
            createdAt, seats);
    }
}
