namespace SeatReservation.Api.Infrastructure.Observability;

/// <summary>
/// Domain fields a handler wants on the single structured "request completed" log line.
/// One line per request keeps log volume sane during a 20k-request burst.
/// </summary>
public sealed class RequestLogContext
{
    public string? Operation { get; set; }
    public Guid? ShowId { get; set; }
    public string? UserId { get; set; }
    public Guid? ReservationId { get; set; }
    public string? Result { get; set; }
    public string? DeclineReason { get; set; }
    public int? SeatCount { get; set; }
    public int? Attempts { get; set; }
}

public static class RequestLogContextExtensions
{
    public static RequestLogContext Log(this HttpContext ctx)
    {
        var log = ctx.Features.Get<RequestLogContext>();
        if (log is null)
        {
            log = new RequestLogContext();
            ctx.Features.Set(log);
        }
        return log;
    }
}
