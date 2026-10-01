using Microsoft.Extensions.Diagnostics.HealthChecks;
using SeatReservation.Api.Infrastructure.Persistence;

namespace SeatReservation.Api.Infrastructure.Health;

/// <summary>
/// Readiness = schema migrated AND a round trip to Postgres succeeds right now.
/// Fails closed: any exception or a slow database reports Unhealthy (503).
/// Uses the small ops pool so a burst that saturates the main pool doesn't make
/// the instance look dead to the platform's health checker.
/// </summary>
public sealed class DatabaseReadinessCheck(OpsDataSource ops, DatabaseState state) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!state.Migrated)
            return HealthCheckResult.Unhealthy("database migrations have not completed");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var cmd = ops.DataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy("database reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("database unreachable", ex);
        }
    }
}
