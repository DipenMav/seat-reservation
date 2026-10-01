using Npgsql;
using NpgsqlTypes;
using SeatReservation.Api.Common;
using SeatReservation.Api.Infrastructure.Authentication;
using SeatReservation.Api.Infrastructure.Observability;
using SeatReservation.Api.Infrastructure.OpenApi;
using SeatReservation.Api.Infrastructure.Persistence;

namespace SeatReservation.Api.Features.Shows.CreateShow;

public sealed record CreateShowRequest(string? Name, List<string>? Seats, long? PricePaise, int? PerUserLimit);

public static class CreateShowEndpoint
{
    public const int DefaultPerUserLimit = 4;
    public const int MaxSeats = 50_000;

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/shows", HandleAsync)
            .RequireAuthorization(AuthPolicies.Admin)
            .WithTags("Shows")
            .WithSummary("Create a show (admin)")
            .WithDescription("Creates the show and every seat in `available` state. `per_user_limit` is optional (default 4). `price_paise` must be an integer.")
            .WithRequestExample("""{"name":"friday-night","seats":["A1","A2","A3","A12","A13"],"price_paise":25000,"per_user_limit":4}""")
            .Produces<ShowStateResponse>(StatusCodes.Status201Created)
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest)
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorEnvelope>(StatusCodes.Status403Forbidden);

    private static async Task<IResult> HandleAsync(
        CreateShowRequest? request, HttpContext ctx, NpgsqlDataSource db, CancellationToken ct)
    {
        var log = ctx.Log();
        log.Operation = "create_show";
        log.UserId = ctx.User.UserId();

        if (Validate(request) is { } error)
        {
            log.Result = "invalid";
            return ApiErrors.Result(ctx, StatusCodes.Status400BadRequest, ErrorCodes.ValidationError, error);
        }

        var name = request!.Name!.Trim();
        var seats = request.Seats!.Select(s => s.Trim()).ToArray();
        var price = request.PricePaise!.Value;
        var limit = request.PerUserLimit ?? DefaultPerUserLimit;
        var showId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var batch = new NpgsqlBatch(conn, tx))
        {
            batch.BatchCommands.Add(Sql.Command(
                "INSERT INTO shows (id, name, price_paise, per_user_limit, total_seats, created_at) VALUES ($1, $2, $3, $4, $5, $6)",
                Sql.P(showId), Sql.P(name), Sql.P(price), Sql.P(limit), Sql.P(seats.Length), Sql.P(now)));

            // One statement for the whole seat map, however large.
            batch.BatchCommands.Add(Sql.Command(
                """
                INSERT INTO seats (id, show_id, seat_number, position, status, reservation_id, created_at, updated_at)
                SELECT u.id, $1, u.seat_number, u.position, 'available', NULL, $4, $4
                  FROM unnest($2::uuid[], $3::text[]) WITH ORDINALITY AS u(id, seat_number, position)
                """,
                Sql.P(showId),
                new NpgsqlParameter { Value = seats.Select(_ => Guid.CreateVersion7()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid },
                new NpgsqlParameter { Value = seats, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
                Sql.P(now)));

            await batch.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        log.ShowId = showId;
        log.SeatCount = seats.Length;
        log.Result = "created";

        var state = ShowStateResponse.From(showId, name, price, limit, seats.Length, now,
            seats.Select(s => new SeatState(s, SeatStatus.Available)).ToList());
        return Results.Json(state, statusCode: StatusCodes.Status201Created);
    }

    private static string? Validate(CreateShowRequest? r)
    {
        if (r is null) return "Request body is required.";
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200)
            return "name is required and must be at most 200 characters.";
        if (r.Seats is null || r.Seats.Count == 0) return "seats must contain at least one seat.";
        if (r.Seats.Count > MaxSeats) return $"A show can have at most {MaxSeats} seats.";
        if (r.Seats.Any(s => string.IsNullOrWhiteSpace(s) || s.Trim().Length > 50))
            return "Seat identifiers must be non-empty and at most 50 characters.";
        var duplicate = r.Seats.Select(s => s.Trim()).GroupBy(s => s, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) return $"Seat '{duplicate.Key}' is listed more than once.";
        if (r.PricePaise is null) return "price_paise is required (integer paise).";
        if (r.PricePaise < 0) return "price_paise must be >= 0.";
        if (r.PerUserLimit is < 1 or > 100) return "per_user_limit must be between 1 and 100.";
        return null;
    }
}
