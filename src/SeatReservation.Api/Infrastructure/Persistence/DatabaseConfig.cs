using Npgsql;

namespace SeatReservation.Api.Infrastructure.Persistence;

public static class DatabaseConfig
{
    /// <summary>
    /// Resolves the connection string from (in order) ConnectionStrings:Default or DATABASE_URL
    /// (postgres://user:pass@host:port/db, the format Render/Railway/Fly hand out), then applies
    /// pool settings sized for a burst: callers wait for a pooled connection instead of failing.
    /// </summary>
    public static string Resolve(IConfiguration config)
    {
        var raw = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(raw))
            raw = config["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "No database configured. Set ConnectionStrings__Default or DATABASE_URL.");

        var builder = raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
                      raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            ? FromUrl(raw)
            : new NpgsqlConnectionStringBuilder(raw);

        // Keep well under the database's max_connections (free-tier Postgres is often ~100).
        builder.MaxPoolSize = config.GetValue("DB_MAX_POOL_SIZE", 40);
        // Seconds to wait for a free pooled connection. Under a stampede requests queue here;
        // a short timeout would turn queueing into 5xx.
        builder.Timeout = config.GetValue("DB_CONNECT_TIMEOUT", 60);
        builder.CommandTimeout = config.GetValue("DB_COMMAND_TIMEOUT", 30);
        builder.ApplicationName ??= "seat-reservation";
        // We authenticate with a password; don't probe for Kerberos/GSS (the Alpine image has no
        // libgssapi, which otherwise prints a scary "Cannot load library" line on every start).
        builder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.ConnectionString;
    }

    private static NpgsqlConnectionStringBuilder FromUrl(string url)
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<SslMode>(kv[1].Replace("-", ""), ignoreCase: true, out var mode))
                builder.SslMode = mode;
        }

        return builder;
    }
}
