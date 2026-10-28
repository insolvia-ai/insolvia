using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Insolvia.Core;

/// <summary>
/// The handful of Python <c>str</c> behaviours the domain's rules lean on,
/// spelled out once so every parser measures and trims the same way.
/// </summary>
public static class PyText
{
    /// <summary>Python's <c>len(s)</c>: Unicode code points, not UTF-16 units.</summary>
    public static int Length(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>Python's <c>s.strip()</c>.</summary>
    public static string Strip(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim();
    }

    /// <summary>Python's <c>s.split()</c>: on runs of whitespace, no empty parts.</summary>
    public static string[] SplitWhitespace(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Python's <c>s.casefold()</c>, approximated by invariant lower-casing —
    /// the only difference is the handful of characters casefold expands
    /// (<c>ß</c> to <c>ss</c>), which no rule here depends on.
    /// </summary>
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Python lower-cases (casefold); a sort over the upper-cased form orders punctuation differently.")]
    public static string Casefold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToLowerInvariant();
    }

    /// <summary>Python's <c>s.lower()</c>, for values that are STORED lower-cased (email addresses).</summary>
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "The stored form is lower-case, as the Python domain wrote it; Cognito's pool is case-insensitive.")]
    public static string Lower(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToLowerInvariant();
    }

    /// <summary>Python's <c>s.isdigit()</c> for the ASCII digits the forms print.</summary>
    public static bool IsDigits(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && value.EnumerateRunes().All(Rune.IsDigit);
    }
}

/// <summary>
/// The server's write-time instant. Python stamped every record with
/// <c>datetime.now(UTC)</c> directly; here the clock is a
/// <see cref="TimeProvider"/> argument that defaults to the system clock, so
/// a test can pin it without a port of its own.
/// </summary>
public static class Clock
{
    /// <summary>
    /// <c>fields.timestamp()</c>: now, UTC, truncated to microseconds — the
    /// precision every case-domain <c>created_at</c>/<c>updated_at</c> had.
    /// </summary>
    public static DateTimeOffset Now(TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }

    /// <summary>
    /// <c>firms._timestamp()</c> and the access log's stamp: now, UTC,
    /// truncated to milliseconds.
    /// </summary>
    public static DateTimeOffset NowMillis(TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    /// <summary>Today's date in UTC — Python's <c>date.today()</c>, pinned to UTC rather than the host's zone.</summary>
    public static DateOnly Today(TimeProvider? clock = null) =>
        DateOnly.FromDateTime((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
}
