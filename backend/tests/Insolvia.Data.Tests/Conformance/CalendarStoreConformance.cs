using Insolvia.Core;
using Insolvia.Core.Events;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data.Calendar;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What both calendar stores must do (Python's EventStore and
/// CalendarTokenStore): once-only create, scope-exact reads and writes, a
/// write-back only over a row that still exists, start order with an id
/// tiebreak, the firm window on the sort instant, and one token per person.
/// </summary>
public abstract class CalendarStoreConformance : CaseConformance
{
    private protected abstract ICalendarEventStore Events();

    private protected abstract ICalendarTokenStore Tokens();

    [Fact]
    public async Task An_event_round_trips_and_is_created_once()
    {
        var owner = await CreateCaseAsync();
        var timed = Timed(owner.Id, "2026-03-02T15:00:00Z", "2026-03-02T16:30:00Z") with
        {
            Description = "Bring the statements.",
            Location = "Room 2",
            Attendees = new[] { Ids.Alice, Ids.Bob }.ToValueCollection(),
            RuleId = "341-meeting",
            RuleCitation = "11 U.S.C. § 341",
            Dismissed = true,
        };
        var allDay = AllDay(null, "2026-03-14", "2026-03-15");
        await Events().CreateAsync(timed, Ct);
        await Events().CreateAsync(allDay, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Events().CreateAsync(timed with { Title = "Again" }, Ct));

        Assert.Equal(timed, await Events().GetAsync(new EventScope(Ids.Firm, owner.Id), timed.Id, Ct));
        Assert.Equal(allDay, await Events().GetAsync(new EventScope(Ids.Firm), allDay.Id, Ct));
    }

    [Fact]
    public async Task An_event_resolves_only_in_its_own_scope()
    {
        var owner = await CreateCaseAsync();
        var other = await CreateCaseAsync();
        var caseEvent = Timed(owner.Id, "2026-03-02T15:00:00Z", "2026-03-02T16:00:00Z");
        var firmEvent = AllDay(null, "2026-03-03", "2026-03-03");
        await Events().CreateAsync(caseEvent, Ct);
        await Events().CreateAsync(firmEvent, Ct);

        Assert.Null(await Events().GetAsync(new EventScope(Ids.Firm, other.Id), caseEvent.Id, Ct));
        Assert.Null(await Events().GetAsync(new EventScope(Ids.Firm), caseEvent.Id, Ct));
        Assert.Null(await Events().GetAsync(new EventScope(Ids.Firm, owner.Id), firmEvent.Id, Ct));
        Assert.Null(await Events().GetAsync(new EventScope(Ids.OtherFirm), firmEvent.Id, Ct));
        Assert.False(await Events().DeleteAsync(new EventScope(Ids.Firm, other.Id), caseEvent.Id, Ct));
        Assert.False(await Events().PutAsync(caseEvent with { CaseId = other.Id }, Ct));
        Assert.Equal([caseEvent], await Events().ListForScopeAsync(new EventScope(Ids.Firm, owner.Id), Ct));
        Assert.Equal([firmEvent], await Events().ListForScopeAsync(new EventScope(Ids.Firm), Ct));
        Assert.Empty(await Events().ListForScopeAsync(new EventScope(Ids.Firm, other.Id), Ct));
    }

    [Fact]
    public async Task A_put_writes_back_only_while_the_event_exists_and_a_delete_answers_once()
    {
        var calendarEvent = AllDay(null, "2026-04-01", "2026-04-02");
        await Events().CreateAsync(calendarEvent, Ct);
        var moved = calendarEvent with { Title = "Moved", Start = "2026-04-03T09:00:00Z", End = "2026-04-03T10:00:00Z", AllDay = false, UpdatedAt = Opened.AddHours(1) };

        Assert.True(await Events().PutAsync(moved, Ct));
        Assert.Equal(moved, await Events().GetAsync(new EventScope(Ids.Firm), calendarEvent.Id, Ct));
        Assert.True(await Events().DeleteAsync(new EventScope(Ids.Firm), calendarEvent.Id, Ct));
        Assert.False(await Events().DeleteAsync(new EventScope(Ids.Firm), calendarEvent.Id, Ct));
        Assert.False(await Events().PutAsync(moved, Ct));
        Assert.Null(await Events().GetAsync(new EventScope(Ids.Firm), calendarEvent.Id, Ct));
    }

    [Fact]
    public async Task A_scope_lists_in_start_order_with_the_id_as_tiebreak()
    {
        var later = Timed(null, "2026-05-02T09:00:00Z", "2026-05-02T10:00:00Z", "00000000-0000-4000-8000-0000000000e3");
        var tieB = AllDay(null, "2026-05-01", "2026-05-01", "00000000-0000-4000-8000-0000000000e2");
        var tieA = Timed(null, "2026-05-01T00:00:00Z", "2026-05-01T01:00:00Z", "00000000-0000-4000-8000-0000000000e1");
        foreach (var calendarEvent in new[] { later, tieB, tieA })
        {
            await Events().CreateAsync(calendarEvent, Ct);
        }

        Assert.Equal([tieA, tieB, later], await Events().ListForScopeAsync(new EventScope(Ids.Firm), Ct));
    }

    [Fact]
    public async Task The_firm_window_reads_both_kinds_by_sort_instant_inclusive_and_no_other_firm()
    {
        var owner = await CreateCaseAsync();
        var before = AllDay(null, "2026-06-01", "2026-06-30");
        var atLower = Timed(owner.Id, "2026-06-02T00:00:00Z", "2026-06-02T01:00:00Z");
        var allDayInside = AllDay(owner.Id, "2026-06-05", "2026-06-05");
        var atUpper = Timed(null, "2026-06-10T23:59:59Z", "2026-06-11T00:30:00Z");
        var after = AllDay(null, "2026-06-11", "2026-06-11");
        var elsewhere = AllDay(null, "2026-06-05", "2026-06-05", firmId: Ids.OtherFirm);
        foreach (var calendarEvent in new[] { before, atLower, allDayInside, atUpper, after, elsewhere })
        {
            await Events().CreateAsync(calendarEvent, Ct);
        }

        var window = await Events().ListForFirmAsync(
            Ids.Firm,
            new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 10, 23, 59, 59, TimeSpan.Zero),
            Ct);

        Assert.Equal([atLower, allDayInside, atUpper], window);
    }

    [Fact]
    public async Task A_token_is_one_per_person_replaced_on_rotation_and_revoked_once()
    {
        var first = new CalendarToken(Ids.Firm, Ids.Alice, new string('a', 64), Opened);
        var rotated = new CalendarToken(Ids.Firm, Ids.Alice, new string('b', 64), Opened.AddHours(1));
        await Tokens().PutAsync(first, Ct);
        await Tokens().PutAsync(rotated, Ct);
        await Tokens().PutAsync(new CalendarToken(Ids.Firm, Ids.Bob, new string('c', 64), Opened), Ct);

        Assert.Equal(rotated, await Tokens().GetAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Null(await Tokens().GetAsync(Ids.OtherFirm, Ids.Alice, Ct));
        Assert.True(await Tokens().DeleteAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.False(await Tokens().DeleteAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Null(await Tokens().GetAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.NotNull(await Tokens().GetAsync(Ids.Firm, Ids.Bob, Ct));
    }

    private static CalendarEvent Timed(string? caseId, string start, string end, string? id = null, string firmId = Ids.Firm) =>
        new(id ?? NewId(), firmId, caseId, "Hearing", start, end, AllDay: false, Ids.Alice, Opened, Opened);

    private static CalendarEvent AllDay(string? caseId, string start, string end, string? id = null, string firmId = Ids.Firm) =>
        new(id ?? NewId(), firmId, caseId, "Office closed", start, end, AllDay: true, Ids.Alice, Opened, Opened);

}

/// <summary>The oracles: the memory calendar stores.</summary>
public sealed class MemoryCalendarStoreConformance : CalendarStoreConformance
{
    private readonly MemoryCaseWorld _world = new();
    private readonly MemoryCalendarEventStore _events = new();
    private readonly MemoryCalendarTokenStore _tokens = new();

    private protected override CaseStores Connect() => _world.Connect();

    private protected override ICalendarEventStore Events() => _events;

    private protected override ICalendarTokenStore Tokens() => _tokens;
}

/// <summary>The Postgres calendar stores on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresCalendarStoreConformance(PostgresFixture postgres) : CalendarStoreConformance
{
    private readonly PostgresCaseWorld _world = new(postgres);

    public override async ValueTask InitializeAsync()
    {
        await _world.InitializeAsync();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _world.DisposeAsync();
        await base.DisposeAsync();
    }

    private protected override CaseStores Connect() => _world.Connect();

    private protected override ICalendarEventStore Events() => new PostgresCalendarEventStore(_world.Harness.NewContext());

    private protected override ICalendarTokenStore Tokens() => new PostgresCalendarTokenStore(_world.Harness.NewContext());
}
