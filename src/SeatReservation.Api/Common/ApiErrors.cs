namespace SeatReservation.Api.Common;

public static class ErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string ShowNotFound = "SHOW_NOT_FOUND";
    public const string UnknownSeat = "UNKNOWN_SEAT";
    public const string SeatUnavailable = "SEAT_UNAVAILABLE";
    public const string UserLimitExceeded = "USER_LIMIT_EXCEEDED";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string ReservationNotFound = "RESERVATION_NOT_FOUND";
    public const string ReservationAccessDenied = "RESERVATION_ACCESS_DENIED";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";
}

public sealed record ErrorBody(string Code, string Message, string CorrelationId);

public sealed record ErrorEnvelope(ErrorBody Error);

public static class ApiErrors
{
    public static IResult Result(HttpContext ctx, int status, string code, string message) =>
        Results.Json(new ErrorEnvelope(new ErrorBody(code, message, ctx.TraceIdentifier)), statusCode: status);

    /// <summary>For code paths outside endpoint results (auth handler, exception middleware).</summary>
    public static Task WriteAsync(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new ErrorEnvelope(new ErrorBody(code, message, ctx.TraceIdentifier)));
    }
}
