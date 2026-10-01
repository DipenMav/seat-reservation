using System.Diagnostics;
using Npgsql;
using SeatReservation.Api.Common;

namespace SeatReservation.Api.Infrastructure.Observability;

/// <summary>
/// Outermost middleware:
///  - assigns a correlation id (honours a sane inbound X-Correlation-ID), echoes it in the response
///  - opens a log scope with request_id / trace_id so every log line in the request carries them
///  - maps exceptions to the JSON error envelope (DB unreachable → 503, never a stack trace)
///  - emits exactly one structured "request completed" line with the handler's domain fields
/// </summary>
public sealed class RequestPipelineMiddleware(RequestDelegate next, ILogger<RequestPipelineMiddleware> logger)
{
    public const string CorrelationHeader = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var requestId = ResolveCorrelationId(ctx.Request.Headers[CorrelationHeader].ToString());
        ctx.TraceIdentifier = requestId;
        ctx.Response.Headers[CorrelationHeader] = requestId;

        var started = Stopwatch.GetTimestamp();
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["trace_id"] = Activity.Current?.TraceId.ToString(),
        });

        try
        {
            await next(ctx);
        }
        catch (BadHttpRequestException ex) when (!ctx.Response.HasStarted)
        {
            // Malformed JSON, wrong types (e.g. a float for price_paise), oversized body.
            await ApiErrors.WriteAsync(ctx, StatusCodes.Status400BadRequest, ErrorCodes.ValidationError,
                ex.InnerException is System.Text.Json.JsonException { Path: { } jsonPath }
                    ? $"Invalid request body: unexpected value at {jsonPath} (money is integer paise)."
                    : "Invalid request body.");
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            ctx.Response.StatusCode = 499; // client went away; nothing to send
        }
        catch (Exception ex) when (IsDatabaseUnavailable(ex) && !ctx.Response.HasStarted)
        {
            logger.LogError(ex, "Database unavailable");
            ctx.Response.Headers.RetryAfter = "1";
            await ApiErrors.WriteAsync(ctx, StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServiceUnavailable,
                "The reservation store is temporarily unavailable. Retry with the same idempotency key.");
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            logger.LogError(ex, "Unhandled exception");
            await ApiErrors.WriteAsync(ctx, StatusCodes.Status500InternalServerError, ErrorCodes.InternalError,
                "An unexpected error occurred.");
        }
        finally
        {
            LogCompletion(ctx, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private void LogCompletion(HttpContext ctx, double durationMs)
    {
        var path = ctx.Request.Path.Value ?? "";
        var level = path.StartsWith("/health") || path.StartsWith("/metrics") ? LogLevel.Debug : LogLevel.Information;
        if (!logger.IsEnabled(level)) return;

        var d = ctx.Features.Get<RequestLogContext>();
        using var domain = d is null ? null : logger.BeginScope(new Dictionary<string, object?>
        {
            ["operation"] = d.Operation,
            ["show_id"] = d.ShowId?.ToString(),
            ["user_id"] = d.UserId,
            ["reservation_id"] = d.ReservationId?.ToString(),
            ["result"] = d.Result,
            ["decline_reason"] = d.DeclineReason,
            ["seat_count"] = d.SeatCount,
            ["attempts"] = d.Attempts,
        });

        logger.Log(level, "{method} {path} -> {status_code} in {duration_ms} ms",
            ctx.Request.Method, path, ctx.Response.StatusCode, Math.Round(durationMs, 2));
    }

    internal static bool IsDatabaseUnavailable(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState is "57P01" or "57P02" or "57P03" or "53300", // shutdown / too many connections
        NpgsqlException => true, // socket / pool / timeout: we could not get an answer from the database
        TimeoutException => true,
        _ => ex.InnerException is not null && IsDatabaseUnavailable(ex.InnerException),
    };

    private static string ResolveCorrelationId(string inbound) =>
        inbound.Length is > 0 and <= 64 && inbound.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? inbound
            : Guid.CreateVersion7().ToString("N");
}
