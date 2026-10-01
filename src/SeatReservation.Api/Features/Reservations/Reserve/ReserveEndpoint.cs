using System.Diagnostics;
using SeatReservation.Api.Common;
using SeatReservation.Api.Infrastructure.Authentication;
using SeatReservation.Api.Infrastructure.Observability;
using SeatReservation.Api.Infrastructure.OpenApi;
using Microsoft.AspNetCore.Mvc;

namespace SeatReservation.Api.Features.Reservations.Reserve;

/// <summary>
/// Body: { "seats": ["A12"], "idempotency_key": "…" }. The key may instead (or also, if equal)
/// be sent as the Idempotency-Key header. There is intentionally no user field: any "user_id"
/// in the body is never bound — the owner is always the authenticated principal.
/// </summary>
public sealed record ReserveRequest(List<string>? Seats, string? IdempotencyKey);

public static class ReserveEndpoint
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    public const int MaxSeatsPerRequest = 20;

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/shows/{showId}/reserve", HandleAsync)
            .RequireAuthorization()
            .WithTags("Reservations")
            .WithSummary("Reserve seats (all-or-nothing, idempotent)")
            .WithDescription("""
                The owner is the authenticated user; any `user_id` in the body is ignored.
                Send the idempotency key in the body (`idempotency_key`) or the `Idempotency-Key` header.

                - **201**: reserved. A retry with the same key and seats returns the *original* reservation with header `Idempotent-Replayed: true`.
                - **409** `SEAT_UNAVAILABLE`: a requested seat is taken; nothing was reserved.
                - **409** `USER_LIMIT_EXCEEDED`: would exceed the show's per-user limit.
                - **409** `IDEMPOTENCY_KEY_REUSED`: same key, different seats.
                - **400**: duplicate seats, missing key, or a seat not in the show.
                """)
            .WithRequestExample("""{"seats":["A12"],"idempotency_key":"9e7c6f2b-9c18-4e7a-91c3-123456789abc"}""")
            .Produces<ReservationDto>(StatusCodes.Status201Created)
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest)
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorEnvelope>(StatusCodes.Status404NotFound)
            .Produces<ErrorEnvelope>(StatusCodes.Status409Conflict)
            .Produces<ErrorEnvelope>(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> HandleAsync(
        string showId,
        ReserveRequest? request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKeyHeader,
        HttpContext ctx,
        ReserveHandler handler,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var userId = ctx.User.UserId();
        var log = ctx.Log();
        log.Operation = "reserve";
        log.UserId = userId;

        ReserveOutcome outcome;
        if (!Guid.TryParse(showId, out var id))
        {
            outcome = new ReserveDeclined(404, ErrorCodes.ShowNotFound, DeclineReason.ShowNotFound, "The requested show does not exist.");
        }
        else
        {
            log.ShowId = id;
            var (seats, key, error) = Normalize(request, idempotencyKeyHeader ?? "");
            outcome = error is not null
                ? new ReserveDeclined(400, ErrorCodes.ValidationError, DeclineReason.InvalidRequest, error)
                : await handler.HandleAsync(id, userId, seats!, key!, ct);
            log.SeatCount = seats?.Count;
        }

        var label = outcome switch
        {
            ReserveCreated => "confirmed",
            ReserveReplayed => DeclineReason.IdempotentReplay,
            ReserveDeclined d => d.Reason,
            _ => "unknown",
        };
        if (outcome is not ReserveCreated)
            ReservationMetrics.Declined.WithLabels(label).Inc();
        ReservationMetrics.Duration.WithLabels(label).Observe(Stopwatch.GetElapsedTime(started).TotalSeconds);
        log.Attempts = outcome.Attempts;

        switch (outcome)
        {
            case ReserveCreated created:
                log.Result = "confirmed";
                log.ReservationId = created.Reservation.ReservationId;
                return Results.Json(created.Reservation, statusCode: StatusCodes.Status201Created);

            case ReserveReplayed replayed:
                // Same response as the original request (201 + same reservation), flagged as a replay.
                log.Result = "replayed";
                log.DeclineReason = DeclineReason.IdempotentReplay;
                log.ReservationId = replayed.Reservation.ReservationId;
                ctx.Response.Headers[ReplayedHeader] = "true";
                return Results.Json(replayed.Reservation, statusCode: StatusCodes.Status201Created);

            case ReserveDeclined declined:
                log.Result = "declined";
                log.DeclineReason = declined.Reason;
                return ApiErrors.Result(ctx, declined.Status, declined.Code, declined.Message);

            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Trim, reject duplicates/empties, sort ordinally (the lock order and the hash input).</summary>
    private static (IReadOnlyList<string>? Seats, string? Key, string? Error) Normalize(ReserveRequest? request, string headerKey)
    {
        if (request?.Seats is not { Count: > 0 } raw)
            return (null, null, "seats must contain at least one seat.");
        if (raw.Count > MaxSeatsPerRequest)
            return (null, null, $"At most {MaxSeatsPerRequest} seats per request.");
        if (raw.Any(s => string.IsNullOrWhiteSpace(s) || s.Trim().Length > 50))
            return (null, null, "Seat identifiers must be non-empty and at most 50 characters.");

        var seats = raw.Select(s => s.Trim()).ToList();
        if (seats.Distinct(StringComparer.Ordinal).Count() != seats.Count)
            return (null, null, "A seat may only be requested once per request.");
        seats.Sort(StringComparer.Ordinal);

        var bodyKey = request.IdempotencyKey?.Trim();
        headerKey = headerKey.Trim();
        if (!string.IsNullOrEmpty(bodyKey) && headerKey.Length > 0 && bodyKey != headerKey)
            return (seats, null, "Idempotency-Key header and idempotency_key body field disagree.");

        var key = headerKey.Length > 0 ? headerKey : bodyKey;
        if (string.IsNullOrEmpty(key))
            return (seats, null, "An idempotency key is required (Idempotency-Key header or idempotency_key field).");
        if (key.Length > 200)
            return (seats, null, "Idempotency key must be at most 200 characters.");

        return (seats, key, null);
    }
}
