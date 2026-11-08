using Insolvia.Core.Events;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists calendar events (issue 14.6 / #358; Python's <c>EventStore</c>) —
/// a case's and the firm's own, under one firm-keyed calendar. Takes NO
/// accessor: a case event is reached through its case (resolved through
/// <see cref="ICaseStore"/> first), a firm event through the caller's firm,
/// and the calendar read is filtered by the route against the cases the
/// caller may see. One authorisation path.
/// </summary>
public interface ICalendarEventStore
{
    /// <summary>Store a new event; an existing (scope, id) means the minting is broken — MUST throw.</summary>
    Task CreateAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default);

    /// <summary>One event in <paramref name="scope"/> — a firm event is not found under a case scope, nor a case's under another case.</summary>
    Task<CalendarEvent?> GetAsync(EventScope scope, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Write back over a row that still exists in the same scope. False when it is gone — an edit racing a delete must not resurrect it.</summary>
    Task<bool> PutAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default);

    /// <summary>Remove the event. True if this call removed it.</summary>
    Task<bool> DeleteAsync(EventScope scope, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Every event of one case (or the firm's own, case-less ones), in start order (<see cref="CalendarEvents.InListOrder"/>). All of them.</summary>
    Task<IReadOnlyList<CalendarEvent>> ListForScopeAsync(EventScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every event of the firm — case and firm events alike — whose
    /// <see cref="CalendarEvents.SortInstant"/> lies in [<paramref name="startingFrom"/>,
    /// <paramref name="until"/>], in start order. The caller widens the lower
    /// bound by <see cref="CalendarEvents.MaxEventSpanDays"/> and filters on the end.
    /// </summary>
    Task<IReadOnlyList<CalendarEvent>> ListForFirmAsync(string firmId, DateTimeOffset startingFrom, DateTimeOffset until, CancellationToken cancellationToken = default);
}

/// <summary>Persists the per-person feed secret's hash (Python's <c>CalendarTokenStore</c>): one per (firm, subject), replaced on rotation.</summary>
public interface ICalendarTokenStore
{
    /// <summary>Write the token, replacing any for the same person.</summary>
    Task PutAsync(CalendarToken token, CancellationToken cancellationToken = default);

    /// <summary>The person's token, or null.</summary>
    Task<CalendarToken?> GetAsync(string firmId, string subject, CancellationToken cancellationToken = default);

    /// <summary>Remove the token. True if this call removed it.</summary>
    Task<bool> DeleteAsync(string firmId, string subject, CancellationToken cancellationToken = default);
}
