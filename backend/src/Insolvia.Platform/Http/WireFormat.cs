using System.Globalization;

namespace Insolvia.Platform.Http;

/// <summary>
/// The wire's timestamp and date strings. Their precision is part of the
/// contract — each record kind keeps the one its clients have always read —
/// so a view formats them here rather than letting the serializer choose.
/// </summary>
public static class WireFormat
{
    /// <summary>ISO 8601 UTC to the millisecond, with a literal Z — firms, firm users, the waitlist.</summary>
    /// <param name="value">The instant.</param>
    /// <returns>The wire string.</returns>
    public static string Millis(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>ISO 8601 UTC to the microsecond, with a literal Z — every other firm-scoped record.</summary>
    /// <param name="value">The instant.</param>
    /// <returns>The wire string.</returns>
    public static string Micros(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A court date, <c>YYYY-MM-DD</c>.</summary>
    /// <param name="value">The date.</param>
    /// <returns>The wire string.</returns>
    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
