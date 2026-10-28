using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// services/api tests/unit/test_portal_documents.py, the client half (the
/// staff half is in <see cref="DocumentRouteTests"/>): a client's upload
/// answers its request through the staff seam, a client sees only their own
/// uploads, every request is logged as the client, and a client's upload is
/// never auto-extracted while a staff one of the same kind is.
/// </summary>
public sealed class PortalDocumentRouteTests : IAsyncLifetime, IDisposable
{
    private const string Upload = """ "fileName": "statement.pdf", "contentType": "application/pdf", "byteSize": 2048 """;

    private readonly CaseWorld _world = new();
    private string _caseId = string.Empty;
    private string _pat = string.Empty;

    private MemoryDocumentBlobStore Blobs => (MemoryDocumentBlobStore)_world.Service<IDocumentBlobStore>();

    private IDocumentStore Documents => _world.Service<IDocumentStore>();

    public async ValueTask InitializeAsync()
    {
        await _world.SeedAsync();
        _caseId = await _world.OpenCaseIdAsync();
        _pat = await _world.InvitedAsync(_caseId);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── The ADR's done-when ─────────────────────────────────────────

    [Fact]
    public async Task A_client_upload_satisfies_its_request_and_the_case_counts_it()
    {
        var applied = await ApplyChecklistAsync(_caseId);
        Assert.Equal(6, applied["added"]!.GetValue<int>());
        Assert.Equal("""{"outstanding":6,"received":0,"total":6,"waived":0}""", Sorted(applied["progress"]));

        var seen = await _world.PortalGet("/v1/portal/document-requests", _pat);
        Assert.Equal(200, seen.Status);
        var bank = ByTitle(seen.Object["requests"]!, "Bank statements");
        Assert.Equal("requested", bank.Str("status"));

        var uploaded = await ClientUploadAsync(bank.Str("id")!);
        Assert.Equal("stored", uploaded.Str("status"));

        var listed = (await _world.Get($"/v1/cases/{_caseId}/document-requests")).Object;
        Assert.Equal("received", ByTitle(listed["requests"]!, "Bank statements").Str("status"));
        Assert.Equal($"[\"{uploaded.Str("id")}\"]", ByTitle(listed["requests"]!, "Bank statements")["documentIds"]!.ToJsonString());
        Assert.Equal("""{"outstanding":5,"received":1,"total":6,"waived":0}""", Sorted(listed["progress"]));

        var mine = (await _world.PortalGet("/v1/portal/document-requests", _pat)).Object;
        var mineBank = ByTitle(mine["requests"]!, "Bank statements");
        Assert.Equal("received", mineBank.Str("status"));
        Assert.Equal(uploaded.Str("id"), Assert.Single(mineBank["uploads"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Equal(1, mine["progress"]!["received"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_client_upload_lands_on_the_case_as_the_clients_with_the_requests_kind()
    {
        var tax = ByTitle((await ApplyChecklistAsync(_caseId))["requests"]!, "Tax returns for the last two years");

        var uploaded = await ClientUploadAsync(tax.Str("id")!, """, "kind": "credit_report" """);

        var document = Assert.Single((await _world.Get($"/v1/cases/{_caseId}/documents")).Object["documents"]!.AsArray())!.AsObject();
        Assert.Equal((uploaded.Str("id"), "tax_return", "client", tax.Str("id")), (document.Str("id"), document.Str("kind"), document.Str("channel"), document.Str("requestId")));
        var minted = Blobs.Minted.First(url => url.Method == "PUT");
        Assert.Equal($"cases/{_caseId}/{uploaded.Str("id")}", minted.StorageRef);
        Assert.Equal([minted.StorageRef], Blobs.Cleared);
    }

    // ── What a client sees ──────────────────────────────────────────

    [Fact]
    public async Task A_client_sees_their_own_uploads_and_never_a_staff_one()
    {
        var bank = ByTitle((await ApplyChecklistAsync(_caseId))["requests"]!, "Bank statements");
        var created = await _world.Post($"/v1/cases/{_caseId}/documents", $$"""{"kind": "bank_statement", "requestId": "{{bank.Str("id")}}", {{Upload}}}""");
        var documentId = created.Object["document"]!["id"]!.GetValue<string>();
        Blobs.AcceptUpload($"cases/{_caseId}/{documentId}", 2048);
        Assert.Equal(200, (await _world.Post($"/v1/cases/{_caseId}/documents/{documentId}/complete", null)).Status);

        var body = await _world.PortalGet("/v1/portal/document-requests", _pat);

        var seen = ByTitle(body.Object["requests"]!, "Bank statements");
        Assert.Equal("received", seen.Str("status"));
        Assert.Empty(seen["uploads"]!.AsArray());
        Assert.DoesNotContain(documentId, body.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(_caseId, body.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_cannot_complete_a_staff_upload()
    {
        var created = await _world.Post($"/v1/cases/{_caseId}/documents", $$"""{"kind": "other", {{Upload}}}""");
        var documentId = created.Object["document"]!["id"]!.GetValue<string>();

        Assert.Equal(404, (await _world.PortalPost($"/v1/portal/documents/{documentId}/complete", null, _pat)).Status);
    }

    [Fact]
    public async Task The_portal_upload_answer_names_no_case()
    {
        var bank = ByTitle((await ApplyChecklistAsync(_caseId))["requests"]!, "Bank statements");

        var answer = await _world.PortalPost("/v1/portal/documents", $$"""{"requestId": "{{bank.Str("id")}}", {{Upload}}}""", _pat);

        Assert.Equal(201, answer.Status);
        Assert.Equal(["byteSize", "contentType", "fileName", "id", "requestId", "status", "uploadedAt"], Answer.Keys(answer.Object["document"]));
        Assert.Equal("PUT", answer.Object["upload"]!["method"]!.GetValue<string>());
        Assert.False(answer.Object["upload"]!["headers"]!.AsObject().ContainsKey("x-amz-server-side-encryption"));
        Assert.DoesNotContain(_caseId, answer.Object["document"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completing_before_the_object_landed_is_a_conflict()
    {
        var bank = ByTitle((await ApplyChecklistAsync(_caseId))["requests"]!, "Bank statements");
        var documentId = (await _world.PortalPost("/v1/portal/documents", $$"""{"requestId": "{{bank.Str("id")}}", {{Upload}}}""", _pat)).Object["document"]!["id"]!.GetValue<string>();

        Assert.Equal(409, (await _world.PortalPost($"/v1/portal/documents/{documentId}/complete", null, _pat)).Status);
    }

    // ── What a client cannot upload against ─────────────────────────

    [Fact]
    public async Task A_waived_request_takes_no_upload()
    {
        var bank = ByTitle((await ApplyChecklistAsync(_caseId))["requests"]!, "Bank statements");
        Assert.Equal(200, (await _world.Patch($"/v1/cases/{_caseId}/document-requests/{bank.Str("id")}", """{"status": "waived"}""")).Status);

        var answer = await _world.PortalPost("/v1/portal/documents", $$"""{"requestId": "{{bank.Str("id")}}", {{Upload}}}""", _pat);

        Assert.Equal(400, answer.Status);
        Assert.True(answer.Object["fields"]!.AsObject().ContainsKey("requestId"));
    }

    [Fact]
    public async Task Another_cases_request_does_not_resolve_for_the_client()
    {
        var other = await _world.OpenCaseIdAsync();
        var foreign = (await ApplyChecklistAsync(other))["requests"]![0]!.AsObject();

        var answer = await _world.PortalPost("/v1/portal/documents", $$"""{"requestId": "{{foreign.Str("id")}}", {{Upload}}}""", _pat);

        Assert.Equal(400, answer.Status);
        Assert.Empty(await Documents.ListForCaseAsync(_caseId, HttpAssert.Ct));
        Assert.Empty(await Documents.ListForCaseAsync(other, HttpAssert.Ct));
    }

    [Fact]
    public async Task An_upload_names_a_request()
    {
        var answer = await _world.PortalPost("/v1/portal/documents", "{" + Upload + "}", _pat);

        Assert.Equal(400, answer.Status);
        Assert.True(answer.Object["fields"]!.AsObject().ContainsKey("requestId"));
    }

    [Fact]
    public async Task A_revoked_client_cannot_upload()
    {
        var bank = ByTitle((await ApplyChecklistAsync(_caseId))["requests"]!, "Bank statements");
        await _world.Delete($"/v1/cases/{_caseId}/portal/clients/{_pat}");

        Assert.Equal(403, (await _world.PortalPost("/v1/portal/documents", $$"""{"requestId": "{{bank.Str("id")}}", {{Upload}}}""", _pat)).Status);
    }

    // ── The access log ──────────────────────────────────────────────

    [Fact]
    public async Task Every_portal_document_request_is_logged_as_the_client()
    {
        var bank = ByTitle((await ApplyChecklistAsync(_caseId))["requests"]!, "Bank statements");
        var before = _world.Events.Count;

        await _world.PortalGet("/v1/portal/document-requests", _pat);
        await ClientUploadAsync(bank.Str("id")!);

        var rows = _world.Events.Skip(before).Select(e => (e.SubjectId, e.Principal, e.Action, e.Outcome)).ToList();
        Assert.Equal(
            [
                (_caseId, _pat, AccessAction.PortalRead, AccessOutcome.Allowed),
                (_caseId, _pat, AccessAction.DocumentCreate, AccessOutcome.Allowed),
                (_caseId, _pat, AccessAction.DocumentCreate, AccessOutcome.Allowed),
            ],
            rows);
    }

    private static JsonObject ByTitle(JsonNode requests, string title) =>
        requests.AsArray().Single(r => r!["title"]!.GetValue<string>() == title)!.AsObject();

    private static string Sorted(JsonNode? node) =>
        new JsonObject([.. node!.AsObject().OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))]).ToJsonString();

    [Fact]
    public async Task A_clients_upload_is_never_auto_extracted()
    {
        // Pay stubs are extractable: the staff upload proves the trigger is
        // live in this host, so the client's silence is the channel.
        var jobs = _world.Service<Core.Ports.IJobStore>();
        var sender = (Core.Memory.MemoryJobSender)_world.Service<Core.Ports.IJobSender>();
        var stubs = ByTitle((await ApplyChecklistAsync(_caseId))["requests"]!, "Pay stubs for the last six months");

        await ClientUploadAsync(stubs.Str("id")!);
        Assert.Empty(await jobs.ListForCaseAsync(_caseId, HttpAssert.Ct));
        Assert.Empty(sender.Bodies);

        var created = await _world.Post($"/v1/cases/{_caseId}/documents", $$"""{"kind": "pay_stub", "requestId": "{{stubs.Str("id")}}", {{Upload}}}""");
        Assert.True(created.Status == 201, created.Body);
        var documentId = created.Object["document"]!["id"]!.GetValue<string>();
        Assert.Equal("staff", created.Object["document"]!["channel"]!.GetValue<string>());
        Blobs.AcceptUpload($"cases/{_caseId}/{documentId}", 2048);
        Assert.Equal(200, (await _world.Post($"/v1/cases/{_caseId}/documents/{documentId}/complete", null)).Status);
        Assert.Equal(documentId, Assert.Single(await jobs.ListForCaseAsync(_caseId, HttpAssert.Ct)).DocumentId);
    }

    private async Task<JsonObject> ApplyChecklistAsync(string caseId)
    {
        var answer = await _world.Post($"/v1/cases/{caseId}/document-requests/from-checklist", null);
        Assert.True(answer.Status == 200, answer.Body);
        return answer.Object;
    }

    // Create, PUT (the blob store's stand-in) and complete.
    private async Task<JsonObject> ClientUploadAsync(string requestId, string extra = "")
    {
        var created = await _world.PortalPost("/v1/portal/documents", $$"""{"requestId": "{{requestId}}", {{Upload}}{{extra}}}""", _pat);
        Assert.True(created.Status == 201, created.Body);
        var documentId = created.Object["document"]!["id"]!.GetValue<string>();

        // The client's answer names no case, so find the row behind the API.
        var stored = (await Documents.GetAsync(_caseId, documentId, HttpAssert.Ct))!;
        Blobs.AcceptUpload(stored.StorageRef, stored.ByteSize);
        var completed = await _world.PortalPost($"/v1/portal/documents/{documentId}/complete", null, _pat);
        Assert.True(completed.Status == 200, completed.Body);
        return completed.Object["document"]!.AsObject();
    }
}
