using System.Text.Json.Serialization;
using Npgsql;

namespace SeatReservation.Api.Features.Reservations;

public sealed record ReservationDto(
    Guid ReservationId,
    Guid ShowId,
    string UserId,
    IReadOnlyList<string> Seats,
    long AmountPaise,
    string Status,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CancelledAt = null);

public static class ReservationQueries
{
    /// <summary>Loads a reservation with its seats (sorted), or null if it does not exist.</summary>
    public static async Task<ReservationDto?> LoadAsync(NpgsqlConnection conn, Guid reservationId, CancellationToken ct)
    {
        const string sql = """
            SELECT r.id, r.show_id, r.user_id, r.amount_paise, r.status, r.created_at, r.cancelled_at,
                   coalesce(array_agg(s.seat_number ORDER BY s.seat_number) FILTER (WHERE s.id IS NOT NULL), '{}')
              FROM reservations r
              LEFT JOIN reservation_seats rs ON rs.reservation_id = r.id
              LEFT JOIN seats s ON s.id = rs.seat_id
             WHERE r.id = $1
             GROUP BY r.id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn) { Parameters = { new NpgsqlParameter<Guid> { TypedValue = reservationId } } };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ReservationDto(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetFieldValue<string[]>(7),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
    }
}
