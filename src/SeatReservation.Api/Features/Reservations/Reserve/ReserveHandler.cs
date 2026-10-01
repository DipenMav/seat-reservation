using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using SeatReservation.Api.Common;
using SeatReservation.Api.Infrastructure.Observability;

namespace SeatReservation.Api.Features.Reservations.Reserve;

public abstract record ReserveOutcome(int Attempts);

public sealed record ReserveCreated(ReservationDto Reservation, int Attempts) : ReserveOutcome(Attempts);

public sealed record ReserveReplayed(ReservationDto Reservation) : ReserveOutcome(0);

public sealed record ReserveDeclined(int Status, string Code, string Reason, string Message, int Attempts = 0)
    : ReserveOutcome(Attempts);

/// <summary>
/// Reserve = one READ COMMITTED transaction in which every decision is an atomic, guarded write:
///
///   1. INSERT reservation row                      (new row, uncontended)
///   2. INSERT idempotency key ON CONFLICT DO NOTHING  — UNIQUE(show, user, key) makes the key
///      claimable exactly once; a concurrent duplicate blocks on the unique index until the
///      first transaction commits (→ 0 rows: replay) or rolls back (→ it claims the key).
///   3. UPSERT show_user_limits … WHERE reserved_count + n <= limit
///      — the per-user counter row is the serialization point for one user's concurrent
///      requests; 0 rows affected = over the limit.
///   4. UPDATE seats SET confirmed … WHERE status = 'available', one statement per seat in
///      ordinal seat order — the row lock + re-check of the WHERE clause after a concurrent
///      commit means exactly one transaction can flip a seat; 0 rows = seat taken. Every
///      transaction locks seats in the same order, so multi-seat requests cannot deadlock.
///   5. INSERT reservation_seats, COMMIT.
///
/// Any 0-row step rolls the whole transaction back: all-or-nothing, nothing to clean up.
/// Lock order is always idempotency key → user counter → seats (sorted); cancel uses
/// reservation → user counter → seats, so there is no cycle between them.
///
/// A read-only "preflight" (single statement, single snapshot) runs first to decline the
/// obvious losers of a hot-seat storm without opening a write transaction. It can only decline
/// or route to the transaction; it never grants anything.
/// </summary>
public sealed class ReserveHandler(NpgsqlDataSource db)
{
    private const int MaxAttempts = 3;

    public async Task<ReserveOutcome> HandleAsync(
        Guid showId, string userId, IReadOnlyList<string> sortedSeats, string idempotencyKey, CancellationToken ct)
    {
        var requestHash = RequestHash(sortedSeats);

        var pre = await PreflightAsync(showId, userId, sortedSeats, idempotencyKey, ct);
        if (pre is null)
            return Declined.ShowNotFound();
        if (pre.IdempotencyHash is not null)
            return await ResolveExistingKeyAsync(pre.IdempotencyHash, pre.IdempotencyReservationId!.Value, requestHash, ct);
        if (pre.KnownSeats < sortedSeats.Count)
            return Declined.UnknownSeat();
        if (pre.ReservedCount + sortedSeats.Count > pre.PerUserLimit)
            return Declined.PerUserLimit(pre.PerUserLimit);
        if (pre.AvailableSeats < sortedSeats.Count)
            return Declined.SeatTaken();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Deliberately not tied to the HTTP request's cancellation: once we start, finish,
                // so a client disconnect can't leave the outcome ambiguous. A retry with the same
                // idempotency key will find the result.
                return await ReserveInTransactionAsync(showId, userId, sortedSeats, idempotencyKey, requestHash,
                    pre.PricePaise, pre.PerUserLimit, attempt, CancellationToken.None);
            }
            catch (PostgresException ex) when (Sql.IsTransient(ex) && attempt < MaxAttempts)
            {
                ReservationMetrics.TransactionRetries.Inc();
            }
        }
    }

    private async Task<ReserveOutcome> ReserveInTransactionAsync(
        Guid showId, string userId, IReadOnlyList<string> seats, string key, string requestHash,
        long pricePaise, int perUserLimit, int attempt, CancellationToken ct)
    {
        var reservationId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var amount = checked(pricePaise * seats.Count);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct); // READ COMMITTED

        // Steps 1-3: reservation row, idempotency claim, per-user capacity.
        await using (var claim = new NpgsqlBatch(conn, tx))
        {
            claim.BatchCommands.Add(Sql.Command(
                """
                INSERT INTO reservations (id, show_id, user_id, amount_paise, status, created_at)
                VALUES ($1, $2, $3, $4, 'confirmed', $5)
                """,
                Sql.P(reservationId), Sql.P(showId), Sql.P(userId), Sql.P(amount), Sql.P(now)));
            claim.BatchCommands.Add(Sql.Command(
                """
                INSERT INTO idempotency_keys (id, show_id, user_id, idempotency_key, request_hash, reservation_id, created_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (show_id, user_id, idempotency_key) DO NOTHING
                """,
                Sql.P(Guid.CreateVersion7()), Sql.P(showId), Sql.P(userId), Sql.P(key), Sql.P(requestHash),
                Sql.P(reservationId), Sql.P(now)));
            claim.BatchCommands.Add(Sql.Command(
                """
                INSERT INTO show_user_limits (show_id, user_id, reserved_count)
                VALUES ($1, $2, $3)
                ON CONFLICT (show_id, user_id) DO UPDATE
                   SET reserved_count = show_user_limits.reserved_count + EXCLUDED.reserved_count
                 WHERE show_user_limits.reserved_count + EXCLUDED.reserved_count <= $4
                """,
                Sql.P(showId), Sql.P(userId), Sql.P(seats.Count), Sql.P(perUserLimit)));

            await claim.ExecuteNonQueryAsync(ct);

            if (claim.BatchCommands[1].RecordsAffected == 0)
            {
                // Another request with this key committed first (we waited for it on the unique index).
                await tx.RollbackAsync(ct);
                return await ResolveKeyAfterConflictAsync(showId, userId, key, requestHash, attempt, ct);
            }

            if (claim.BatchCommands[2].RecordsAffected == 0)
            {
                await tx.RollbackAsync(ct);
                return Declined.PerUserLimit(perUserLimit) with { Attempts = attempt };
            }
        }

        // Step 4-5: flip each seat available -> confirmed in sorted order, then link them.
        await using (var acquire = new NpgsqlBatch(conn, tx))
        {
            foreach (var seat in seats)
            {
                acquire.BatchCommands.Add(Sql.Command(
                    """
                    UPDATE seats
                       SET status = 'confirmed', reservation_id = $1, updated_at = $2
                     WHERE show_id = $3 AND seat_number = $4 AND status = 'available'
                    """,
                    Sql.P(reservationId), Sql.P(now), Sql.P(showId), Sql.P(seat)));
            }
            acquire.BatchCommands.Add(Sql.Command(
                "INSERT INTO reservation_seats (reservation_id, seat_id) SELECT $1, id FROM seats WHERE reservation_id = $1",
                Sql.P(reservationId)));

            await acquire.ExecuteNonQueryAsync(ct);

            for (var i = 0; i < seats.Count; i++)
            {
                if (acquire.BatchCommands[i].RecordsAffected != 1)
                {
                    await tx.RollbackAsync(ct);
                    return Declined.SeatTaken() with { Attempts = attempt };
                }
            }
        }

        await tx.CommitAsync(ct);

        ReservationMetrics.Confirmed.Inc();
        ReservationMetrics.SeatsReserved.Inc(seats.Count);

        return new ReserveCreated(
            new ReservationDto(reservationId, showId, userId, seats, amount, "confirmed", now), attempt);
    }

    private async Task<ReserveOutcome> ResolveKeyAfterConflictAsync(
        Guid showId, string userId, string key, string requestHash, int attempt, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT request_hash, reservation_id FROM idempotency_keys WHERE show_id = $1 AND user_id = $2 AND idempotency_key = $3");
        cmd.Parameters.Add(Sql.P(showId));
        cmd.Parameters.Add(Sql.P(userId));
        cmd.Parameters.Add(Sql.P(key));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Idempotency insert conflicted but no committed key was found.");

        var storedHash = reader.GetString(0);
        var reservationId = reader.GetGuid(1);
        await reader.DisposeAsync();
        return await ResolveExistingKeyAsync(storedHash, reservationId, requestHash, ct) with { Attempts = attempt };
    }

    private async Task<ReserveOutcome> ResolveExistingKeyAsync(
        string storedHash, Guid reservationId, string requestHash, CancellationToken ct)
    {
        if (!string.Equals(storedHash, requestHash, StringComparison.Ordinal))
            return Declined.IdempotencyConflict();

        await using var conn = await db.OpenConnectionAsync(ct);
        var reservation = await ReservationQueries.LoadAsync(conn, reservationId, ct)
            ?? throw new InvalidOperationException($"Idempotency key points at missing reservation {reservationId}.");
        return new ReserveReplayed(reservation);
    }

    private sealed record Preflight(
        long PricePaise, int PerUserLimit, string? IdempotencyHash, Guid? IdempotencyReservationId,
        int ReservedCount, int KnownSeats, int AvailableSeats);

    /// <summary>
    /// One statement → one MVCC snapshot. That matters: if a concurrent request with the same key
    /// commits, we see both its idempotency row and its seat change, or neither — never "seat taken"
    /// without "key exists", which would wrongly decline a retry of the winning request.
    /// </summary>
    private async Task<Preflight?> PreflightAsync(
        Guid showId, string userId, IReadOnlyList<string> seats, string key, CancellationToken ct)
    {
        const string sql = """
            SELECT sh.price_paise, sh.per_user_limit,
                   ik.request_hash, ik.reservation_id,
                   coalesce(ul.reserved_count, 0),
                   st.known, st.available
              FROM shows sh
              LEFT JOIN idempotency_keys ik
                     ON ik.show_id = sh.id AND ik.user_id = $2 AND ik.idempotency_key = $4
              LEFT JOIN show_user_limits ul
                     ON ul.show_id = sh.id AND ul.user_id = $2
             CROSS JOIN LATERAL (
                   SELECT count(*)::int AS known,
                          (count(*) FILTER (WHERE se.status = 'available'))::int AS available
                     FROM seats se
                    WHERE se.show_id = sh.id AND se.seat_number = ANY($3)) st
             WHERE sh.id = $1
            """;
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.Add(Sql.P(showId));
        cmd.Parameters.Add(Sql.P(userId));
        cmd.Parameters.Add(new NpgsqlParameter { Value = seats.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(Sql.P(key));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new Preflight(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6));
    }

    /// <summary>
    /// Fingerprint of the logical request: the normalized (trimmed, de-duplicated, ordinal-sorted)
    /// seat set. ["A13","A12"] and ["A12","A13"] are the same request. Show and user are already
    /// part of the idempotency key's scope.
    /// </summary>
    public static string RequestHash(IReadOnlyList<string> sortedSeats) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("v1|" + string.Join('\n', sortedSeats))));

    private static class Declined
    {
        public static ReserveDeclined ShowNotFound() => new(404, ErrorCodes.ShowNotFound, DeclineReason.ShowNotFound,
            "The requested show does not exist.");

        public static ReserveDeclined UnknownSeat() => new(400, ErrorCodes.UnknownSeat, DeclineReason.InvalidRequest,
            "One or more requested seats do not exist in this show.");

        public static ReserveDeclined SeatTaken() => new(409, ErrorCodes.SeatUnavailable, DeclineReason.SeatTaken,
            "One or more requested seats are no longer available.");

        public static ReserveDeclined PerUserLimit(int limit) => new(409, ErrorCodes.UserLimitExceeded, DeclineReason.PerUserLimit,
            $"The reservation exceeds the per-user limit of {limit} seats for this show.");

        public static ReserveDeclined IdempotencyConflict() => new(409, ErrorCodes.IdempotencyKeyReused, DeclineReason.IdempotencyConflict,
            "The idempotency key was already used with a different request.");
    }
}
