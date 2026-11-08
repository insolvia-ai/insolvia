using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Documents;
using Insolvia.Core.Memory;

namespace Insolvia.Core.Tests;

/// <summary>
/// The CASE half of document requests, translated from
/// test_document_requests.py (its checklist half is 18.6's
/// FirmConfigTests).
/// </summary>
public sealed class DocumentRequestTests
{
    private const string CaseId = "00000000-0000-4000-8000-0000000000ca";
    private const string OtherCaseId = "00000000-0000-4000-8000-0000000000cb";
    private const string Doc1 = "00000000-0000-4000-8000-00000000d0c1";
    private const string Doc2 = "00000000-0000-4000-8000-00000000d0c2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DocumentRequest Request(string title = "Bank statements", DocumentRequestStatus status = DocumentRequestStatus.Requested, string caseId = CaseId, string? description = null) =>
        DocumentRequestRules.Create(new ChecklistItem(title, DocumentKind.BankStatement, description), caseId, Ids.Alice) with { Status = status };

    [Fact]
    public void Applying_the_checklist_requests_each_entry_once()
    {
        var first = DocumentRequestRules.FromChecklist(DocumentChecklists.DefaultChecklist, [], CaseId, Ids.Alice);
        Assert.Equal(DocumentChecklists.DefaultChecklist.Select(item => item.Title), first.Select(request => request.Title));
        Assert.All(first, request => Assert.Equal(DocumentRequestStatus.Requested, request.Status));

        // Again — after the firm added one — adds only the new entry, and a
        // waived request still counts as asked.
        var waived = first[0] with { Status = DocumentRequestStatus.Waived };
        var again = DocumentRequestRules.FromChecklist(
            [.. DocumentChecklists.DefaultChecklist, new ChecklistItem("Lease", DocumentKind.Other)],
            [waived, .. first.Skip(1)],
            CaseId,
            Ids.Alice);
        Assert.Equal(["Lease"], again.Select(request => request.Title));
    }

    [Fact]
    public void A_request_is_parsed_like_a_checklist_entry()
    {
        Assert.Equal(new ChecklistItem("Lease", DocumentKind.Other), DocumentRequestRules.ParseCreation(Body.Of("""{"title": "Lease", "kind": "other"}""")));
        var error = Assert.Throws<FieldValidationException>(() => DocumentRequestRules.ParseCreation(Body.Of("""{"kind": "nope"}""")));
        Assert.Equal(["kind", "title"], error.Fields.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Received_cannot_be_set_by_hand()
    {
        Assert.Throws<FieldValidationException>(() => DocumentRequestRules.ParseUpdate(Body.Of("""{"status": "received"}""")));
        Assert.Throws<FieldValidationException>(() => DocumentRequestRules.ParseUpdate(Body.Of("""{"status": "done"}""")));
        Assert.Equal(DocumentRequestStatus.Waived, DocumentRequestRules.ParseUpdate(Body.Of("""{"status": "waived"}""")));
        var error = Assert.Throws<ArgumentException>(() => DocumentRequestRules.SetStatus(Request(), DocumentRequestStatus.Received));
        Assert.Contains("not settable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_completed_upload_receives_the_request_once()
    {
        var received = DocumentRequestRules.Satisfy(Request(), Doc1);
        Assert.Equal(DocumentRequestStatus.Received, received.Status);
        Assert.Equal([Doc1], received.DocumentIds);
        Assert.NotNull(received.ReceivedAt);

        Assert.Equal(received, DocumentRequestRules.Satisfy(received, Doc1));
        var second = DocumentRequestRules.Satisfy(received, Doc2);
        Assert.Equal([Doc1, Doc2], second.DocumentIds);
        Assert.Equal(received.ReceivedAt, second.ReceivedAt);
    }

    [Fact]
    public void An_upload_racing_a_waiver_does_not_overturn_it()
    {
        var after = DocumentRequestRules.Satisfy(Request(status: DocumentRequestStatus.Waived), Doc1);
        Assert.Equal(DocumentRequestStatus.Waived, after.Status);
        Assert.Equal([Doc1], after.DocumentIds);
    }

    [Fact]
    public void Reopening_a_received_request_keeps_its_documents()
    {
        var reopened = DocumentRequestRules.SetStatus(DocumentRequestRules.Satisfy(Request(), Doc1), DocumentRequestStatus.Requested);
        Assert.Equal(DocumentRequestStatus.Requested, reopened.Status);
        Assert.Equal([Doc1], reopened.DocumentIds);
    }

    [Fact]
    public void Only_a_waived_request_refuses_uploads()
    {
        Assert.True(DocumentRequestRules.AcceptsUploads(Request()));
        Assert.True(DocumentRequestRules.AcceptsUploads(Request(status: DocumentRequestStatus.Received)));
        Assert.False(DocumentRequestRules.AcceptsUploads(Request(status: DocumentRequestStatus.Waived)));
    }

    [Fact]
    public void Progress_counts_arrived_against_outstanding_and_leaves_waived_out() =>
        Assert.Equal(
            new DocumentRequestProgress(3, 1, 2, 1),
            DocumentRequestRules.Progress([Request("A", DocumentRequestStatus.Received), Request("B"), Request("C"), Request("D", DocumentRequestStatus.Waived)]));

    // test_the_staff_listing_carries_progress_from_the_same_list and
    // test_the_clients_view_names_no_case_and_no_staff_documents: the two JSON
    // views (the API's); test_the_request_item_round_trips_with_its_documents,
    // test_a_request_row_with_an_unknown_status_fails_loudly and the three
    // FakeDynamoDb tests: the item shape and the DynamoDB adapter; dropped.

    [Fact]
    public async Task The_memory_store_refuses_a_second_create_and_a_ghost_update()
    {
        var store = new MemoryDocumentRequestStore();
        var made = Request();
        await store.CreateAsync(made, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(made, Ct));
        Assert.Null(await store.UpdateAsync(Request("Other"), Ct));
    }

    [Fact]
    public async Task The_memory_store_scopes_by_case_and_lists_in_creation_order()
    {
        var store = new MemoryDocumentRequestStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var first = DocumentRequestRules.FromChecklist(DocumentChecklists.DefaultChecklist.Take(1), [], CaseId, Ids.Alice, clock)[0];
        clock.Now = clock.Now.AddTicks(10);
        var second = DocumentRequestRules.FromChecklist(DocumentChecklists.DefaultChecklist.Skip(1).Take(1), [], CaseId, Ids.Alice, clock)[0];
        await store.CreateAsync(second, Ct);
        await store.CreateAsync(first, Ct);
        await store.CreateAsync(Request(caseId: OtherCaseId), Ct);

        Assert.Equal([first.Id, second.Id], (await store.ListForCaseAsync(CaseId, Ct)).Select(r => r.Id));
        Assert.Null(await store.GetAsync(OtherCaseId, first.Id, Ct));
        Assert.True(await store.DeleteAsync(CaseId, first.Id, Ct));
        Assert.False(await store.DeleteAsync(CaseId, first.Id, Ct));
    }

    [Fact]
    public void The_request_statuses_keep_their_wire_strings() =>
        Assert.Equal(["requested", "received", "waived"], Wire.Names<DocumentRequestStatus>());
}
