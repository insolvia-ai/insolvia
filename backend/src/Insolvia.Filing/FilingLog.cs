using System.Text.Json;
using System.Text.Json.Nodes;

namespace Insolvia.Filing;

/// <summary>
/// The worker's log: one JSON line per record, metadata only — ids, states,
/// reasons; never a password, a seed, a TOTP code, a document or a page.
/// A port so the guard tests can read every line the worker wrote.
/// </summary>
public interface IFilingLog
{
    /// <summary>One record.</summary>
    void Write(string level, string message, params (string Key, object? Value)[] fields);
}

/// <summary>JSON lines to stdout — what CloudWatch and a terminal read.</summary>
public sealed class ConsoleFilingLog : IFilingLog
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Write(string level, string message, params (string Key, object? Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var line = new JsonObject
        {
            ["level"] = level,
            ["logger"] = "insolvia_filing",
            ["message"] = message,
            ["service"] = "insolvia-filing",
        };
        foreach (var (key, value) in fields)
        {
            line[key] = value is null ? null : JsonValue.Create(value.ToString());
        }

        lock (_gate)
        {
            Console.Out.WriteLine(line.ToJsonString(Compact));
        }
    }
}
