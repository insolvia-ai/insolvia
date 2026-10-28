using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Documents;
using Insolvia.Core.Firms;
using Insolvia.Core.Jobs;
using Insolvia.Core.Memory;
using Insolvia.Core.Packets;
using Insolvia.Core.Ports;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_job_routes.py and test_packet_routes.py, translated: accept and read a
/// job (tenancy, idempotency per kind/document/options, the record BEFORE the
/// message, the pinned wire message, 503 without a queue, an enqueue failure
/// that cannot wedge), and list/download packets.
/// </summary>
public sealed class JobRouteTests : IAsyncLifetime, IDisposable
{
    private const string PlainOptions = """{"draftWatermark":false,"printDate":false,"signaturePages":"all","signElectronically":false,"amendedOnly":false}""";

    private readonly CaseWorld _world = new();

    private MemoryJobSender Sender => (MemoryJobSender)_world.Service<IJobSender>();

    private IJobStore Jobs => _world.Service<IJobStore>();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    private Task<Answer> AcceptEcho(string caseId, string subject = Alice) => _world.Post($"/v1/cases/{caseId}/jobs", """{"kind": "echo"}""", subject);

    private Task<Answer> AcceptPacket(string caseId, string? options = null) =>
        _world.Post($"/v1/cases/{caseId}/jobs", options is null ? """{"kind": "packet_assembly"}""" : $$"""{"kind": "packet_assembly", "options": {{options}}}""", Alice);

    private Task<Answer> AcceptExtraction(string caseId, string documentId) =>
        _world.Post($"/v1/cases/{caseId}/jobs", $$"""{"kind": "document_extraction", "documentId": "{{documentId}}"}""", Alice);

    private async Task<string> PlantDocumentAsync(string caseId, string kind = "credit_report")
    {
        var draft = DocumentRules.ParseUpload(JsonNode.Parse($$"""{"kind": "{{kind}}", "fileName": "synthetic.pdf", "contentType": "application/pdf", "byteSize": 100}""")!.AsObject());
        var document = DocumentRules.Confirm(DocumentRules.Create(draft, caseId, Alice), new StoredBlob(100, new string('e', 32)));
        await _world.Service<IDocumentStore>().CreateAsync(document, HttpAssert.Ct);
        return document.Id;
    }

    // ── Auth and ownership ──────────────────────────────────────────

    [Theory]
    [InlineData("POST", "/v1/cases/any-id/jobs")]
    [InlineData("GET", "/v1/cases/any-id/jobs/any-job")]
    [InlineData("GET", "/v1/cases/any-id/packets")]
    [InlineData("GET", "/v1/cases/any-id/packets/any-packet/url")]
    [InlineData("GET", "/v1/cases/any-id/summary")]
    public async Task Every_route_refuses_an_unauthenticated_caller(string method, string path) =>
        Assert.Equal(401, (await _world.World.SendAsync(new HttpMethod(method), path, json: "{}")).Status);

    [Fact]
    public async Task Another_firms_case_is_not_found_on_accept()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(404, (await AcceptEcho(caseId, Carol)).Status);
        Assert.Empty(Sender.Bodies);
    }

    [Fact]
    public async Task A_job_is_invisible_to_another_firm()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var jobId = (await AcceptEcho(caseId)).Str("id");
        var foreign = await _world.Get($"/v1/cases/{caseId}/jobs/{jobId}", Carol);
        var missing = await _world.Get("/v1/cases/no-such-case/jobs/no-such-id", Carol);
        Assert.Equal(404, foreign.Status);
        Assert.Equal(missing.Body, foreign.Body);
    }

    [Fact]
    public async Task A_job_id_does_not_resolve_through_another_case()
    {
        var first = await _world.OpenCaseIdAsync();
        var second = await _world.OpenCaseIdAsync();
        var jobId = (await AcceptEcho(first)).Str("id");
        Assert.Equal(404, (await _world.Get($"/v1/cases/{second}/jobs/{jobId}")).Status);
    }

    [Fact]
    public async Task View_only_staff_can_read_but_not_accept()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var jobId = (await AcceptEcho(caseId)).Str("id");
        await _world.ReplaceAsync(FirmWorld.Member(Dana, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.Cases, PermissionLevel.ViewOnly)));
        Assert.Equal(403, (await AcceptEcho(caseId, Dana)).Status);
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}/jobs/{jobId}", Dana)).Status);
    }

    // ── Accepting ───────────────────────────────────────────────────

    [Fact]
    public async Task Accepting_a_job_records_and_enqueues()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var answer = await AcceptEcho(caseId);
        Assert.Equal(202, answer.Status);
        var body = answer.Object;
        Assert.Equal("echo", body.Str("kind"));
        Assert.Equal("queued", body.Str("status"));
        Assert.Equal(Alice, body.Str("createdBy"));
        Assert.Equal(0, body["attempts"]!.GetValue<int>());
        Assert.Equal(["attempts", "createdAt", "createdBy", "id", "kind", "status", "updatedAt"], Answer.Keys(body));

        // The record before the message; the message the pinned wire shape.
        Assert.NotNull(await Jobs.GetAsync(caseId, body.Str("id")!, HttpAssert.Ct));
        Assert.Equal([$$"""{"version":1,"jobId":"{{body.Str("id")}}","caseId":"{{caseId}}","kind":"echo"}"""], Sender.Bodies);
    }

    [Fact]
    public async Task An_unknown_kind_is_rejected_per_field()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var answer = await _world.Post($"/v1/cases/{caseId}/jobs", """{"kind": "assemble_packet_from_the_future"}""", Alice);
        Assert.Equal(400, answer.Status);
        Assert.Contains("kind", answer.FieldErrors);
    }

    [Fact]
    public async Task Accepting_an_extraction_names_its_document()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await PlantDocumentAsync(caseId);
        var answer = await AcceptExtraction(caseId, documentId);
        Assert.Equal(202, answer.Status);
        Assert.Equal("document_extraction", answer.Str("kind"));
        Assert.Equal(documentId, answer.Str("documentId"));
        Assert.Equal(documentId, Assert.Single(Sender.Messages)["documentId"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_document_is_not_found_on_accept()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(404, (await AcceptExtraction(caseId, "not-a-document")).Status);
    }

    [Fact]
    public async Task An_unextractable_document_kind_is_refused_at_accept()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var documentId = await PlantDocumentAsync(caseId, "bank_statement");
        Assert.Equal(400, (await AcceptExtraction(caseId, documentId)).Status);
    }

    [Fact]
    public async Task Extraction_idempotency_is_per_document()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var firstDoc = await PlantDocumentAsync(caseId);
        var secondDoc = await PlantDocumentAsync(caseId);
        var first = (await AcceptExtraction(caseId, firstDoc)).Str("id");
        var repeat = (await AcceptExtraction(caseId, firstDoc)).Str("id");
        var other = (await AcceptExtraction(caseId, secondDoc)).Str("id");
        Assert.Equal(first, repeat);
        Assert.NotEqual(first, other);
        Assert.Equal(2, (await Jobs.ListForCaseAsync(caseId, HttpAssert.Ct)).Count);
    }

    [Fact]
    public async Task Packet_assembly_with_no_options_stores_the_plain_defaults()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var body = (await AcceptPacket(caseId)).Object;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(PlainOptions), body["options"]));
        Assert.True(JsonNode.DeepEquals(body["options"], (await Jobs.GetAsync(caseId, body.Str("id")!, HttpAssert.Ct))!.Options));
    }

    [Fact]
    public async Task Packet_assembly_options_are_canonicalised_and_stored()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var body = (await AcceptPacket(caseId, """{"draftWatermark": true}""")).Object;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(PlainOptions.Replace("\"draftWatermark\":false", "\"draftWatermark\":true", StringComparison.Ordinal)), body["options"]));
    }

    [Fact]
    public async Task An_unknown_form_in_the_subset_is_rejected()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(400, (await AcceptPacket(caseId, """{"forms": ["b101", "not-a-real-form"]}""")).Status);
    }

    [Fact]
    public async Task A_malformed_options_shape_is_rejected_per_field()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var answer = await AcceptPacket(caseId, """{"signaturePages": "sideways"}""");
        Assert.Equal(400, answer.Status);
        Assert.Contains("signaturePages", answer.FieldErrors);
    }

    [Fact]
    public async Task Options_on_a_kind_that_does_not_take_them_is_rejected()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(400, (await _world.Post($"/v1/cases/{caseId}/jobs", """{"kind": "echo", "options": {"draftWatermark": true}}""", Alice)).Status);
    }

    [Fact]
    public async Task Idempotency_is_scoped_per_options()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var plain = (await AcceptPacket(caseId)).Str("id");
        var draft = (await AcceptPacket(caseId, """{"draftWatermark": true}""")).Str("id");
        Assert.NotEqual(plain, draft);
        Assert.Equal(plain, (await AcceptPacket(caseId)).Str("id"));
        Assert.Equal(draft, (await AcceptPacket(caseId, """{"draftWatermark": true}""")).Str("id"));
        Assert.Equal(2, (await Jobs.ListForCaseAsync(caseId, HttpAssert.Ct)).Count);
    }

    [Fact]
    public async Task A_repeat_accept_returns_the_active_job_not_a_duplicate_and_is_not_logged_again()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var first = (await AcceptEcho(caseId)).Str("id");
        var second = await AcceptEcho(caseId);
        Assert.Equal(202, second.Status);
        Assert.Equal(first, second.Str("id"));
        Assert.Single(await Jobs.ListForCaseAsync(caseId, HttpAssert.Ct));
        Assert.Single(Sender.Bodies);
        Assert.Single(_world.Events, e => e.Action == AccessAction.JobAccept);
    }

    [Fact]
    public async Task A_finished_job_does_not_block_a_new_accept()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var first = (await AcceptEcho(caseId)).Str("id")!;
        var stored = (await Jobs.GetAsync(caseId, first, HttpAssert.Ct))!;
        var running = (await Jobs.UpdateAsync(JobRules.StartAttempt(stored), JobStatus.Queued, HttpAssert.Ct))!;
        await Jobs.UpdateAsync(JobRules.Complete(running, new JsonObject { ["echo"] = first }), JobStatus.Running, HttpAssert.Ct);
        Assert.NotEqual(first, (await AcceptEcho(caseId)).Str("id"));
    }

    [Fact]
    public async Task Without_a_queue_the_accept_answers_503()
    {
        using var world = new CaseWorld(services => services.RemoveAll<IJobSender>());
        await world.SeedAsync();
        var caseId = await world.OpenCaseIdAsync();
        var answer = await world.Post($"/v1/cases/{caseId}/jobs", """{"kind": "echo"}""");
        Assert.Equal(503, answer.Status);
        Assert.Equal("PipelineUnavailable", answer.Str("error"));
        Assert.Empty(await world.Service<IJobStore>().ListForCaseAsync(caseId, HttpAssert.Ct));
    }

    [Fact]
    public async Task An_enqueue_failure_fails_the_job_rather_than_wedging_it()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Sender.FailWith = new IOException("sqs unreachable");
        Assert.Equal(500, (await AcceptEcho(caseId)).Status);
        var job = Assert.Single(await Jobs.ListForCaseAsync(caseId, HttpAssert.Ct));
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("enqueue_failed", job.Failure!.Category);

        Sender.FailWith = null;
        var retry = await AcceptEcho(caseId);
        Assert.Equal(202, retry.Status);
        Assert.NotEqual(job.Id, retry.Str("id"));
    }

    // ── Status reads ────────────────────────────────────────────────

    [Fact]
    public async Task A_jobs_status_is_read_back_and_a_missing_one_is_404()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var jobId = (await AcceptEcho(caseId)).Str("id");
        var answer = await _world.Get($"/v1/cases/{caseId}/jobs/{jobId}");
        Assert.Equal(200, answer.Status);
        Assert.Equal("queued", answer.Str("status"));
        Assert.Equal("""{"error":"NotFoundError","message":"job not found"}""", (await _world.Get($"/v1/cases/{caseId}/jobs/no-such-job")).Body);
    }

    [Fact]
    public async Task A_failed_jobs_reason_reaches_the_preparer()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var jobId = (await AcceptEcho(caseId)).Str("id")!;
        var running = (await Jobs.UpdateAsync(JobRules.StartAttempt((await Jobs.GetAsync(caseId, jobId, HttpAssert.Ct))!), JobStatus.Queued, HttpAssert.Ct))!;
        await Jobs.UpdateAsync(JobRules.Fail(running, new JobFailure("case_incomplete", "The case has no debtor yet.")), JobStatus.Running, HttpAssert.Ct);
        var body = (await _world.Get($"/v1/cases/{caseId}/jobs/{jobId}")).Object;
        Assert.Equal("failed", body.Str("status"));
        Assert.Equal("""{"category":"case_incomplete","message":"The case has no debtor yet."}""", body["failure"]!.ToJsonString());
    }

    // ── Packets (test_packet_routes.py) ─────────────────────────────

    private async Task<PacketItem> StorePacketAsync(string caseId, DateTimeOffset createdAt)
    {
        var current = (await _world.StoredAsync(caseId))!;
        var id = Guid.NewGuid().ToString();
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal) { ["form/b101"] = "2024-06-22" };
        var packet = new PacketItem(id, caseId, Guid.NewGuid().ToString(), "chapter7-packet.zip", "application/zip", 10, new string('a', 64), $"cases/{caseId}/packets/{id}", revisions.ToValueDictionary(), "dollar_amounts/2025-04-01", 1, Alice, createdAt, null, []);
        Assert.True(await _world.Service<IPacketStore>().CreateAsync(packet, CaseRules.Pin(current, revisions, "dollar_amounts/2025-04-01"), current.UpdatedAt, cancellationToken: HttpAssert.Ct));
        return packet;
    }

    [Fact]
    public async Task Listing_returns_packets_newest_first_without_a_storage_ref()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var first = await StorePacketAsync(caseId, FirmWorld.Seeded);
        var second = await StorePacketAsync(caseId, FirmWorld.Seeded.AddMinutes(1));
        var listed = (await _world.Get($"/v1/cases/{caseId}/packets")).Object["packets"]!.AsArray();
        Assert.Equal([second.Id, first.Id], listed.Select(p => p!["id"]!.GetValue<string>()));
        Assert.False(listed[1]!.AsObject().ContainsKey("storageRef"));
        Assert.Equal("""{"form/b101":"2024-06-22"}""", listed[1]!["formRevisions"]!.ToJsonString());

        // A packet written before options existed reads as the plain filing set.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(PlainOptions), listed[1]!["options"]));
        Assert.Contains(_world.Events, e => e.Action == AccessAction.PacketRead && e.CaseId == caseId);
    }

    [Fact]
    public async Task Another_firms_packets_are_not_found_and_a_packet_does_not_resolve_through_another_case()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var other = await _world.OpenCaseIdAsync();
        var packet = await StorePacketAsync(caseId, FirmWorld.Seeded);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/packets", Carol)).Status);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/packets/{packet.Id}/url", Carol)).Status);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{other}/packets/{packet.Id}/url")).Status);
    }

    [Fact]
    public async Task The_url_route_mints_a_short_lived_get_and_logs_the_download()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var packet = await StorePacketAsync(caseId, FirmWorld.Seeded);
        var answer = await _world.Get($"/v1/cases/{caseId}/packets/{packet.Id}/url");
        Assert.Equal(200, answer.Status);
        Assert.Equal("GET", answer.Str("method"));
        Assert.NotNull(answer.Str("expiresAt"));
        var minted = ((MemoryDocumentBlobStore)_world.Service<IDocumentBlobStore>()).Minted[^1];
        Assert.Equal(("GET", packet.StorageRef, 300), (minted.Method, minted.StorageRef, minted.ExpiresIn));
        Assert.Contains(_world.Events, e => e.Action == AccessAction.PacketDownload && e.CaseId == caseId);
    }

    [Fact]
    public async Task A_pinned_case_reports_its_revisions_and_an_unpinned_one_omits_them()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var unpinned = (await _world.Get($"/v1/cases/{caseId}")).Object;
        Assert.False(unpinned.ContainsKey("formRevisions"));
        Assert.False(unpinned.ContainsKey("constantsSetId"));
        await StorePacketAsync(caseId, FirmWorld.Seeded);
        Assert.Equal("""{"form/b101":"2024-06-22"}""", (await _world.Get($"/v1/cases/{caseId}")).Object["formRevisions"]!.ToJsonString());
    }
}
