using System.Text.Json.Nodes;
using Insolvia.Core.Candidates;
using Insolvia.Core.CaseTasks;
using Insolvia.Core.Documents;
using Insolvia.Core.Memory;

namespace Insolvia.Core.Tests;

/// <summary>Case tasks, translated from test_tasks.py.</summary>
public sealed class CaseTaskTests
{
    private const string CaseId = "00000000-0000-4000-8000-0000000000ca";
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static JsonObject Payload(Action<JsonObject>? overrides = null)
    {
        var payload = Body.Of($$"""
            {"subject": "Get the vehicle payoff", "description": "Call the lender listed on the credit report.",
             "dueDate": "2026-10-01", "assigneeSubject": "{{Ids.Alice}}", "formSeries": "b106d"}
            """);
        overrides?.Invoke(payload);
        return payload;
    }

    private static CaseTask Make(Action<JsonObject>? overrides = null, TimeProvider? clock = null) =>
        CaseTaskRules.Create(CaseTaskRules.ParseCreation(Payload(overrides)), CaseId, Ids.Alice, clock);

    private static CaseTask Apply(CaseTask task, string json, TimeProvider? clock = null) =>
        CaseTaskRules.ApplyChanges(task, CaseTaskRules.ParseUpdate(Body.Of(json)), clock);

    [Fact]
    public void Only_subject_is_required()
    {
        var draft = CaseTaskRules.ParseCreation(Body.Of("""{"subject": "Follow up with client"}"""));
        Assert.Equal("Follow up with client", draft.Subject);
        Assert.Null(draft.Description);
        Assert.Null(draft.DueDate);
        Assert.Null(draft.AssigneeSubject);
        Assert.Null(draft.FormSeries);
    }

    [Theory]
    [InlineData("subject", "null")]
    [InlineData("subject", "\"   \"")]
    [InlineData("subject", "LONG")]
    [InlineData("subject", "\"two\\nlines\"")]
    [InlineData("dueDate", "\"not-a-date\"")]
    [InlineData("dueDate", "\"2026-02-30\"")]
    [InlineData("assigneeSubject", "\"not-a-subject\"")]
    [InlineData("assigneeSubject", "7")]
    [InlineData("formSeries", "\"UPPER CASE!\"")]
    [InlineData("formSeries", "\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\"")]
    public void Creation_refuses_bad_fields(string field, string value)
    {
        // LONG is one character past the cap — Python's "x" * (MAX_SUBJECT + 1).
        var node = value == "LONG" ? JsonValue.Create(new string('x', CaseTaskRules.MaxSubject + 1)) : Body.Value(value);
        var error = Assert.Throws<FieldValidationException>(() => CaseTaskRules.ParseCreation(Payload(p => p[field] = node)));
        Assert.Contains(field, error.Fields.Keys);
    }

    [Fact]
    public void The_subject_cap_is_exactly_the_maximum() =>
        Assert.Equal(CaseTaskRules.MaxSubject, CaseTaskRules.ParseCreation(new JsonObject { ["subject"] = new string('x', CaseTaskRules.MaxSubject) }).Subject.Length);

    [Fact]
    public void A_blank_optional_field_collapses_to_none_rather_than_erroring()
    {
        var draft = CaseTaskRules.ParseCreation(Payload(p =>
        {
            p["description"] = "   ";
            p["formSeries"] = null;
        }));
        Assert.Null(draft.Description);
        Assert.Null(draft.FormSeries);
    }

    [Fact]
    public void Creation_never_accepts_done()
    {
        var task = Make(p => p["done"] = true);
        Assert.False(task.Done);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public void Create_task_stamps_identity_and_audit_fact()
    {
        var task = Make();
        Assert.Equal((CaseId, Ids.Alice), (task.CaseId, task.CreatedBy));
        Assert.NotEmpty(task.Id);
        Assert.Equal(task.CreatedAt, task.UpdatedAt);
        Assert.False(task.Done);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public void Update_requires_at_least_one_field() =>
        Assert.Throws<ValidationException>(() => CaseTaskRules.ParseUpdate([]));

    [Fact]
    public void Update_leaves_absent_fields_unchanged()
    {
        var task = Make();
        var updated = Apply(task, """{"subject": "Renamed"}""");
        Assert.Equal("Renamed", updated.Subject);
        Assert.Equal((task.Description, task.DueDate, task.AssigneeSubject, task.FormSeries), (updated.Description, updated.DueDate, updated.AssigneeSubject, updated.FormSeries));
    }

    [Theory]
    [InlineData("description")]
    [InlineData("dueDate")]
    [InlineData("assigneeSubject")]
    [InlineData("formSeries")]
    public void Update_clears_a_nullable_field_on_explicit_null(string field)
    {
        var updated = Apply(Make(), $$"""{"{{field}}": null}""");
        object? cleared = field switch
        {
            "description" => updated.Description,
            "dueDate" => updated.DueDate,
            "assigneeSubject" => updated.AssigneeSubject,
            _ => updated.FormSeries,
        };
        Assert.Null(cleared);
    }

    [Fact]
    public void Subject_cannot_be_cleared() =>
        Assert.Contains("subject", Assert.Throws<FieldValidationException>(() => CaseTaskRules.ParseUpdate(Body.Of("""{"subject": null}"""))).Fields.Keys);

    [Fact]
    public void Reassign_changes_only_the_assignee()
    {
        var task = Make();
        var updated = Apply(task, $$"""{"assigneeSubject": "{{Ids.Bob}}"}""");
        Assert.Equal(Ids.Bob, updated.AssigneeSubject);
        Assert.Equal(task.Subject, updated.Subject);
    }

    [Fact]
    public void Completing_a_task_stamps_completed_at()
    {
        var updated = Apply(Make(), """{"done": true}""");
        Assert.True(updated.Done);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public void Reopening_a_task_clears_completed_at()
    {
        var reopened = Apply(Apply(Make(), """{"done": true}"""), """{"done": false}""");
        Assert.False(reopened.Done);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public void Resending_the_same_done_value_does_not_touch_completed_at()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var done = Apply(Make(clock: clock), """{"done": true}""", clock);
        clock.Now = clock.Now.AddHours(1);
        Assert.Equal(done.CompletedAt, Apply(done, """{"done": true}""", clock).CompletedAt);
    }

    [Fact]
    public void Editing_other_fields_does_not_disturb_completion()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var done = Apply(Make(clock: clock), """{"done": true}""", clock);
        clock.Now = clock.Now.AddHours(1);
        var edited = Apply(done, """{"subject": "Renamed"}""", clock);
        Assert.True(edited.Done);
        Assert.Equal(done.CompletedAt, edited.CompletedAt);
    }

    [Fact]
    public void Done_must_be_a_boolean() =>
        Assert.Contains("done", Assert.Throws<FieldValidationException>(() => CaseTaskRules.ParseUpdate(Body.Of("""{"done": "yes"}"""))).Fields.Keys);

    [Fact]
    public void A_task_past_its_due_date_is_overdue() =>
        Assert.True(CaseTaskRules.IsOverdue(Make(p => p["dueDate"] = "2026-01-01"), Today));

    [Fact]
    public void A_task_due_today_or_later_is_not_overdue()
    {
        Assert.False(CaseTaskRules.IsOverdue(Make(p => p["dueDate"] = "2026-09-24"), Today));
        Assert.False(CaseTaskRules.IsOverdue(Make(p => p["dueDate"] = "2026-12-01"), Today));
    }

    [Fact]
    public void A_task_with_no_due_date_is_never_overdue() =>
        Assert.False(CaseTaskRules.IsOverdue(Make(p => p["dueDate"] = null), Today));

    [Fact]
    public void A_done_task_is_never_overdue_even_when_late() =>
        Assert.False(CaseTaskRules.IsOverdue(Apply(Make(p => p["dueDate"] = "2026-01-01"), """{"done": true}"""), Today));

    // test_task_json_carries_the_computed_overdue_flag (the JSON view; IsOverdue
    // is the computation it read), test_item_round_trip_preserves_every_field,
    // test_item_omits_absent_optional_fields and test_sort_key_is_namespaced_by_task
    // (the item shape and key): dropped.

    [Fact]
    public void List_order_is_creation_time_then_id()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var older = Make(clock: clock);
        clock.Now = clock.Now.AddSeconds(1);
        var newer = Make(clock: clock);
        Assert.Equal([older, newer], new[] { newer, older }.Order(CaseTaskRules.ListOrder));
    }
}

/// <summary>The memory case-child stores, translated from test_task_store.py, test_document_store.py and test_candidate_store.py.</summary>
public sealed class CaseChildStoreTests
{
    private const string CaseId = "00000000-0000-4000-8000-0000000000ca";
    private const string OtherCaseId = "00000000-0000-4000-8000-0000000000cb";
    private const string TaskId = "00000000-0000-4000-8000-0000000000ta";
    private const string DocumentId = "00000000-0000-4000-8000-0000000000d0";

    private static readonly CandidateOrigin Origin = new(OriginChannel.Mcp, "client-1", "subject-1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Tasks ───────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_the_same_task_twice_is_refused()
    {
        var store = new MemoryCaseTaskStore();
        await store.CreateAsync(Task(), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Task(subject: "A different task"), Ct));
    }

    [Fact]
    public async Task Creating_the_same_task_instance_twice_is_refused()
    {
        var store = new MemoryCaseTaskStore();
        var only = Task();
        await store.CreateAsync(only, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(only, Ct));
        Assert.Same(only, await store.GetAsync(CaseId, TaskId, Ct));
    }

    [Fact]
    public async Task Task_update_refuses_a_row_that_is_not_there()
    {
        var store = new MemoryCaseTaskStore();
        Assert.Null(await store.UpdateAsync(Task(), Ct));
        Assert.Empty(await store.ListForCaseAsync(CaseId, Ct));
    }

    [Fact]
    public async Task Task_update_replaces_an_existing_row()
    {
        var store = new MemoryCaseTaskStore();
        await store.CreateAsync(Task(), Ct);
        var completed = Task() with { Done = true, CompletedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        Assert.Equal(completed, await store.UpdateAsync(completed, Ct));
        Assert.Equal(completed, await store.GetAsync(CaseId, TaskId, Ct));
    }

    [Fact]
    public async Task Task_delete_reports_whether_it_removed_anything()
    {
        var store = new MemoryCaseTaskStore();
        await store.CreateAsync(Task(), Ct);
        Assert.True(await store.DeleteAsync(CaseId, TaskId, Ct));
        Assert.False(await store.DeleteAsync(CaseId, TaskId, Ct));
    }

    [Fact]
    public async Task The_same_task_id_in_another_case_is_a_different_row()
    {
        var store = new MemoryCaseTaskStore();
        await store.CreateAsync(Task(), Ct);
        await store.CreateAsync(Task(caseId: OtherCaseId), Ct);
        Assert.NotNull(await store.GetAsync(CaseId, TaskId, Ct));
        Assert.NotNull(await store.GetAsync(OtherCaseId, TaskId, Ct));
        Assert.Single(await store.ListForCaseAsync(CaseId, Ct));
    }

    [Fact]
    public async Task Task_list_for_case_is_creation_order()
    {
        var store = new MemoryCaseTaskStore();
        var first = Task(taskId: TaskId[..^1] + "1") with { CreatedAt = DateTimeOffset.UnixEpoch };
        var second = Task(taskId: TaskId[..^1] + "2") with { CreatedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        await store.CreateAsync(second, Ct);
        await store.CreateAsync(first, Ct);
        Assert.Equal([first.Id, second.Id], (await store.ListForCaseAsync(CaseId, Ct)).Select(t => t.Id));
    }

    // ── Documents ───────────────────────────────────────────────────

    [Fact]
    public async Task Creating_the_same_document_twice_is_refused()
    {
        var store = new MemoryDocumentStore();
        await store.CreateAsync(Document(), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Document(fileName: "b.pdf"), Ct));
    }

    [Fact]
    public async Task Creating_the_same_document_instance_twice_is_refused()
    {
        var store = new MemoryDocumentStore();
        var only = Document();
        await store.CreateAsync(only, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(only, Ct));
        Assert.Same(only, await store.GetAsync(CaseId, DocumentId, Ct));
    }

    [Fact]
    public async Task Document_update_refuses_a_row_that_is_not_there()
    {
        var store = new MemoryDocumentStore();
        Assert.Null(await store.UpdateAsync(Document(), Ct));
        Assert.Empty(await store.ListForCaseAsync(CaseId, Ct));
    }

    [Fact]
    public async Task Document_update_replaces_an_existing_row()
    {
        var store = new MemoryDocumentStore();
        await store.CreateAsync(Document(), Ct);
        var confirmed = Document() with { Status = DocumentStatus.Stored, ByteSize = 99, Etag = "deadbeef" };
        Assert.Equal(confirmed, await store.UpdateAsync(confirmed, Ct));
        Assert.Equal(confirmed, await store.GetAsync(CaseId, DocumentId, Ct));
    }

    [Fact]
    public async Task The_same_document_id_in_another_case_is_a_different_row()
    {
        var store = new MemoryDocumentStore();
        await store.CreateAsync(Document(), Ct);
        await store.CreateAsync(Document(caseId: OtherCaseId), Ct);
        Assert.NotNull(await store.GetAsync(CaseId, DocumentId, Ct));
        Assert.NotNull(await store.GetAsync(OtherCaseId, DocumentId, Ct));
        Assert.Single(await store.ListForCaseAsync(CaseId, Ct));
    }

    // ── Candidates ──────────────────────────────────────────────────

    [Fact]
    public async Task Created_candidates_are_read_back()
    {
        var store = new MemoryCandidateStore();
        var candidate = Candidate();
        await store.CreateAsync(candidate, Ct);
        Assert.Equal(candidate, await store.GetAsync("case-1", candidate.Id, Ct));
    }

    [Fact]
    public async Task Create_refuses_to_overwrite()
    {
        var store = new MemoryCandidateStore();
        var candidate = Candidate();
        await store.CreateAsync(candidate, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(candidate, Ct));
    }

    [Fact]
    public async Task A_candidate_does_not_resolve_under_another_case()
    {
        var store = new MemoryCandidateStore();
        var candidate = Candidate();
        await store.CreateAsync(candidate, Ct);
        Assert.Null(await store.GetAsync("case-2", candidate.Id, Ct));
    }

    [Fact]
    public async Task Listing_is_case_scoped_and_creation_ordered()
    {
        var store = new MemoryCandidateStore();
        var first = Candidate();
        var second = Candidate() with { CreatedAt = first.CreatedAt.AddTicks(10) };
        var otherCase = Candidate(caseId: "case-2");
        await store.CreateAsync(second, Ct);
        await store.CreateAsync(first, Ct);
        await store.CreateAsync(otherCase, Ct);
        Assert.Equal([first, second], await store.ListForCaseAsync("case-1", Ct));
    }

    [Fact]
    public async Task Update_is_a_compare_and_swap_on_status()
    {
        var store = new MemoryCandidateStore();
        var candidate = Candidate();
        await store.CreateAsync(candidate, Ct);
        var withdrawn = candidate with { Status = CandidateStatus.Withdrawn };
        Assert.Equal(withdrawn, await store.UpdateAsync(withdrawn, CandidateStatus.Pending, Ct));
        Assert.Null(await store.UpdateAsync(withdrawn, CandidateStatus.Pending, Ct));
    }

    [Fact]
    public async Task Update_of_a_missing_row_fails() =>
        Assert.Null(await new MemoryCandidateStore().UpdateAsync(Candidate(), CandidateStatus.Pending, Ct));

    private static CaseTask Task(string taskId = TaskId, string caseId = CaseId, string subject = "A task") =>
        new(taskId, caseId, subject, null, null, null, null, false, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Ids.Alice);

    private static Document Document(string documentId = DocumentId, string caseId = CaseId, string fileName = "a.pdf") =>
        new(documentId, caseId, DocumentKind.PayStub, fileName, DocumentContentType.Pdf, 1024, $"cases/{caseId}/{documentId}", Ids.Alice, DateTimeOffset.UnixEpoch);

    private static Candidate Candidate(string caseId = "case-1")
    {
        var draft = Assert.Single(CandidateRules.ParseProposals(Body.Value("""[{"entityType": "creditors", "payload": {"name": "First Example Bank"}}]""")));
        return CandidateRules.Create(draft, caseId, Origin);
    }
}
