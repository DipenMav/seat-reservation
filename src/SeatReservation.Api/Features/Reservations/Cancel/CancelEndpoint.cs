using SeatReservation.Api.Common;
using SeatReservation.Api.Infrastructure.Authentication;
using SeatReservation.Api.Infrastructure.Observability;

namespace SeatReservation.Api.Features.Reservations.Cancel;

public static class CancelEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/reservations/{reservationId}/cancel", HandleAsync).RequireAuthorization();

    private static async Task<IResult> HandleAsync(string reservationId, HttpContext ctx, CancelHandler handler)
    {
        var userId = ctx.User.UserId();
        var log = ctx.Log();
        log.Operation = "cancel";
        log.UserId = userId;

        if (!Guid.TryParse(reservationId, out var id))
            return NotFound(ctx);
        log.ReservationId = id;

        switch (await handler.HandleAsync(id, userId))
        {
            case Cancelled c:
                log.ShowId = c.Reservation.ShowId;
                log.SeatCount = c.Reservation.Seats.Count;
                log.Result = c.AlreadyCancelled ? "already_cancelled" : "cancelled";
                return Results.Ok(c.Reservation);
            case CancelForbidden:
                log.Result = "forbidden";
                return ApiErrors.Result(ctx, StatusCodes.Status403Forbidden, ErrorCodes.ReservationAccessDenied,
                    "You are not allowed to cancel this reservation.");
            default:
                return NotFound(ctx);
        }
    }

    private static IResult NotFound(HttpContext ctx)
    {
        ctx.Log().Result = "not_found";
        return ApiErrors.Result(ctx, StatusCodes.Status404NotFound, ErrorCodes.ReservationNotFound,
            "The requested reservation does not exist.");
    }
}
