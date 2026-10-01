using Microsoft.EntityFrameworkCore;

namespace SeatReservation.Api.Infrastructure.Persistence;

public sealed class DatabaseState
{
    private volatile bool _migrated;
    public bool Migrated => _migrated;
    public void MarkMigrated() => _migrated = true;
}

/// <summary>
/// Applies EF migrations at startup, retrying until the database is reachable.
/// Runs in the background so the process (and /health/live) comes up immediately on a cold
/// start even if Postgres is still booting; /health/ready stays 503 until this finishes.
/// </summary>
public sealed class DatabaseMigrator(
    IServiceScopeFactory scopes,
    DatabaseState state,
    ILogger<DatabaseMigrator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync(stoppingToken);
                state.MarkMigrated();
                logger.LogInformation("Database migrated and ready after {Attempts} attempt(s)", attempt);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}): {Error}. Retrying in {DelaySeconds}s",
                    attempt, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
    }
}
