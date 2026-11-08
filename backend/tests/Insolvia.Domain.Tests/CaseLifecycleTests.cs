using System.Text.Json.Nodes;
using Insolvia.Core.Access;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Clients;
using Insolvia.Core.Creditors;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;
using Insolvia.Core.Memory;
using Insolvia.Core.Provenance;
using static Insolvia.Core.Tests.CaseFixtures;

namespace Insolvia.Core.Tests;

/// <summary>The case lifecycle (#355), translated from test_case_lifecycle.py.</summary>
public sealed class CaseLifecycleTests
{
    private const string Filing = """{"status": "filed", "filed_at": "2026-09-01", "case_number": "8:26-bk-01234"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Accessor Admin => Accessor(isAdmin: true);

    public static TheoryData<CaseStatus, CaseStatus> OffTheMap()
    {
        var data = new TheoryData<CaseStatus, CaseStatus>();
        foreach (var start in Wire.Members<CaseStatus>())
        {
            foreach (var to in Wire.Members<CaseStatus>())
            {
                if (to != start && !CaseRules.Transitions[start].Contains(to))
                {
                    data.Add(start, to);
                }
            }
        }

        return data;
    }

    public static TheoryData<CaseStatus> Statuses() => [.. Wire.Members<CaseStatus>()];

    [Theory]
    [InlineData("prospect")]
    [InlineData("filed")]
    [InlineData("ready_to_file")]
    public void A_case_opens_retained_whatever_the_body_asks(string asked)
    {
        var payload = Opening();
        payload["status"] = asked;
        var (created, _) = CaseRules.Create(CaseRules.ParseCreation(payload), Ids.Firm, Ids.Alice);
        Assert.Equal(CaseStatus.Intake, created.Status);
    }

    [Fact]
    public void Prospect_is_not_a_case_status()
    {
        Assert.DoesNotContain("prospect", Wire.Names<CaseStatus>());
        Assert.Throws<FieldValidationException>(() => CaseRules.ParseUpdate(Body.Of("""{"status": "prospect"}""")));
    }

    [Fact]
    public void A_case_moves_from_intake_to_filed_and_closed()
    {
        var current = ACase();
        foreach (var payload in new[]
        {
            """{"status": "ready_to_file"}""",
            """{"status": "filed", "filed_at": "2026-09-01", "case_number": "8:26-bk-01234", "judge": "Hon. Example Judge", "trustee": "Example Trustee"}""",
            """{"meeting_341_at": "2026-10-05", "office_file_number": "F-1001"}""",
            """{"status": "discharged"}""",
            """{"status": "closed"}""",
        })
        {
            current = Moved(current, payload);
        }

        Assert.Equal(CaseStatus.Closed, current.Status);
        Assert.Equal(("8:26-bk-01234", "Hon. Example Judge", "Example Trustee", "F-1001"), (current.CaseNumber, current.Judge, current.Trustee, current.OfficeFileNumber));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 5)), (current.FiledAt!.Value, current.Meeting341At!.Value));
    }

    [Theory]
    [MemberData(nameof(OffTheMap))]
    public void A_move_off_the_map_is_refused(CaseStatus start, CaseStatus to)
    {
        var current = ACase() with { Status = start, FiledAt = new DateOnly(2026, 9, 1), CaseNumber = "8:26-bk-01234" };
        Assert.Throws<ConflictException>(() => Moved(current, $$"""{"status": "{{to.ToWire()}}"}"""));
    }

    [Fact]
    public void A_filed_case_never_returns_to_preparation()
    {
        var filed = Moved(ACase(), Filing);
        var error = Assert.Throws<ConflictException>(() => Moved(filed, """{"status": "ready_to_file"}"""));
        Assert.Contains("amendment", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_case_is_reopened_as_filed()
    {
        var closed = ACase() with { Status = CaseStatus.Closed, FiledAt = new DateOnly(2026, 9, 1), CaseNumber = "8:26-bk-1" };
        Assert.Equal(CaseStatus.Filed, Moved(closed, """{"status": "filed"}""").Status);
    }

    [Theory]
    [InlineData("""{"status": "filed"}""", new[] { "case_number", "filed_at" })]
    [InlineData("""{"status": "filed", "filed_at": "2026-09-01"}""", new[] { "case_number" })]
    [InlineData("""{"status": "filed", "case_number": "8:26-bk-1"}""", new[] { "filed_at" })]
    public void Filing_needs_the_date_and_the_case_number(string payload, string[] missing)
    {
        var error = Assert.Throws<FieldValidationException>(() => Moved(ACase() with { Status = CaseStatus.ReadyToFile }, payload));
        Assert.Equal(missing, error.Fields.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("filed_at")]
    [InlineData("case_number")]
    public void A_filed_case_keeps_its_anchors(string cleared)
    {
        var filed = Moved(ACase(), Filing);
        var error = Assert.Throws<FieldValidationException>(() => Moved(filed, $$"""{"{{cleared}}": null}"""));
        Assert.Equal([cleared], error.Fields.Keys);
    }

    [Fact]
    public void A_case_filed_before_the_rule_still_takes_other_edits()
    {
        var legacy = ACase() with { Status = CaseStatus.Filed };
        Assert.NotNull(Moved(legacy, """{"meeting_341_at": "2026-10-05"}""").Meeting341At);
    }

    [Theory]
    [InlineData("""{"case_number": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}""", "case_number")]
    [InlineData("""{"judge": 7}""", "judge")]
    [InlineData("""{"trustee": "two\nlines"}""", "trustee")]
    public void The_docket_facts_are_single_lines_of_bounded_text(string payload, string field)
    {
        var error = Assert.Throws<FieldValidationException>(() => CaseRules.ParseUpdate(Body.Of(payload)));
        Assert.Equal([field], error.Fields.Keys);
    }

    [Fact]
    public void A_blank_docket_fact_clears_it()
    {
        var withJudge = Moved(ACase(), """{"judge": "Hon. Example"}""");
        Assert.Null(Moved(withJudge, """{"judge": ""}""").Judge);
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public void Filed_means_on_the_docket_whatever_happened_since(CaseStatus status) =>
        Assert.Equal(status is CaseStatus.Filed or CaseStatus.Discharged or CaseStatus.Dismissed or CaseStatus.Closed, CaseRules.IsFiled(status));

    // ── The history ─────────────────────────────────────────────────

    [Fact]
    public void A_move_writes_a_history_row_and_an_edit_does_not()
    {
        var before = ACase();
        var ready = Moved(before, """{"status": "ready_to_file"}""");
        var write = CaseLifecycle.StatusMove(before, ready, [], Ids.Alice);
        Assert.NotNull(write);
        var change = write.Event;
        Assert.Equal((CaseStatus.Intake, CaseStatus.ReadyToFile), (change.FromStatus!.Value, change.ToStatus!.Value));
        Assert.Equal(ready.UpdatedAt, change.RecordedAt);
        Assert.Null(change.EffectiveOn);
        Assert.Null(CaseLifecycle.StatusMove(ready, Moved(ready, """{"judge": "X"}"""), [], Ids.Alice));
    }

    // test_the_history_row_round_trips, test_the_lifecycle_round_trips_the_item_and_reaches_the_wire,
    // test_a_row_written_before_the_lifecycle_reads_with_none_of_it and
    // test_the_deletion_stamp_never_reaches_the_wire asserted on the DynamoDB
    // item / the JSON view; dropped (18.10's mapping, the API's view).

    // ── Archive and delete ──────────────────────────────────────────

    [Fact]
    public void Archiving_twice_keeps_the_first_stamp_and_restoring_clears_it()
    {
        var first = CaseRules.SetArchived(ACase(), true, Ids.Alice);
        Assert.Equal(first, CaseRules.SetArchived(first, true, "someone-else"));
        var restored = CaseRules.SetArchived(first, false, Ids.Alice);
        Assert.Equal((null, null), (restored.ArchivedAt, restored.ArchivedBy));
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public void Only_a_case_that_never_reached_the_court_may_be_deleted(CaseStatus status) =>
        Assert.Equal(CaseRules.IsFiled(status), CaseRules.DeletionRefusal(ACase() with { Status = status }) is not null);

    [Fact]
    public void A_deleted_case_is_invisible_to_everyone()
    {
        var deleted = CaseRules.MarkDeleted(ACase(), Ids.Alice);
        Assert.False(CaseAccess.MaySeeCase(Admin, deleted, assigned: true));
        Assert.Null(CasePublicStatuses.From(deleted, Ids.Firm));
    }

    [Fact]
    public async Task The_working_list_and_the_archive_are_two_views_a_deleted_case_neither()
    {
        var store = new MemoryCaseStore();
        var cases = new List<Case>();
        for (var i = 0; i < 5; i++)
        {
            var (created, assignment) = NewCase();
            await store.CreateAsync(created, assignment, cancellationToken: Ct);
            cases.Add(created);
        }

        await store.UpdateAsync(CaseRules.SetArchived(cases[0], true, Ids.Alice), cancellationToken: Ct);
        await store.UpdateAsync(CaseRules.SetArchived(cases[1], true, Ids.Alice), cancellationToken: Ct);
        await store.UpdateAsync(CaseRules.MarkDeleted(cases[2], Ids.Alice), cancellationToken: Ct);

        var working = await store.ListForAccessorAsync(Admin, 10, null, cancellationToken: Ct);
        var archive = await store.ListForAccessorAsync(Admin, 10, null, archived: true, cancellationToken: Ct);
        Assert.Equal(new[] { cases[3].Id, cases[4].Id }.Order(StringComparer.Ordinal), working.Cases.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { cases[0].Id, cases[1].Id }.Order(StringComparer.Ordinal), archive.Cases.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.Null(await store.GetAsync(cases[2].Id, Admin, Ct));
    }

    [Fact]
    public async Task A_cursor_from_the_archive_is_refused_by_the_working_list()
    {
        var store = new MemoryCaseStore();
        for (var i = 0; i < 3; i++)
        {
            var (created, assignment) = NewCase();
            await store.CreateAsync(created, assignment, cancellationToken: Ct);
            await store.UpdateAsync(CaseRules.SetArchived(created, true, Ids.Alice), cancellationToken: Ct);
        }

        var page = await store.ListForAccessorAsync(Admin, 1, null, archived: true, cancellationToken: Ct);
        Assert.NotNull(page.NextCursor);
        await Assert.ThrowsAsync<ValidationException>(() => store.ListForAccessorAsync(Admin, 1, page.NextCursor, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_write_read_before_a_status_move_is_refused()
    {
        var store = new MemoryCaseStore();
        var (created, assignment) = NewCase();
        await store.CreateAsync(created, assignment, cancellationToken: Ct);
        await store.RecordAsync(CaseLifecycle.StatusMove(created, Moved(created, """{"status": "ready_to_file"}"""), [], Ids.Alice)!, Ct);
        await Assert.ThrowsAsync<ConflictException>(() => store.UpdateAsync(Moved(created, """{"judge": "J"}"""), CaseStatus.Intake, cancellationToken: Ct));
    }

    [Fact]
    public async Task The_memory_store_keeps_the_history_with_the_write()
    {
        var store = new MemoryCaseStore();
        var (created, assignment) = NewCase();
        await store.CreateAsync(created, assignment, cancellationToken: Ct);
        var after = Moved(created, """{"status": "ready_to_file"}""");
        await store.RecordAsync(CaseLifecycle.StatusMove(created, after, [], Ids.Alice)!, Ct);
        Assert.Equal([CaseStatus.ReadyToFile], (await store.LifecycleAsync(created.Id, Ct)).Select(change => change.ToStatus!.Value));
    }

    // ── Copy case ───────────────────────────────────────────────────

    [Fact]
    public void A_copy_is_a_new_retained_case_and_every_value_names_its_source()
    {
        var source = Moved(ACase() with { ExemptionSet = ExemptionSet.Federal }, """{"status": "filed", "filed_at": "2026-09-01", "case_number": "8:26-bk-01234", "judge": "J"}""");
        var staff = new ProvenanceEntry(ProvenanceSource.StaffTyped);
        var confirmed = new ProvenanceEntry(ProvenanceSource.AiExtracted) { ConfirmedBy = Ids.Alice, ConfirmedAt = "2026-09-01T00:00:00.000000Z", DocumentId = "doc-1" };
        var debtor = new Debtor("d1", source.Id, FilingRole.Debtor1, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), DebtorBody.Empty with { Email = "a@example.test" })
        {
            Provenance = new Dictionary<string, ProvenanceEntry> { ["email"] = staff }.ToValueDictionary(),
            ClientId = ClientA,
            CaseCreatedAt = source.CreatedAt,
        };
        var creditor = new CaseEntity<CreditorBody>(
            CreditorRules.Kind,
            "c1",
            source.Id,
            new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
            new CreditorBody("Example Bank", new Address()),
            new Dictionary<string, ProvenanceEntry> { ["name"] = confirmed }.ToValueDictionary(),
            Amended: true);

        var copy = CaseCopies.Copy(source, Ids.Alice, [debtor], [creditor]);

        Assert.NotEqual(source.Id, copy.Case.Id);
        Assert.Equal((CaseStatus.Intake, source.Chapter, source.Court), (copy.Case.Status, copy.Case.Chapter, copy.Case.Court));
        Assert.Equal(ExemptionSet.Federal, copy.Case.ExemptionSet);
        Assert.Equal((null, null, null), (copy.Case.FiledAt, copy.Case.CaseNumber, copy.Case.Judge));
        Assert.Equal(Ids.Alice, copy.Assignment.Subject);
        var copiedDebtor = Assert.Single(copy.Debtors);
        Assert.Equal("d1", copiedDebtor.Id);
        Assert.Equal(copy.Case.Id, copiedDebtor.CaseId);
        Assert.Equal(ClientA, copiedDebtor.ClientId);
        Assert.Equal(copy.Case.CreatedAt, copiedDebtor.CaseCreatedAt);
        Assert.Equal(staff with { CopiedFromCaseId = source.Id }, copiedDebtor.Provenance["email"]);
        var copied = Assert.IsType<CaseEntity<CreditorBody>>(Assert.Single(copy.Entities));
        Assert.Equal(("c1", copy.Case.Id, false), (copied.Id, copied.CaseId, copied.Amended));
        Assert.Equal(creditor.CreatedAt, copied.CreatedAt);
        Assert.Equal(confirmed with { CopiedFromCaseId = source.Id }, copied.Provenance["name"]);
    }

    [Fact]
    public void The_copy_marker_round_trips_the_provenance_wire()
    {
        var entry = new ProvenanceEntry(ProvenanceSource.StaffTyped) { CopiedFromCaseId = "case-1" };
        var parsed = ProvenanceRules.Parse(ProvenanceRules.ToJson(new Dictionary<string, ProvenanceEntry> { ["name"] = entry }));
        Assert.Equal(entry, parsed["name"]);
        Assert.Throws<FieldValidationException>(() => ProvenanceRules.Parse(Body.Of("""{"name": {"source": "staff_typed", "copied_from_case_id": 7}}""")));
    }

    // The three FakeDynamoDb tests (the listing reads on until the page is
    // full, the status move's transaction, the lost-race conflict) are the
    // DynamoDB adapter's; dropped — 18.10's Postgres store owns those
    // behaviours and the memory twin above is its oracle.

    // ── Beyond the Python suite: the filing worker's move ──────────

    [Fact]
    public void An_electronic_filing_files_the_case_with_its_history_row()
    {
        var ready = ACase() with { Status = CaseStatus.ReadyToFile };
        var write = CaseLifecycle.FileCase(ready, [], " 8:26-bk-01234 ", "2026-09-01", CaseRules.FilingWorkerActor, "filing-1");
        var (filed, change) = (write.Case, write.Event);
        Assert.Equal((CaseStatus.Filed, "8:26-bk-01234", new DateOnly(2026, 9, 1)), (filed.Status, filed.CaseNumber, filed.FiledAt!.Value));
        Assert.Equal(("filing-1", CaseRules.FilingWorkerActor), (change.FilingId, change.RecordedBy));
        Assert.Throws<ConflictException>(() => CaseLifecycle.FileCase(filed, [], "8:26-bk-1", "2026-09-01", CaseRules.FilingWorkerActor, "filing-2"));
        var error = Assert.Throws<FieldValidationException>(() => CaseLifecycle.FileCase(ready, [], " ", "September", CaseRules.FilingWorkerActor, "filing-3"));
        Assert.Equal(["case_number", "filed_at"], error.Fields.Keys.Order(StringComparer.Ordinal));
    }

    private static Case ACase() => NewCase().Case;

    private static Case Moved(Case current, string payload) =>
        CaseRules.ApplyChanges(current, CaseRules.ParseUpdate((JsonObject)JsonNode.Parse(payload)!));
}
