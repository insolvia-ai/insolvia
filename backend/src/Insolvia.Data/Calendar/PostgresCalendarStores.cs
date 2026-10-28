using Insolvia.Core;
using Insolvia.Core.Events;
using Insolvia.Core.Ports;
using Insolvia.Data.Cases;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Calendar;

/// <summary>
/// The Postgres <see cref="ICalendarEventStore"/> (Python's <c>DynamoDbEventStore</c>)
/// over <c>calendar_events</c>. The firm's calendar is one range query on
/// (<c>firm_id</c>, <c>sort_at</c>) — the GSI the Python rode, as an index.
/// Every write names the scope: a row is found only under its own firm AND
/// case (or no case), so an id from elsewhere does not resolve.
/// </summary>
public sealed class PostgresCalendarEventStore(InsolviaDbContext db) : ICalendarEventStore
{
    private const string PrimaryKey = "pk_calendar_events";

    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public Task CreateAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return _db.InsertOnceAsync(ToRow(calendarEvent), PrimaryKey, () => new InvalidOperationException("event id already exists in this firm"), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CalendarEvent?> GetAsync(EventScope scope, string eventId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var row = await InScope(scope.FirmId, scope.CaseId, eventId).AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<bool> PutAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var row = ToRow(calendarEvent);
        var written = await InScope(row.FirmId, row.CaseId, row.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.Title, row.Title)
                    .SetProperty(stored => stored.Description, row.Description)
                    .SetProperty(stored => stored.AllDay, row.AllDay)
                    .SetProperty(stored => stored.StartsOn, row.StartsOn)
                    .SetProperty(stored => stored.EndsOn, row.EndsOn)
                    .SetProperty(stored => stored.StartsAt, row.StartsAt)
                    .SetProperty(stored => stored.EndsAt, row.EndsAt)
                    .SetProperty(stored => stored.SortAt, row.SortAt)
                    .SetProperty(stored => stored.Location, row.Location)
                    .SetProperty(stored => stored.Attendees, row.Attendees)
                    .SetProperty(stored => stored.RuleId, row.RuleId)
                    .SetProperty(stored => stored.RuleCitation, row.RuleCitation)
                    .SetProperty(stored => stored.Dismissed, row.Dismissed)
                    .SetProperty(stored => stored.CreatedBy, row.CreatedBy)
                    .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
                    .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt),
                cancellationToken)
            .ConfigureAwait(false);
        return written > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(EventScope scope, string eventId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return await InScope(scope.FirmId, scope.CaseId, eventId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarEvent>> ListForScopeAsync(EventScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var caseId = scope.CaseId;
        var rows = await _db.Set<CalendarEventRow>().AsNoTracking()
            .Where(row => row.FirmId == scope.FirmId && (caseId == null ? row.CaseId == null : row.CaseId == caseId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. CalendarEvents.InListOrder(rows.Select(ToDomain))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarEvent>> ListForFirmAsync(string firmId, DateTimeOffset startingFrom, DateTimeOffset until, CancellationToken cancellationToken = default)
    {
        var lower = startingFrom.ToUniversalTime();
        var upper = until.ToUniversalTime();
        var rows = await _db.Set<CalendarEventRow>().AsNoTracking()
            .Where(row => row.FirmId == firmId && row.SortAt >= lower && row.SortAt <= upper)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. CalendarEvents.InListOrder(rows.Select(ToDomain))];
    }

    private IQueryable<CalendarEventRow> InScope(string firmId, string? caseId, string eventId) =>
        _db.Set<CalendarEventRow>().Where(row => row.FirmId == firmId && row.Id == eventId && (caseId == null ? row.CaseId == null : row.CaseId == caseId));

    private static CalendarEventRow ToRow(CalendarEvent calendarEvent) => new()
    {
        FirmId = calendarEvent.FirmId,
        Id = calendarEvent.Id,
        CaseId = calendarEvent.CaseId,
        Title = calendarEvent.Title,
        Description = calendarEvent.Description,
        AllDay = calendarEvent.AllDay,
        StartsOn = calendarEvent.AllDay ? CalendarEvents.ParseDay(calendarEvent.Start) : null,
        EndsOn = calendarEvent.AllDay ? CalendarEvents.ParseDay(calendarEvent.End) : null,
        StartsAt = calendarEvent.AllDay ? null : CalendarEvents.ParseInstant(calendarEvent.Start),
        EndsAt = calendarEvent.AllDay ? null : CalendarEvents.ParseInstant(calendarEvent.End),
        SortAt = CalendarEvents.SortInstant(calendarEvent),
        Location = calendarEvent.Location,
        Attendees = [.. calendarEvent.Attendees],
        RuleId = calendarEvent.RuleId,
        RuleCitation = calendarEvent.RuleCitation,
        Dismissed = calendarEvent.Dismissed,
        CreatedBy = calendarEvent.CreatedBy,
        CreatedAt = calendarEvent.CreatedAt,
        UpdatedAt = calendarEvent.UpdatedAt,
    };

    private static CalendarEvent ToDomain(CalendarEventRow row) =>
        new(
            row.Id,
            row.FirmId,
            row.CaseId,
            row.Title,
            row.AllDay ? CalendarEvents.FormatDay(row.StartsOn!.Value) : CalendarEvents.FormatInstant(row.StartsAt!.Value),
            row.AllDay ? CalendarEvents.FormatDay(row.EndsOn!.Value) : CalendarEvents.FormatInstant(row.EndsAt!.Value),
            row.AllDay,
            row.CreatedBy,
            row.CreatedAt,
            row.UpdatedAt)
        {
            Description = row.Description,
            Location = row.Location,
            Attendees = row.Attendees.ToValueCollection(),
            RuleId = row.RuleId,
            RuleCitation = row.RuleCitation,
            Dismissed = row.Dismissed,
        };
}

/// <summary>The Postgres <see cref="ICalendarTokenStore"/> over <c>calendar_tokens</c>: one row per (firm, subject).</summary>
public sealed class PostgresCalendarTokenStore(InsolviaDbContext db) : ICalendarTokenStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task PutAsync(CalendarToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        // One statement, so two racing rotations cannot both insert.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO insolvia.calendar_tokens (firm_id, subject, secret_hash, created_at)
            VALUES ({token.FirmId}, {token.Subject}, {token.SecretHash}, {Inserts.Utc(token.CreatedAt)})
            ON CONFLICT (firm_id, subject) DO UPDATE SET secret_hash = EXCLUDED.secret_hash, created_at = EXCLUDED.created_at
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CalendarToken?> GetAsync(string firmId, string subject, CancellationToken cancellationToken = default)
    {
        var row = await Row(firmId, subject).AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : new CalendarToken(row.FirmId, row.Subject, row.SecretHash, row.CreatedAt);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string firmId, string subject, CancellationToken cancellationToken = default) =>
        await Row(firmId, subject).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;

    private IQueryable<CalendarTokenRow> Row(string firmId, string subject) =>
        _db.Set<CalendarTokenRow>().Where(row => row.FirmId == firmId && row.Subject == subject);
}
