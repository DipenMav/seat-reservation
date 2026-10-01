using Npgsql;
using SeatReservation.Api.Common;
using SeatReservation.Api.Infrastructure.Authentication;
using SeatReservation.Api.Infrastructure.Observability;

namespace SeatReservation.Api.Features.Reservations.GetReservation;

public static class GetReservationEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/reservations/{reservationId}", HandleAsync).RequireAuthorization();

    private static async Task<IResult> HandleAsync(string reservationId, HttpContext ctx, NpgsqlDataSource db, CancellationToken ct)
    {
        var log = ctx.Log();
        log.Operation = "get_reservation";
        log.UserId = ctx.User.UserId();

        ReservationDto? reservation = null;
        if (Guid.TryParse(reservationId, out var id))
        {
            log.ReservationId = id;
            await using var conn = await db.OpenConnectionAsync(ct);
            reservation = await ReservationQueries.LoadAsync(conn, id, ct);
        }

        if (reservation is null)
            return ApiErrors.Result(ctx, StatusCodes.Status404NotFound, ErrorCodes.ReservationNotFound,
                "The requested reservation does not exist.");

        if (reservation.UserId != ctx.User.UserId() && !ctx.User.IsAdmin())
            return ApiErrors.Result(ctx, StatusCodes.Status403Forbidden, ErrorCodes.ReservationAccessDenied,
                "You are not allowed to view this reservation.");

        return Results.Ok(reservation);
    }
}
