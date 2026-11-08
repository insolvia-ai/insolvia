using System.Globalization;

namespace Insolvia.Core.Events;

// Calendar events and the feed token (issue 14.6 / #358; Python's
// services/api core/events.py and core/calendar_feed.py). The RECORDS live
// here because their ports do and an adapter in Insolvia.Data must name them;
// the rules that make and show them (parse, replace, dismissal, event_json,
// the ICS feed) are the API's, in Insolvia.Api/Calendar.

/// <summary>Where an event lives: its firm, and its case when it has one (a firm's own office event has none).</summary>
/// <param name="FirmId">The firm.</param>
/// <param name="CaseId">The case, or null for a firm event.</param>
public sealed record EventScope(string FirmId, string? CaseId = null);

/// <summary>
/// A titled span of time on a case's or the firm's calendar. <see cref="AllDay"/>
/// decides how <see cref="Start"/> and <see cref="End"/> read: form dates
/// (<c>YYYY-MM-DD</c>, <see cref="End"/> inclusive) when all-day, else RFC 3339
/// instants normalised to UTC with a literal Z (<c>YYYY-MM-DDTHH:MM:SSZ</c>) —
/// kept as strings so the wire and both stores agree byte for byte.
/// </summary>
/// <param name="Id">The server-minted uuid.</param>
/// <param name="FirmId">The owning firm.</param>
/// <param name="CaseId">The case, or null for a firm event.</param>
/// <param name="Title">The title.</param>
/// <param name="Start">The start (a date or an instant).</param>
/// <param name="End">The end (a date or an instant).</param>
/// <param name="AllDay">Whether the span is whole days.</param>
/// <param name="CreatedBy">Who made it (a subject, or the deadline engine's actor).</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="UpdatedAt">When it was last written.</param>
public sealed record CalendarEvent(
    string Id,
    string FirmId,
    string? CaseId,
    string Title,
    string Start,
    string End,
    bool AllDay,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Free text.</summary>
    public string? Description { get; init; }

    /// <summary>Where.</summary>
    public string? Location { get; init; }

    /// <summary>The firm users attending, by subject, in the order given.</summary>
    public ValueCollection<string> Attendees { get; init; } = [];

    /// <summary>The deadline rule that generated it; null for a hand-made event.</summary>
    public string? RuleId { get; init; }

    /// <summary>The rule's citation, with <see cref="RuleId"/>.</summary>
    public string? RuleCitation { get; init; }

    /// <summary>Whether someone dismissed it — survives regeneration.</summary>
    public bool Dismissed { get; init; }

    /// <summary>Whether the deadline engine made it (Python's <c>generated</c>).</summary>
    public bool Generated => RuleId is not null;

    /// <summary>Where it lives.</summary>
    public EventScope Scope => new(FirmId, CaseId);
}

/// <summary>A person's calendar-feed secret, as its SHA-256 hex — the secret itself is never stored.</summary>
/// <param name="FirmId">The firm.</param>
/// <param name="Subject">The person.</param>
/// <param name="SecretHash">Lower-case hex SHA-256 of the secret.</param>
/// <param name="CreatedAt">When it was minted.</param>
public sealed record CalendarToken(string FirmId, string Subject, string SecretHash, DateTimeOffset CreatedAt);

/// <summary>The ordering and window rules both event stores share (Python's <c>sort_instant</c>, <c>list_order</c>).</summary>
public static class CalendarEvents
{
    /// <summary>
    /// The longest an event may run, in days — LOAD-BEARING: the calendar's
    /// index is keyed on START, so a window query looks back this far and
    /// filters on the end.
    /// </summary>
    public const int MaxEventSpanDays = 62;

    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>The one key both stores order and range-query on: an all-day event at the start of its first day, a timed one at its instant.</summary>
    public static DateTimeOffset SortInstant(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return calendarEvent.AllDay
            ? new DateTimeOffset(ParseDay(calendarEvent.Start).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : ParseInstant(calendarEvent.Start);
    }

    /// <summary>The instant an event is over — the END of its last day when all-day.</summary>
    public static DateTimeOffset EndInstant(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return calendarEvent.AllDay
            ? new DateTimeOffset(ParseDay(calendarEvent.End).ToDateTime(new TimeOnly(23, 59, 59)), TimeSpan.Zero)
            : ParseInstant(calendarEvent.End);
    }

    /// <summary>Start order, id as the tiebreak — one definition for both stores.</summary>
    public static IOrderedEnumerable<CalendarEvent> InListOrder(IEnumerable<CalendarEvent> events) =>
        events.OrderBy(SortInstant).ThenBy(calendarEvent => calendarEvent.Id, StringComparer.Ordinal);

    /// <summary>A form date.</summary>
    public static DateOnly ParseDay(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A stored instant (<c>YYYY-MM-DDTHH:MM:SSZ</c>).</summary>
    public static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.ParseExact(value, InstantFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>An instant in the stored form.</summary>
    public static string FormatInstant(DateTimeOffset value) => value.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    /// <summary>A day in the stored form.</summary>
    public static string FormatDay(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
