using System.Collections;

namespace Insolvia.Admin.Logging;

/// <summary>
/// A fixed message plus named fields — Python's
/// <c>logger.info("...", extra={...})</c>. The message is a constant at every
/// call site; anything variable goes in a field, so a log line can be queried
/// by key and nothing sensitive is ever interpolated into prose.
/// </summary>
public static class StructuredLog
{
    /// <summary>The logger names the Python service used, kept so a CloudWatch query reads both backends.</summary>
    public static class Categories
    {
        /// <summary>The one-line-per-request log.</summary>
        public const string Request = "insolvia_admin.request";

        /// <summary>Staff (Google Workspace) authentication.</summary>
        public const string Auth = "insolvia_admin.api.auth";

        /// <summary>The error handlers.</summary>
        public const string AppFactory = "insolvia_admin.api.app_factory";
    }

    /// <summary>Logs <paramref name="message"/> at <paramref name="level"/> with <paramref name="fields"/>.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="level">The level.</param>
    /// <param name="message">A constant message.</param>
    /// <param name="exception">The exception, if any.</param>
    /// <param name="fields">The structured fields.</param>
    public static void Write(this ILogger logger, LogLevel level, string message, Exception? exception, params (string Key, object? Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!logger.IsEnabled(level))
        {
            return;
        }

        logger.Log(level, default, new Fields(message, fields), exception, static (state, _) => state.Message);
    }

    /// <summary>Logs at Information.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="message">A constant message.</param>
    /// <param name="fields">The structured fields.</param>
    public static void Info(this ILogger logger, string message, params (string Key, object? Value)[] fields) =>
        logger.Write(LogLevel.Information, message, null, fields);

    /// <summary>The state a structured entry carries: readable as key/value pairs by any provider.</summary>
    /// <param name="Message">The constant message.</param>
    /// <param name="Values">The fields.</param>
    public sealed record Fields(string Message, IReadOnlyList<(string Key, object? Value)> Values)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        /// <inheritdoc />
        public int Count => Values.Count;

        /// <inheritdoc />
        public KeyValuePair<string, object?> this[int index] => new(Values[index].Key, Values[index].Value);

        /// <inheritdoc />
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            foreach (var (key, value) in Values)
            {
                yield return new(key, value);
            }
        }

        /// <inheritdoc />
        public override string ToString() => Message;

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
