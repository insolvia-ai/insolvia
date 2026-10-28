using Insolvia.Core.Events;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="ICalendarEventStore"/>. Keyed by (firm, id) as the
/// table is; a read or write names its scope and must match the stored one,
/// so a firm event is not found under a case and a case's not under another.
/// </summary>
public sealed class MemoryCalendarEventStore : ICalendarEventStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string FirmId, string Id), CalendarEvent> _rows = [];

    /// <inheritdoc />
    public Task CreateAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        lock (_gate)
        {
            if (!_rows.TryAdd((calendarEvent.FirmId, calendarEvent.Id), calendarEvent))
            {
                throw new InvalidOperationException($"event {calendarEvent.Id} already exists");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<CalendarEvent?> GetAsync(EventScope scope, string eventId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            var found = _rows.GetValueOrDefault((scope.FirmId, eventId));
            return Task.FromResult(found is not null && found.CaseId == scope.CaseId ? found : null);
        }
    }

    /// <inheritdoc />
    public Task<bool> PutAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        lock (_gate)
        {
            var key = (calendarEvent.FirmId, calendarEvent.Id);
            if (!_rows.TryGetValue(key, out var stored) || stored.CaseId != calendarEvent.CaseId)
            {
                return Task.FromResult(false);
            }

            _rows[key] = calendarEvent;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(EventScope scope, string eventId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            var key = (scope.FirmId, eventId);
            return Task.FromResult(_rows.TryGetValue(key, out var stored) && stored.CaseId == scope.CaseId && _rows.Remove(key));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CalendarEvent>> ListForScopeAsync(EventScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            IReadOnlyList<CalendarEvent> listed = [.. CalendarEvents.InListOrder(_rows.Values.Where(stored => stored.FirmId == scope.FirmId && stored.CaseId == scope.CaseId))];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CalendarEvent>> ListForFirmAsync(string firmId, DateTimeOffset startingFrom, DateTimeOffset until, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<CalendarEvent> listed = [.. CalendarEvents.InListOrder(_rows.Values.Where(stored =>
                stored.FirmId == firmId
                && CalendarEvents.SortInstant(stored) >= startingFrom
                && CalendarEvents.SortInstant(stored) <= until))];
            return Task.FromResult(listed);
        }
    }
}

/// <summary>The in-memory <see cref="ICalendarTokenStore"/>.</summary>
public sealed class MemoryCalendarTokenStore : ICalendarTokenStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string FirmId, string Subject), CalendarToken> _rows = [];

    /// <inheritdoc />
    public Task PutAsync(CalendarToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            _rows[(token.FirmId, token.Subject)] = token;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<CalendarToken?> GetAsync(string firmId, string subject, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.GetValueOrDefault((firmId, subject)));
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string firmId, string subject, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Remove((firmId, subject)));
        }
    }
}
