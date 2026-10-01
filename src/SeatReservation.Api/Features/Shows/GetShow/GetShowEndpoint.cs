using Microsoft.EntityFrameworkCore;
using SeatReservation.Api.Common;
using SeatReservation.Api.Infrastructure.Authentication;
using SeatReservation.Api.Infrastructure.Observability;
using SeatReservation.Api.Infrastructure.Persistence;

namespace SeatReservation.Api.Features.Shows.GetShow;

public static class GetShowEndpoint
{
    // Public read: anyone can see the seat map. An admin token additionally reveals who owns each seat,
    // which is what the burst script uses to verify "no seat confirmed to two users".
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/shows/{showId}", HandleAsync).AllowAnonymous();

    private static async Task<IResult> HandleAsync(string showId, HttpContext ctx, AppDbContext db, CancellationToken ct)
    {
        var log = ctx.Log();
        log.Operation = "get_show";

        if (!Guid.TryParse(showId, out var id))
            return NotFound(ctx);
        log.ShowId = id;

        var show = await db.Shows.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (show is null)
            return NotFound(ctx);

        // One query = one snapshot of the seat rows, so the counts derived from it always reconcile.
        var includeOwners = ctx.User.IsAdmin();
        var seats = await (
            from seat in db.Seats.AsNoTracking()
            where seat.ShowId == id
            join r in db.Reservations on seat.ReservationId equals r.Id into owners
            from owner in owners.DefaultIfEmpty()
            orderby seat.Position
            select new SeatState(
                seat.SeatNumber,
                seat.Status,
                includeOwners ? seat.ReservationId : null,
                includeOwners && owner != null ? owner.UserId : null)
        ).ToListAsync(ct);

        log.Result = "ok";
        return Results.Ok(ShowStateResponse.From(
            show.Id, show.Name, show.PricePaise, show.PerUserLimit, show.TotalSeats, show.CreatedAt, seats));
    }

    private static IResult NotFound(HttpContext ctx)
    {
        ctx.Log().Result = "not_found";
        return ApiErrors.Result(ctx, StatusCodes.Status404NotFound, ErrorCodes.ShowNotFound, "The requested show does not exist.");
    }
}
