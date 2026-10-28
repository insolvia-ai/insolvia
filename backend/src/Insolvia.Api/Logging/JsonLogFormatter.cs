using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Insolvia.Api.Logging;

/// <summary>
/// One JSON object per log line — services/api core/logging.py's
/// <c>JsonFormatter</c>: <c>timestamp</c> (UTC, milliseconds, <c>Z</c>),
/// <c>level</c> (Python's level names), <c>logger</c>, <c>message</c>, then
/// every structured field the call site passed (Python's <c>extra=</c>), then
/// <c>exception</c> when there is one. A value that is not a JSON primitive is
/// written as its string, as <c>json.dumps(default=str)</c> did.
/// </summary>
/// <remarks>
/// The privacy rule is the call sites', not this class's: log lines carry
/// request metadata only — never a body, a query string, a header, a token or
/// a claim (GLBA; ADR 0001). This formatter writes what it is given.
/// </remarks>
public sealed class JsonLogFormatter : ConsoleFormatter
{
    /// <summary>The name the console logger selects this formatter by.</summary>
    public const string FormatterName = "insolvia-json";

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "timestamp", "level", "logger", "message", "exception", "{OriginalFormat}",
    };

    /// <summary>Creates the formatter.</summary>
    public JsonLogFormatter()
        : base(FormatterName)
    {
    }

    /// <summary>Python's name for a .NET level.</summary>
    /// <param name="level">The .NET level.</param>
    /// <returns>DEBUG, INFO, WARNING, ERROR or CRITICAL.</returns>
    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        _ => "CRITICAL",
    };

    /// <summary>Renders one entry as the JSON line, without the newline.</summary>
    /// <param name="timestamp">When the entry was logged.</param>
    /// <param name="level">Its level.</param>
    /// <param name="category">The logger's name.</param>
    /// <param name="message">The rendered message.</param>
    /// <param name="fields">The structured fields, in order.</param>
    /// <param name="exception">The exception, if any.</param>
    /// <returns>The line.</returns>
    public static string Render(
        DateTimeOffset timestamp,
        LogLevel level,
        string category,
        string message,
        IEnumerable<KeyValuePair<string, object?>> fields,
        Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(fields);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("level", LevelName(level));
            writer.WriteString("logger", category);
            writer.WriteString("message", message);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in fields)
            {
                if (Reserved.Contains(key) || !seen.Add(key))
                {
                    continue;
                }

                writer.WritePropertyName(key);
                WriteValue(writer, value);
            }

            if (exception is not null)
            {
                writer.WriteString("exception", exception.ToString());
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        IEnumerable<KeyValuePair<string, object?>> fields = logEntry.State as IEnumerable<KeyValuePair<string, object?>> ?? [];
        textWriter.Write(Render(DateTimeOffset.UtcNow, logEntry.LogLevel, logEntry.Category, message ?? string.Empty, fields, logEntry.Exception));
        textWriter.Write('\n');
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number when double.IsFinite(number):
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
