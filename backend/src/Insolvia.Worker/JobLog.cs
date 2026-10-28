using System.Text.Json.Nodes;
using Insolvia.Core.Jobs;

namespace Insolvia.Worker;

/// <summary>
/// The worker's structured log: one JSON line per record on stdout (CloudWatch
/// on Lambda, the terminal locally) — the shape the API's request log writes.
/// GLBA: callers pass identifiers, kinds, counts and token usage only, never a
/// value from the case or a model answer; an exception contributes its TYPE.
/// </summary>
public class JobLog
{
    /// <summary>The process-wide log.</summary>
    public static JobLog Console { get; } = new();

    /// <summary>One record.</summary>
    /// <param name="level">info, warning or error.</param>
    /// <param name="message">A fixed sentence.</param>
    /// <param name="fields">Identifiers and counts.</param>
    public virtual void Write(string level, string message, params (string Key, object? Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var line = new JsonObject
        {
            ["timestamp"] = JobRules.Timestamp(DateTimeOffset.UtcNow),
            ["level"] = level,
            ["logger"] = "insolvia_worker",
            ["message"] = message,
        };
        foreach (var (key, value) in fields)
        {
            line[key] = value switch
            {
                null => null,
                string text => text,
                int number => number,
                long number => number,
                bool flag => flag,
                _ => value.ToString(),
            };
        }

        System.Console.Out.WriteLine(line.ToJsonString());
    }

    /// <summary>An info record.</summary>
    public void Info(string message, params (string Key, object? Value)[] fields) => Write("info", message, fields);

    /// <summary>An error record.</summary>
    public void Error(string message, params (string Key, object? Value)[] fields) => Write("error", message, fields);
}

/// <summary>A log that keeps its records — what tests read to assert what (and only what) was logged.</summary>
public sealed class RecordingJobLog : JobLog
{
    private readonly Lock _gate = new();
    private readonly List<(string Level, string Message, IReadOnlyDictionary<string, object?> Fields)> _records = [];

    /// <summary>Every record, oldest first.</summary>
    public IReadOnlyList<(string Level, string Message, IReadOnlyDictionary<string, object?> Fields)> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    /// <inheritdoc />
    public override void Write(string level, string message, params (string Key, object? Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        lock (_gate)
        {
            _records.Add((level, message, fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal)));
        }
    }
}
