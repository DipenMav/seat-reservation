using Npgsql;

namespace SeatReservation.Api.Common;

/// <summary>Tiny helpers for positional-parameter ($1, $2…) Npgsql batch commands.</summary>
public static class Sql
{
    public static NpgsqlBatchCommand Command(string sql, params NpgsqlParameter[] parameters)
    {
        var cmd = new NpgsqlBatchCommand(sql);
        cmd.Parameters.AddRange(parameters);
        return cmd;
    }

    public static NpgsqlParameter<T> P<T>(T value) => new() { TypedValue = value };

    /// <summary>Deadlock / serialization failure: the transaction was rolled back and is safe to retry.</summary>
    public static bool IsTransient(PostgresException ex) => ex.SqlState is "40P01" or "40001";
}
