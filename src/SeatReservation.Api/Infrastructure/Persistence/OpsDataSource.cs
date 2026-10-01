using Npgsql;

namespace SeatReservation.Api.Infrastructure.Persistence;

/// <summary>
/// A separate, tiny connection pool for readiness probes and metrics scrapes, so operational
/// visibility keeps working while the main pool is saturated by a booking stampede.
/// </summary>
public sealed class OpsDataSource(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public static OpsDataSource Create(string mainConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(mainConnectionString)
        {
            MaxPoolSize = 3,
            Timeout = 3,
            CommandTimeout = 5,
            ApplicationName = "seat-reservation-ops",
        };
        return new OpsDataSource(NpgsqlDataSource.Create(builder.ConnectionString));
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
