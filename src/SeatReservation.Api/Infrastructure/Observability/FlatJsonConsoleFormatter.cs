using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace SeatReservation.Api.Infrastructure.Observability;

/// <summary>
/// One JSON object per line with message-template properties and scope properties flattened
/// to top-level snake_case keys, e.g.
/// {"ts":"…","level":"info","msg":"…","request_id":"…","show_id":"…","decline_reason":"seat_taken"}
/// so log platforms can filter on request_id / decline_reason without parsing nested scopes.
/// </summary>
public sealed class FlatJsonConsoleFormatter() : ConsoleFormatter(Name)
{
    public new const string Name = "flatjson";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ASP.NET hosting scope fields that duplicate ours (request_id / trace_id) or add noise.
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "{OriginalFormat}", "SpanId", "TraceId", "ParentId", "ConnectionId", "RequestId", "RequestPath",
    };

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopes, TextWriter writer)
    {
        var message = entry.Formatter(entry.State, entry.Exception);
        if (message is null && entry.Exception is null) return;

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("ts", DateTimeOffset.UtcNow);
            json.WriteString("level", Level(entry.LogLevel));
            json.WriteString("category", entry.Category);
            json.WriteString("msg", message);

            var written = new HashSet<string>(StringComparer.Ordinal) { "ts", "level", "category", "msg" };
            scopes?.ForEachScope((scope, w) => WriteProperties(scope, w, written), json);
            WriteProperties(entry.State, json, written);

            if (entry.Exception is not null)
                json.WriteString("exception", entry.Exception.ToString());
            json.WriteEndObject();
        }

        writer.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    private static void WriteProperties(object? state, Utf8JsonWriter json, HashSet<string> written)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> pairs) return;
        foreach (var (key, value) in pairs)
        {
            if (value is null || Skipped.Contains(key)) continue;
            if (!written.Add(key)) continue;
            switch (value)
            {
                case string s: json.WriteString(key, s); break;
                case bool b: json.WriteBoolean(key, b); break;
                case int i: json.WriteNumber(key, i); break;
                case long l: json.WriteNumber(key, l); break;
                case double d: json.WriteNumber(key, Math.Round(d, 2)); break;
                default: json.WriteString(key, value.ToString()); break;
            }
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "fatal",
        _ => "none",
    };
}
