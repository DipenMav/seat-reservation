using Npgsql;
using SeatReservation.Api.Common;
using SeatReservation.Api.Infrastructure.Observability;

namespace SeatReservation.Api.Features.Reservations.Cancel;

public abstract record CancelOutcome;

public sealed record Cancelled(ReservationDto Reservation, bool AlreadyCancelled) : CancelOutcome;

public sealed record CancelNotFound : CancelOutcome;

public sealed record CancelForbidden : CancelOutcome;

/// <summary>
/// Cancel = one transaction:
///   1. UPDATE reservations SET cancelled WHERE id AND user_id = caller AND status = 'confirmed'
///      — ownership and "still confirmed" are part of the guard, so a non-owner can never change
///      anything and two concurrent cancels can't both release (the second sees 0 rows).
///   2. Decrement the caller's show_user_limits counter.
///   3. Release seats WHERE reservation_id = this reservation AND status = 'confirmed', in sorted
///      order. Guarding on reservation_id means a release can only ever touch seats this
///      reservation still owns — it cannot resurrect a seat that now belongs to someone else.
/// Lock order reservation → user counter → seats; reserve takes user counter → seats, so no cycle.
/// </summary>
public sealed class CancelHandler(NpgsqlDataSource db)
{
    private const int MaxAttempts = 3;

    public async Task<CancelOutcome> HandleAsync(Guid reservationId, string userId)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CancelInTransactionAsync(reservationId, userId, CancellationToken.None);
            }
            catch (PostgresException ex) when (Sql.IsTransient(ex) && attempt < MaxAttempts)
            {
                ReservationMetrics.TransactionRetries.Inc();
            }
        }
    }

    private async Task<CancelOutcome> CancelInTransactionAsync(Guid reservationId, string userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid showId;
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE reservations SET status = 'cancelled', cancelled_at = $3
             WHERE id = $1 AND user_id = $2 AND status = 'confirmed'
            RETURNING show_id
            """, conn, tx))
        {
            cmd.Parameters.Add(Sql.P(reservationId));
            cmd.Parameters.Add(Sql.P(userId));
            cmd.Parameters.Add(Sql.P(now));
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is not Guid id)
            {
                await tx.RollbackAsync(ct);
                return await ClassifyNoopAsync(conn, reservationId, userId, ct);
            }
            showId = id;
        }

        var seatIds = new List<Guid>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT s.id FROM reservation_seats rs JOIN seats s ON s.id = rs.seat_id
             WHERE rs.reservation_id = $1 ORDER BY s.seat_number
            """, conn, tx))
        {
            cmd.Parameters.Add(Sql.P(reservationId));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) seatIds.Add(reader.GetGuid(0));
        }

        await using (var release = new NpgsqlBatch(conn, tx))
        {
            release.BatchCommands.Add(Sql.Command(
                """
                UPDATE show_user_limits SET reserved_count = reserved_count - $3
                 WHERE show_id = $1 AND user_id = $2
                """,
                Sql.P(showId), Sql.P(userId), Sql.P(seatIds.Count)));
            foreach (var seatId in seatIds)
            {
                release.BatchCommands.Add(Sql.Command(
                    """
                    UPDATE seats SET status = 'available', reservation_id = NULL, updated_at = $3
                     WHERE id = $1 AND reservation_id = $2 AND status = 'confirmed'
                    """,
                    Sql.P(seatId), Sql.P(reservationId), Sql.P(now)));
            }
            await release.ExecuteNonQueryAsync(ct);

            // A confirmed reservation must own all of its seats and have counted them. If not,
            // state is already inconsistent: refuse to make it worse and surface it loudly.
            if (release.BatchCommands.Cast<NpgsqlBatchCommand>().Any(c => c.RecordsAffected != 1))
                throw new InvalidOperationException($"Invariant violated while cancelling reservation {reservationId}.");
        }

        await tx.CommitAsync(ct);

        ReservationMetrics.Cancelled.Inc();
        ReservationMetrics.SeatsReleased.Inc(seatIds.Count);

        var dto = await ReservationQueries.LoadAsync(conn, reservationId, ct);
        return new Cancelled(dto!, AlreadyCancelled: false);
    }

    private static async Task<CancelOutcome> ClassifyNoopAsync(
        NpgsqlConnection conn, Guid reservationId, string userId, CancellationToken ct)
    {
        var existing = await ReservationQueries.LoadAsync(conn, reservationId, ct);
        if (existing is null) return new CancelNotFound();
        if (existing.UserId != userId) return new CancelForbidden();
        return new Cancelled(existing, AlreadyCancelled: true); // repeat cancel is a no-op success
    }
}
