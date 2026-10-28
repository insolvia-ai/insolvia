using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.Cases;
using Insolvia.Core.Clients;
using Insolvia.Core.Debtors;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What every <see cref="Core.Ports.ICaseStore"/> must do, against the memory twin and
/// Postgres: the create transaction and its client conditions, the access
/// rule on every read, the two listings and their cursors, the status
/// compare-and-swap, and the lifecycle write (ADR 0027) with each of its
/// refusals — plus the race that only one of two writers from one read wins.
/// </summary>
public abstract class CaseStoreConformance : CaseConformance
{
    // ── Create ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_case_is_written_with_its_creator_s_link_and_its_debtors_in_one_go()
    {
        var client = await ClientAsync();
        var created = Make.Case(NewId());
        var debtor = MakeDebtor(created, clientId: client.Id);
        await Connect().Cases.CreateAsync(created, Make.Assignment(created), [debtor], Ct);

        var stores = Connect();
        Assert.Equal(created, await stores.Cases.GetAsync(created.Id, Staff(), Ct));
        Assert.Equal([Ids.Alice], (await stores.Cases.AssigneesAsync(created.Id, Ct)).Select(link => link.Subject));
        Assert.Equal([debtor], await stores.Debtors.ListForCaseAsync(created.Id, Ct));
        Assert.Equal([new ClientCase(created, FilingRole.Debtor1)], await stores.Cases.ListForClientAsync(client.Id, Staff(), Ct));
    }

    [Fact]
    public async Task A_case_id_is_never_written_twice()
    {
        var created = await CreateCaseAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Cases.CreateAsync(created with { Chapter = 13 }, Make.Assignment(created), cancellationToken: Ct));
        Assert.Equal(7, (await Connect().Cases.ReadForWorkerAsync(created.Id, Ct))!.Chapter);
    }

    [Fact]
    public async Task A_client_that_is_no_longer_linkable_refuses_the_whole_create_and_is_named()
    {
        var linkable = await ClientAsync(surname: "Linkable");
        var archived = await ClientAsync(surname: "Archived");
        await ArchiveAsync(archived);
        var created = Make.Case(NewId());

        var error = await Assert.ThrowsAsync<ClientUnavailableException>(() => Connect().Cases.CreateAsync(
            created,
            Make.Assignment(created),
            [MakeDebtor(created, FilingRole.Debtor1, linkable.Id), MakeDebtor(created, FilingRole.Debtor2, archived.Id)],
            Ct));

        Assert.Equal(archived.Id, error.ClientId);
        var stores = Connect();
        Assert.Null(await stores.Cases.ReadForWorkerAsync(created.Id, Ct));
        Assert.Empty(await stores.Debtors.ListForCaseAsync(created.Id, Ct));
        Assert.Empty(await stores.Debtors.RolesForClientAsync(linkable.Id, Ct));
    }

    [Fact]
    public async Task A_client_of_another_firm_is_not_linkable()
    {
        var theirs = await ClientAsync(Ids.OtherFirm);
        var created = Make.Case(NewId());
        await Assert.ThrowsAsync<ClientUnavailableException>(() =>
            Connect().Cases.CreateAsync(created, Make.Assignment(created), [MakeDebtor(created, clientId: theirs.Id)], Ct));
        Assert.Null(await Connect().Cases.ReadForWorkerAsync(created.Id, Ct));
    }

    [Fact]
    public async Task A_debtor_of_another_case_is_refused_before_anything_is_written()
    {
        var created = Make.Case(NewId());
        var stray = MakeDebtor(Make.Case(NewId()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Cases.CreateAsync(created, Make.Assignment(created), [stray], Ct));
        Assert.Null(await Connect().Cases.ReadForWorkerAsync(created.Id, Ct));
    }

    // ── Reads and the access rule ───────────────────────────────────

    [Fact]
    public async Task A_case_is_visible_to_its_firm_s_admin_and_to_those_linked_and_to_nobody_else()
    {
        var created = await CreateCaseAsync();
        var cases = Connect().Cases;
        Assert.NotNull(await cases.GetAsync(created.Id, Admin(), Ct));
        Assert.NotNull(await cases.GetAsync(created.Id, Staff(Ids.Alice), Ct));
        Assert.Null(await cases.GetAsync(created.Id, Staff(Ids.Kim), Ct));
        Assert.Null(await cases.GetAsync(created.Id, Admin(Ids.OtherFirm), Ct));
        Assert.Null(await cases.GetAsync(NewId(), Admin(), Ct));

        await cases.AssignAsync(new CaseAssignment(created.Id, Ids.Kim, created.CreatedAt, Opened, Ids.Bob), Ct);
        Assert.NotNull(await Connect().Cases.GetAsync(created.Id, Staff(Ids.Kim), Ct));
    }

    [Fact]
    public async Task A_deleted_case_is_gone_from_every_read_but_the_worker_s()
    {
        var created = await CreateCaseAsync();
        await Connect().Cases.UpdateAsync(CaseRules.MarkDeleted(created, Ids.Alice), CaseStatus.Intake, Ct);
        var cases = Connect().Cases;
        Assert.Null(await cases.GetAsync(created.Id, Admin(), Ct));
        Assert.Empty((await cases.ListForAccessorAsync(Admin(), 10, null, cancellationToken: Ct)).Cases);
        Assert.True((await cases.ReadForWorkerAsync(created.Id, Ct))!.Deleted);
    }

    [Fact]
    public async Task The_client_s_read_is_its_case_s_chapter_and_stage_and_only_in_its_firm()
    {
        var created = await CreateCaseAsync(chapter: 13);
        var cases = Connect().Cases;
        var binding = Make.Binding(caseId: created.Id);
        Assert.Equal(new CasePublicStatus(13, CaseStatus.Intake), await cases.PublicStatusAsync(new ClientAccessor(Make.Firm(), binding), Ct));
        Assert.Null(await cases.PublicStatusAsync(new ClientAccessor(Make.Firm(Ids.OtherFirm), binding with { FirmId = Ids.OtherFirm }), Ct));
        Assert.Null(await cases.PublicStatusAsync(new ClientAccessor(Make.Firm(), binding with { CaseId = NewId() }), Ct));
    }

    [Fact]
    public async Task A_client_s_cases_are_newest_first_and_only_those_the_caller_may_see()
    {
        var client = await ClientAsync();
        var older = Make.Case(NewId(), createdAt: Opened);
        var newer = Make.Case(NewId(), createdAt: Opened.AddDays(1));
        var unlinked = Make.Case(NewId(), createdAt: Opened.AddDays(2));
        await Connect().Cases.CreateAsync(older, Make.Assignment(older), [MakeDebtor(older, clientId: client.Id)], Ct);
        await Connect().Cases.CreateAsync(newer, Make.Assignment(newer), [MakeDebtor(newer, FilingRole.Debtor2, client.Id)], Ct);
        await Connect().Cases.CreateAsync(unlinked, Make.Assignment(unlinked, Ids.Kim), [MakeDebtor(unlinked, clientId: client.Id)], Ct);

        var mine = await Connect().Cases.ListForClientAsync(client.Id, Staff(Ids.Alice), Ct);
        Assert.Equal([(newer.Id, FilingRole.Debtor2), (older.Id, FilingRole.Debtor1)], mine.Select(entry => (entry.Case.Id, entry.FilingRole)));
        Assert.Equal(3, (await Connect().Cases.ListForClientAsync(client.Id, Admin(), Ct)).Count);
        Assert.Empty(await Connect().Cases.ListForClientAsync(client.Id, Admin(Ids.OtherFirm), Ct));
    }

    // ── Listings ────────────────────────────────────────────────────

    [Fact]
    public async Task The_firm_listing_pages_newest_first_and_ties_break_on_the_id()
    {
        var created = new List<Case>();
        for (var i = 0; i < 5; i++)
        {
            created.Add(await CreateCaseAsync(createdAt: Opened.AddMinutes(i % 3)));
        }

        var expected = created
            .OrderByDescending(found => CaseRules.ListingSortKey(found.CreatedAt, found.Id), StringComparer.Ordinal)
            .Select(found => found.Id)
            .ToList();
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await Connect().Cases.ListForAccessorAsync(Admin(), 2, cursor, cancellationToken: Ct);
            Assert.True(page.Cases.Count <= 2);
            seen.AddRange(page.Cases.Select(found => found.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task The_working_list_and_the_archive_are_two_views_and_a_page_is_never_short_for_the_other()
    {
        var cases = new List<Case>();
        for (var i = 0; i < 6; i++)
        {
            cases.Add(await CreateCaseAsync(createdAt: Opened.AddMinutes(i)));
        }

        // The three newest archived: the working list's first page must still be full.
        foreach (var archived in cases.Skip(3))
        {
            await Connect().Cases.UpdateAsync(CaseRules.SetArchived(archived, true, Ids.Alice), CaseStatus.Intake, Ct);
        }

        var working = await Connect().Cases.ListForAccessorAsync(Admin(), 2, null, cancellationToken: Ct);
        Assert.Equal([cases[2].Id, cases[1].Id], working.Cases.Select(found => found.Id));
        var archive = await Connect().Cases.ListForAccessorAsync(Admin(), 10, null, archived: true, cancellationToken: Ct);
        Assert.Equal([cases[5].Id, cases[4].Id, cases[3].Id], archive.Cases.Select(found => found.Id));
        Assert.Null(archive.NextCursor);
    }

    [Fact]
    public async Task A_cursor_is_refused_by_another_view_another_index_and_when_it_is_not_one()
    {
        for (var i = 0; i < 3; i++)
        {
            var created = await CreateCaseAsync(createdAt: Opened.AddMinutes(i));
            await Connect().Cases.UpdateAsync(CaseRules.SetArchived(created, true, Ids.Alice), CaseStatus.Intake, Ct);
            await CreateCaseAsync(createdAt: Opened.AddMinutes(i));
        }

        var archive = await Connect().Cases.ListForAccessorAsync(Admin(), 1, null, archived: true, cancellationToken: Ct);
        var byFirm = await Connect().Cases.ListForAccessorAsync(Admin(), 1, null, cancellationToken: Ct);
        Assert.NotNull(archive.NextCursor);
        Assert.NotNull(byFirm.NextCursor);
        await Assert.ThrowsAsync<ValidationException>(() => Connect().Cases.ListForAccessorAsync(Admin(), 1, archive.NextCursor, cancellationToken: Ct));
        await Assert.ThrowsAsync<ValidationException>(() => Connect().Cases.ListForAccessorAsync(Staff(), 1, byFirm.NextCursor, cancellationToken: Ct));
        await Assert.ThrowsAsync<ValidationException>(() => Connect().Cases.ListForAccessorAsync(Admin(), 1, "not-a-cursor", cancellationToken: Ct));
    }

    [Fact]
    public async Task The_assignee_listing_holds_only_the_caller_s_cases_in_their_firm()
    {
        var mine = await CreateCaseAsync(createdAt: Opened);
        var theirs = Make.Case(NewId(), createdAt: Opened.AddDays(1));
        await Connect().Cases.CreateAsync(theirs, Make.Assignment(theirs, Ids.Kim), cancellationToken: Ct);
        await CreateCaseAsync(Ids.OtherFirm);

        var page = await Connect().Cases.ListForAccessorAsync(Staff(Ids.Alice), 10, null, cancellationToken: Ct);
        Assert.Equal([mine.Id], page.Cases.Select(found => found.Id));
        Assert.Equal(2, (await Connect().Cases.ListForAccessorAsync(Admin(), 10, null, cancellationToken: Ct)).Cases.Count);
    }

    // ── Assignments ─────────────────────────────────────────────────

    [Fact]
    public async Task Linking_is_idempotent_unlinking_answers_once_and_links_list_oldest_first()
    {
        var created = await CreateCaseAsync();
        var cases = Connect().Cases;
        await cases.AssignAsync(new CaseAssignment(created.Id, Ids.Kim, created.CreatedAt, Opened.AddDays(2), Ids.Bob), Ct);
        await cases.AssignAsync(new CaseAssignment(created.Id, Ids.Pat, created.CreatedAt, Opened.AddDays(1), Ids.Bob), Ct);
        await cases.AssignAsync(new CaseAssignment(created.Id, Ids.Pat, created.CreatedAt, Opened.AddDays(3), Ids.Alice), Ct);

        var links = await Connect().Cases.AssigneesAsync(created.Id, Ct);
        Assert.Equal([Ids.Alice, Ids.Kim, Ids.Pat], links.Select(link => link.Subject));
        Assert.Equal(Ids.Alice, links[2].AssignedBy);
        Assert.True(await Connect().Cases.UnassignAsync(created.Id, Ids.Kim, Ct));
        Assert.False(await Connect().Cases.UnassignAsync(created.Id, Ids.Kim, Ct));
    }

    // ── Plain updates ───────────────────────────────────────────────

    [Fact]
    public async Task An_edit_is_written_back_only_in_its_firm()
    {
        var created = await CreateCaseAsync();
        var edited = created with { Judge = "Hon. Example", UpdatedAt = Opened.AddHours(1) };
        Assert.Equal(edited, await Connect().Cases.UpdateAsync(edited, CaseStatus.Intake, Ct));
        Assert.Equal(edited, await Connect().Cases.ReadForWorkerAsync(created.Id, Ct));
        Assert.Null(await Connect().Cases.UpdateAsync(edited with { FirmId = Ids.OtherFirm, Judge = "X" }, cancellationToken: Ct));
        Assert.Null(await Connect().Cases.UpdateAsync(Make.Case(NewId()), cancellationToken: Ct));
        Assert.Equal("Hon. Example", (await Connect().Cases.ReadForWorkerAsync(created.Id, Ct))!.Judge);
    }

    [Fact]
    public async Task An_edit_read_before_a_status_move_is_refused_and_an_edit_never_moves_the_status()
    {
        var created = await CreateCaseAsync();
        await Connect().Cases.RecordAsync(Move(created, CaseStatus.ReadyToFile, []), Ct);

        var stale = await Assert.ThrowsAsync<ConflictException>(() => Connect().Cases.UpdateAsync(created with { Judge = "J" }, CaseStatus.Intake, Ct));
        Assert.Equal(CaseRules.StatusMoved, stale.Message);
        var sneaky = await Assert.ThrowsAsync<ConflictException>(() => Connect().Cases.UpdateAsync(created with { Status = CaseStatus.Intake }, cancellationToken: Ct));
        Assert.Equal(CaseRules.StatusMoved, sneaky.Message);
        Assert.Equal(CaseStatus.ReadyToFile, (await Connect().Cases.ReadForWorkerAsync(created.Id, Ct))!.Status);
    }

    // ── The lifecycle write (ADR 0027) ──────────────────────────────

    [Fact]
    public async Task A_status_move_writes_the_case_and_its_event_together()
    {
        var created = await CreateCaseAsync();
        var ready = Move(created, CaseStatus.ReadyToFile, []);
        Assert.Equal(ready.Case, await Connect().Cases.RecordAsync(ready, Ct));
        var filed = File(ready.Case, [ready.Event]);
        await Connect().Cases.RecordAsync(filed, Ct);

        Assert.Equal(filed.Case, await Connect().Cases.ReadForWorkerAsync(created.Id, Ct));
        var history = await Connect().Cases.LifecycleAsync(created.Id, Ct);
        Assert.Equal([ready.Event, filed.Event], history);
        Assert.Equal(7, LifecyclePeriods.PetitionChapter(history));
        Assert.Empty(await Connect().Cases.LifecycleAsync(NewId(), Ct));
    }

    [Fact]
    public async Task Every_kind_of_event_round_trips_through_the_history()
    {
        var created = await CreateCaseAsync(chapter: 13);
        var filed = File(created, []);
        await Connect().Cases.RecordAsync(filed, Ct);
        var history = await Connect().Cases.LifecycleAsync(created.Id, Ct);
        var denied = CaseLifecycle.Rule(filed.Case, history, false, new DateOnly(2026, 11, 2), Ids.Alice, planVersionId: "plan-1");
        await Connect().Cases.RecordAsync(denied, Ct);
        history = await Connect().Cases.LifecycleAsync(created.Id, Ct);
        var converted = CaseLifecycle.Convert(denied.Case, history, 7, new DateOnly(2026, 12, 1), Ids.Alice, LifecycleProvenance.CourtNotice, candidateId: "candidate-1");
        await Connect().Cases.RecordAsync(converted, Ct);
        history = await Connect().Cases.LifecycleAsync(created.Id, Ct);
        var dismissed = CaseLifecycle.StatusMove(converted.Case, converted.Case with { Status = CaseStatus.Dismissed }, history, Ids.Alice, new DateOnly(2027, 1, 4))!;
        await Connect().Cases.RecordAsync(dismissed, Ct);

        Assert.Equal([filed.Event, denied.Event, converted.Event, dismissed.Event], await Connect().Cases.LifecycleAsync(created.Id, Ct));
        Assert.Equal((7, CaseStatus.Dismissed), ((await Connect().Cases.ReadForWorkerAsync(created.Id, Ct))!.Chapter, (await Connect().Cases.ReadForWorkerAsync(created.Id, Ct))!.Status));
    }

    [Fact]
    public async Task A_lifecycle_write_whose_status_moved_underneath_is_refused_and_writes_nothing()
    {
        var created = await CreateCaseAsync();
        var first = Move(created, CaseStatus.ReadyToFile, []);
        var stale = File(created, []);
        await Connect().Cases.RecordAsync(first, Ct);

        var error = await Assert.ThrowsAsync<ConflictException>(() => Connect().Cases.RecordAsync(stale, Ct));
        Assert.Equal(CaseRules.StatusMoved, error.Message);
        Assert.Equal([first.Event], await Connect().Cases.LifecycleAsync(created.Id, Ct));
        Assert.Equal(CaseStatus.ReadyToFile, (await Connect().Cases.ReadForWorkerAsync(created.Id, Ct))!.Status);
    }

    [Fact]
    public async Task A_lifecycle_write_whose_chapter_moved_underneath_is_refused()
    {
        var created = await CreateCaseAsync();
        var stale = File(created, []);
        await Connect().Cases.UpdateAsync(created with { Chapter = 13 }, CaseStatus.Intake, Ct);

        var error = await Assert.ThrowsAsync<ConflictException>(() => Connect().Cases.RecordAsync(stale, Ct));
        Assert.Equal(CaseRules.StatusMoved, error.Message);
        Assert.Empty(await Connect().Cases.LifecycleAsync(created.Id, Ct));
    }

    [Fact]
    public async Task A_lifecycle_write_raced_by_another_event_is_refused_as_a_moved_history()
    {
        // Two rulings from one read: neither moves the status or the
        // chapter, so only the history's version can tell them apart.
        var created = await CreateCaseAsync(chapter: 13);
        var filed = File(created, []);
        await Connect().Cases.RecordAsync(filed, Ct);
        var history = await Connect().Cases.LifecycleAsync(created.Id, Ct);
        var confirmed = CaseLifecycle.Rule(filed.Case, history, true, new DateOnly(2026, 11, 2), Ids.Alice);
        var denied = CaseLifecycle.Rule(filed.Case, history, false, new DateOnly(2026, 11, 2), Ids.Kim);
        await Connect().Cases.RecordAsync(confirmed, Ct);

        var error = await Assert.ThrowsAsync<ConflictException>(() => Connect().Cases.RecordAsync(denied, Ct));
        Assert.Equal(CaseLifecycle.HistoryMoved, error.Message);
        Assert.True(LifecyclePeriods.IsConfirmed(await Connect().Cases.LifecycleAsync(created.Id, Ct)));
    }

    [Fact]
    public async Task A_plain_edit_between_two_events_does_not_disturb_the_history_s_version()
    {
        var created = await CreateCaseAsync();
        var ready = Move(created, CaseStatus.ReadyToFile, []);
        await Connect().Cases.RecordAsync(ready, Ct);
        var edited = ready.Case with { Judge = "Hon. Example" };
        await Connect().Cases.UpdateAsync(edited, CaseStatus.ReadyToFile, Ct);

        var back = Move(edited, CaseStatus.Intake, await Connect().Cases.LifecycleAsync(created.Id, Ct));
        Assert.NotNull(await Connect().Cases.RecordAsync(back, Ct));
        Assert.Equal(2, (await Connect().Cases.LifecycleAsync(created.Id, Ct)).Count);
    }

    [Fact]
    public async Task A_lifecycle_write_for_a_case_gone_or_in_another_firm_answers_none_and_writes_nothing()
    {
        var created = await CreateCaseAsync();
        var elsewhere = Move(created with { FirmId = Ids.OtherFirm }, CaseStatus.ReadyToFile, []);
        Assert.Null(await Connect().Cases.RecordAsync(elsewhere, Ct));
        var missing = Make.Case(NewId());
        Assert.Null(await Connect().Cases.RecordAsync(Move(missing, CaseStatus.ReadyToFile, []), Ct));
        Assert.Empty(await Connect().Cases.LifecycleAsync(created.Id, Ct));
        Assert.Equal(CaseStatus.Intake, (await Connect().Cases.ReadForWorkerAsync(created.Id, Ct))!.Status);
    }

    [Fact]
    public async Task Of_two_status_moves_from_one_read_exactly_one_lands()
    {
        var created = await CreateCaseAsync();
        var ready = Move(created, CaseStatus.ReadyToFile, []);
        var filed = File(created, []);

        var results = await RaceAsync<ConflictException>(
            () => Connect().Cases.RecordAsync(ready, Ct),
            () => Connect().Cases.RecordAsync(filed, Ct));

        Assert.Single(results, won => won);
        var history = await Connect().Cases.LifecycleAsync(created.Id, Ct);
        var winner = Assert.Single(history);
        Assert.Equal(winner.ToStatus, (await Connect().Cases.ReadForWorkerAsync(created.Id, Ct))!.Status);
    }

    private static LifecycleWrite Move(Case current, CaseStatus to, IReadOnlyList<LifecycleEvent> history) =>
        CaseLifecycle.StatusMove(current, current with { Status = to, UpdatedAt = current.UpdatedAt.AddMinutes(1) }, history, Ids.Alice)!;

    private static LifecycleWrite File(Case current, IReadOnlyList<LifecycleEvent> history) =>
        CaseLifecycle.StatusMove(
            current,
            current with { Status = CaseStatus.Filed, FiledAt = new DateOnly(2026, 10, 30), CaseNumber = "8:26-bk-01234", UpdatedAt = current.UpdatedAt.AddMinutes(2) },
            history,
            Ids.Alice)!;
}

/// <summary>The oracle: <see cref="Core.Memory.MemoryCaseStore"/> against its own suite.</summary>
public sealed class MemoryCaseStoreConformance : CaseStoreConformance
{
    private readonly MemoryCaseWorld _world = new();

    private protected override CaseStores Connect() => _world.Connect();
}

/// <summary><see cref="Cases.PostgresCaseStore"/>, held to the oracle's suite on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresCaseStoreConformance(PostgresFixture postgres) : CaseStoreConformance
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
}
