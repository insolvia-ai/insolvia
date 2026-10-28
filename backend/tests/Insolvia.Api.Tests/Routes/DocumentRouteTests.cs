using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Documents;
using Insolvia.Core.Firms;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_document_routes.py, and the staff half of test_portal_documents.py
/// (the case's document requests): tenancy, the validated upload and its
/// presigned capability, completion against the bucket, the list, download,
/// delete, the access log's four verbs, and who may. The extraction trigger's
/// four tests wait for the job store (18.17).
/// </summary>
public sealed class DocumentRouteTests : IAsyncLifetime, IDisposable
{
    private const string Upload = """{"kind": "bank_statement", "fileName": "statement.pdf", "contentType": "application/pdf", "byteSize": 2048}""";

    private readonly CaseWorld _world = new();

    private MemoryDocumentBlobStore Blobs => (MemoryDocumentBlobStore)_world.Service<IDocumentBlobStore>();

    private IDocumentStore Documents => _world.Service<IDocumentStore>();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── Tenancy ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("POST", "/v1/cases/any-id/documents")]
    [InlineData("GET", "/v1/cases/any-id/documents")]
    [InlineData("GET", "/v1/cases/any-id/documents/any-doc/url")]
    [InlineData("DELETE", "/v1/cases/any-id/documents/any-doc")]
    [InlineData("POST", "/v1/cases/any-id/documents/any-doc/complete")]
    public async Task Every_route_refuses_an_unauthenticated_caller_and_records_nothing(string method, string path)
    {
        Assert.Equal(401, (await _world.World.SendAsync(new HttpMethod(method), path, json: Upload)).Status);
        Assert.Empty(_world.Events);
    }

    [Fact]
    public async Task Another_firm_uploads_nothing_lists_nothing_downloads_nothing_and_deletes_nothing()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(404, (await Add(caseId, subject: Carol)).Status);
        Assert.Empty(await Documents.ListForCaseAsync(caseId, HttpAssert.Ct));
        Assert.Empty(Blobs.Minted);

        var documentId = await AddedAsync(caseId);
        var minted = Blobs.Minted.Count;
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/documents", Carol)).Status);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/documents/{documentId}/url", Carol)).Status);
        Assert.Equal(404, (await _world.Delete($"/v1/cases/{caseId}/documents/{documentId}", Carol)).Status);
        Assert.Equal(minted, Blobs.Minted.Count);
        Assert.NotNull(await Documents.GetAsync(caseId, documentId, HttpAssert.Ct));
    }

    [Fact]
    public async Task A_missing_case_a_foreign_one_and_a_document_of_another_case_are_all_404()
    {
        var first = await _world.OpenCaseIdAsync();
        var second = await _world.OpenCaseIdAsync();
        var documentId = await AddedAsync(first);

        var missing = await _world.Get("/v1/cases/no-such-case/documents");
        var foreign = await _world.Get($"/v1/cases/{first}/documents", Carol);
        Assert.Equal(missing.Body, foreign.Body);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{second}/documents/{documentId}/url")).Status);
        Assert.Equal(404, (await _world.Delete($"/v1/cases/{second}/documents/{documentId}")).Status);
        Assert.Equal("""{"error":"NotFoundError","message":"document not found"}""", (await _world.Get($"/v1/cases/{first}/documents/no-such-document/url")).Body);
    }

    // ── The upload ──────────────────────────────────────────────────

    [Theory]
    [InlineData(""" "byteSize": 52428801 """, "byteSize")]
    [InlineData(""" "byteSize": 0 """, "byteSize")]
    [InlineData(""" "contentType": "application/zip" """, "contentType")]
    [InlineData(""" "contentType": "image/svg+xml" """, "contentType")]
    [InlineData(""" "kind": "holiday_snaps" """, "kind")]
    [InlineData(""" "fileName": "../escape.pdf" """, "fileName")]
    public async Task A_bad_upload_is_refused_and_mints_nothing(string overrides, string field)
    {
        var caseId = await _world.OpenCaseIdAsync();

        var answer = await Add(caseId, overrides);

        Assert.Equal(400, answer.Status);
        Assert.Contains(field, answer.FieldErrors);
        Assert.Empty(Blobs.Minted);
        Assert.Empty(await Documents.ListForCaseAsync(caseId, HttpAssert.Ct));
    }

    [Fact]
    public async Task A_non_object_or_malformed_body_is_refused_before_the_case_is_touched()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(400, (await _world.Post($"/v1/cases/{caseId}/documents", """["nope"]""")).Status);

        var before = _world.Events.Count;
        Assert.Equal(400, (await Add("no-such-case", """ "byteSize": -1 """)).Status);
        Assert.Equal(before, _world.Events.Count);
    }

    [Fact]
    public async Task An_upload_returns_the_record_and_the_put_capability_bound_to_the_validated_terms()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var answer = await Add(caseId, """ "byteSize": 4096 """);

        Assert.Equal(201, answer.Status);
        var document = answer.Object["document"]!.AsObject();
        Assert.Equal(("statement.pdf", "bank_statement", "pending", "staff"), (document.Str("fileName"), document.Str("kind"), document.Str("status"), document.Str("channel")));
        Assert.Null(document["requestId"]);
        Assert.Equal(["byteSize", "caseId", "channel", "contentType", "fileName", "id", "kind", "requestId", "status", "uploadedAt"], Answer.Keys(document));
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$", document.Str("uploadedAt")!);
        var upload = answer.Object["upload"]!.AsObject();
        Assert.Equal("PUT", upload.Str("method"));
        Assert.EndsWith("Z", upload.Str("expiresAt"), StringComparison.Ordinal);
        Assert.Equal("""{"Content-Type":"application/pdf","x-amz-tagging":"upload=unconfirmed"}""", Http.FlaskJson.Serialize(upload["headers"]));

        var minted = Blobs.Minted[^1];
        Assert.Equal(("PUT", 4096L, "application/pdf", DocumentRules.UploadTag, 900), (minted.Method, minted.ByteSize!.Value, minted.ContentType, minted.Tag, minted.ExpiresIn));
        Assert.Equal($"cases/{caseId}/{document.Str("id")}", minted.StorageRef);
        Assert.DoesNotContain("statement", upload.Str("url"), StringComparison.Ordinal);
    }

    // ── Completion ──────────────────────────────────────────────────

    [Fact]
    public async Task Completing_an_upload_that_never_landed_is_refused()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await AddedAsync(caseId);

        var answer = await Complete(caseId, documentId);

        Assert.Equal(409, answer.Status);
        Assert.Equal("ConflictError", answer.Str("error"));
        Assert.Empty(Blobs.Cleared);
        Assert.Equal(DocumentStatus.Pending, (await Documents.GetAsync(caseId, documentId, HttpAssert.Ct))!.Status);
    }

    [Fact]
    public async Task Completing_clears_the_reapers_tag_and_reports_the_size_s3_counted()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await UploadedAsync(caseId, byteSize: 1999);
        var storageRef = $"cases/{caseId}/{documentId}";
        Assert.Contains(storageRef, Blobs.Tagged);

        var answer = await Complete(caseId, documentId);

        Assert.Equal(200, answer.Status);
        Assert.Equal([storageRef], Blobs.Cleared);
        Assert.DoesNotContain(storageRef, Blobs.Tagged);
        var document = answer.Object["document"]!.AsObject();
        Assert.Equal((1999, "stored"), (document["byteSize"]!.GetValue<long>(), document.Str("status")));
    }

    [Fact]
    public async Task Completing_is_idempotent_another_firm_cannot_and_a_deleted_document_is_404()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await UploadedAsync(caseId);

        Assert.Equal(404, (await Complete(caseId, documentId, Carol)).Status);
        Assert.Empty(Blobs.Cleared);
        var first = await Complete(caseId, documentId);
        var second = await Complete(caseId, documentId);
        Assert.Equal((200, 200), (first.Status, second.Status));
        Assert.Equal(first.Body, second.Body);

        await _world.Delete($"/v1/cases/{caseId}/documents/{documentId}");
        Assert.Equal(404, (await Complete(caseId, documentId)).Status);
        Assert.Empty(await Documents.ListForCaseAsync(caseId, HttpAssert.Ct));
    }

    // ── The list, download and delete ───────────────────────────────

    [Fact]
    public async Task Listing_is_newest_first_this_cases_only_shows_status_and_mints_nothing()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var other = await _world.OpenCaseIdAsync();
        var finished = await UploadedAsync(caseId);
        await Task.Delay(5, HttpAssert.Ct);
        var abandoned = await AddedAsync(caseId);
        await Complete(caseId, finished);
        var minted = Blobs.Minted.Count;

        var listed = (await _world.Get($"/v1/cases/{caseId}/documents")).Object["documents"]!.AsArray();

        Assert.Equal([(abandoned, "pending"), (finished, "stored")], listed.Select(d => (d!.AsObject().Str("id"), d.AsObject().Str("status"))));
        Assert.Equal("""{"documents":[]}""", (await _world.Get($"/v1/cases/{other}/documents")).Body);
        Assert.Equal(minted, Blobs.Minted.Count);
    }

    [Fact]
    public async Task A_download_url_is_a_short_get_capability()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await AddedAsync(caseId);

        var answer = await _world.Get($"/v1/cases/{caseId}/documents/{documentId}/url");

        Assert.Equal(("GET", true), (answer.Str("method"), answer.Str("expiresAt")!.EndsWith('Z')));
        Assert.Equal(["expiresAt", "method", "url"], Answer.Keys(answer.Json));
        Assert.Equal(("GET", 300), (Blobs.Minted[^1].Method, Blobs.Minted[^1].ExpiresIn));
    }

    [Fact]
    public async Task Delete_removes_the_row_and_the_object_once()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await AddedAsync(caseId);
        var path = $"/v1/cases/{caseId}/documents/{documentId}";

        var first = await _world.Delete(path);

        Assert.Equal((204, ""), (first.Status, first.Body));
        Assert.Equal(404, (await _world.Delete(path)).Status);
        Assert.Equal([$"cases/{caseId}/{documentId}"], Blobs.Deleted);
        Assert.Empty(await Documents.ListForCaseAsync(caseId, HttpAssert.Ct));
        Assert.Equal(404, (await _world.Get($"{path}/url")).Status);
    }

    // ── The access log ──────────────────────────────────────────────

    [Theory]
    [InlineData("create")]
    [InlineData("read")]
    [InlineData("download")]
    [InlineData("delete")]
    public async Task Every_route_is_recorded_under_its_own_action(string verb)
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await AddedAsync(caseId);

        var (action, call) = verb switch
        {
            "create" => (AccessAction.DocumentCreate, Add(caseId)),
            "read" => (AccessAction.DocumentRead, _world.Get($"/v1/cases/{caseId}/documents")),
            "download" => (AccessAction.DocumentDownload, _world.Get($"/v1/cases/{caseId}/documents/{documentId}/url")),
            _ => (AccessAction.DocumentDelete, _world.Delete($"/v1/cases/{caseId}/documents/{documentId}")),
        };
        await call;

        Assert.Equal((action, AccessOutcome.Allowed, caseId), (_world.Events[^1].Action, _world.Events[^1].Outcome, _world.Events[^1].CaseId));
    }

    [Fact]
    public async Task A_refused_download_is_recorded_as_denied()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await AddedAsync(caseId);

        await _world.Get($"/v1/cases/{caseId}/documents/{documentId}/url", Carol);

        Assert.Equal((AccessAction.DocumentDownload, AccessOutcome.Denied, Carol), (_world.Events[^1].Action, _world.Events[^1].Outcome, _world.Events[^1].Principal));
    }

    // ── Who may ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_colleague_on_the_matter_can_open_its_documents()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await AddedAsync(caseId);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/documents", Bob)).Status);

        Assert.Equal(204, (await _world.Put($"/v1/cases/{caseId}/assignees/{Bob}", null)).Status);

        var listed = await _world.Get($"/v1/cases/{caseId}/documents", Bob);
        Assert.Equal([documentId], listed.Object["documents"]!.AsArray().Select(d => d!.AsObject().Str("id")));
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}/documents/{documentId}/url", Bob)).Status);
    }

    [Fact]
    public async Task Documents_hidden_is_403_and_view_only_reads_but_does_not_upload_or_delete()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await AddedAsync(caseId);

        await RestrictAliceAsync(PermissionLevel.Hidden);
        Assert.Equal(403, (await _world.Get($"/v1/cases/{caseId}/documents")).Status);

        await RestrictAliceAsync(PermissionLevel.ViewOnly);
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}/documents")).Status);
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}/documents/{documentId}/url")).Status);
        Assert.Equal(403, (await Add(caseId)).Status);
        Assert.Equal(403, (await _world.Delete($"/v1/cases/{caseId}/documents/{documentId}")).Status);
    }

    // ── Document requests: the case half (test_portal_documents.py) ─

    [Fact]
    public async Task Applying_the_checklist_adds_the_default_once_with_progress()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var first = await ApplyChecklistAsync(caseId);
        var again = await ApplyChecklistAsync(caseId);

        Assert.Equal(DocumentChecklists.DefaultChecklist.Count, first["added"]!.GetValue<int>());
        Assert.Equal(0, again["added"]!.GetValue<int>());
        Assert.Equal(DocumentChecklists.DefaultChecklist.Count, again["requests"]!.AsArray().Count);
        Assert.Equal(["added", "progress", "requests"], Answer.Keys(again));
        var progress = again["progress"]!.AsObject();
        Assert.Equal((DocumentChecklists.DefaultChecklist.Count, 0, DocumentChecklists.DefaultChecklist.Count, 0), (progress["total"]!.GetValue<int>(), progress["received"]!.GetValue<int>(), progress["outstanding"]!.GetValue<int>(), progress["waived"]!.GetValue<int>()));
    }

    [Fact]
    public async Task The_case_applies_its_firms_own_checklist()
    {
        Assert.Equal(200, (await _world.Put("/v1/firm/document-checklist", """{"items": [{"title": "Lease", "kind": "other"}]}""")).Status);
        var caseId = await _world.OpenCaseIdAsync();

        var applied = await ApplyChecklistAsync(caseId);

        Assert.Equal(["Lease"], applied["requests"]!.AsArray().Select(r => r!.AsObject().Str("title")));
    }

    [Fact]
    public async Task One_request_can_be_added_and_withdrawn()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var made = await _world.Post($"/v1/cases/{caseId}/document-requests", """{"title": "Divorce decree", "kind": "court_notice"}""");

        Assert.Equal(201, made.Status);
        Assert.Equal(["caseId", "createdAt", "description", "documentIds", "id", "kind", "receivedAt", "status", "title", "updatedAt"], Answer.Keys(made.Json));
        Assert.Equal(204, (await _world.Delete($"/v1/cases/{caseId}/document-requests/{made.Str("id")}")).Status);
        Assert.Equal(404, (await _world.Delete($"/v1/cases/{caseId}/document-requests/{made.Str("id")}")).Status);
        Assert.Empty((await _world.Get($"/v1/cases/{caseId}/document-requests")).Object["requests"]!.AsArray());
    }

    [Fact]
    public async Task Received_cannot_be_set_by_hand_and_waived_can()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var requestId = (await ApplyChecklistAsync(caseId))["requests"]![0]!.AsObject().Str("id");

        Assert.Equal(400, (await _world.Patch($"/v1/cases/{caseId}/document-requests/{requestId}", """{"status": "received"}""")).Status);
        var waived = await _world.Patch($"/v1/cases/{caseId}/document-requests/{requestId}", """{"status": "waived"}""");

        Assert.Equal("waived", waived.Str("status"));
        Assert.Equal(0, (await _world.Get($"/v1/cases/{caseId}/document-requests")).Object["progress"]!["received"]!.GetValue<int>());
    }

    [Fact]
    public async Task Another_firms_case_requests_are_a_404()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/document-requests", Carol)).Status);
        Assert.Equal(404, (await _world.Post($"/v1/cases/{caseId}/document-requests/from-checklist", null, Carol)).Status);
    }

    [Fact]
    public async Task A_staff_upload_naming_a_waived_or_unknown_request_is_refused_and_a_live_one_is_received_on_completion()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var requests = (await ApplyChecklistAsync(caseId))["requests"]!.AsArray();
        var waivedId = requests[0]!.AsObject().Str("id");
        var liveId = requests[1]!.AsObject().Str("id");
        await _world.Patch($"/v1/cases/{caseId}/document-requests/{waivedId}", """{"status": "waived"}""");

        Assert.Equal(["requestId"], (await Add(caseId, $$""" "kind": "other", "requestId": "{{waivedId}}" """)).FieldErrors);
        Assert.Equal(["requestId"], (await Add(caseId, """ "requestId": "no-such-request" """)).FieldErrors);

        var documentId = (await Add(caseId, $$""" "requestId": "{{liveId}}" """)).Object["document"]!.AsObject().Str("id")!;
        Blobs.AcceptUpload($"cases/{caseId}/{documentId}", 2048);
        Assert.Equal(200, (await Complete(caseId, documentId)).Status);

        var live = (await _world.Get($"/v1/cases/{caseId}/document-requests")).Object["requests"]!.AsArray().Single(r => r!.AsObject().Str("id") == liveId)!.AsObject();
        Assert.Equal(("received", $"[\"{documentId}\"]"), (live.Str("status"), live["documentIds"]!.ToJsonString()));
    }

    private async Task<System.Text.Json.Nodes.JsonObject> ApplyChecklistAsync(string caseId)
    {
        var answer = await _world.Post($"/v1/cases/{caseId}/document-requests/from-checklist", null);
        Assert.True(answer.Status == 200, answer.Body);
        return answer.Object;
    }

    private Task RestrictAliceAsync(PermissionLevel level) =>
        _world.ReplaceAsync(FirmWorld.Member(Alice, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.Documents, level)));

    // Python's {**UPLOAD, **overrides}: merged here, so no key repeats.
    private Task<Answer> Add(string caseId, string? overrides = null, string subject = Alice)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(Upload)!.AsObject();
        if (overrides is not null)
        {
            foreach (var (key, value) in System.Text.Json.Nodes.JsonNode.Parse("{" + overrides + "}")!.AsObject())
            {
                body[key] = value?.DeepClone();
            }
        }

        return _world.Post($"/v1/cases/{caseId}/documents", body.ToJsonString(), subject);
    }

    private async Task<string> AddedAsync(string caseId, string? overrides = null)
    {
        var answer = await Add(caseId, overrides);
        Assert.True(answer.Status == 201, answer.Body);
        return answer.Object["document"]!.AsObject().Str("id")!;
    }

    // Created, then PUT through the capability (the blob store's stand-in).
    private async Task<string> UploadedAsync(string caseId, long byteSize = 2048)
    {
        var documentId = await AddedAsync(caseId);
        Blobs.AcceptUpload($"cases/{caseId}/{documentId}", byteSize, "etag-1");
        return documentId;
    }

    private Task<Answer> Complete(string caseId, string documentId, string subject = Alice) =>
        _world.Post($"/v1/cases/{caseId}/documents/{documentId}/complete", null, subject);
}
