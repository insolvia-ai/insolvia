extern alias worker;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Claims;
using Insolvia.Core.Creditors;
using Insolvia.Core.Documents;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;
using Insolvia.Forms.Tests.Support;
using static Insolvia.Api.Tests.Support.CaseWorld;
using WorkerComposition = worker::Insolvia.Worker.WorkerComposition;
using WorkerLog = worker::Insolvia.Worker.RecordingJobLog;
using WorkerStores = worker::Insolvia.Worker.WorkerStores;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// The pipeline end to end, in process (ADR 0018's local story): the API
/// accepts a job and writes the row BEFORE the identifiers-only message; the
/// WORKER consumes that exact message body through the same dispatch the
/// Lambda and the poller run, over the same stores; the API then serves the
/// status and the result. Nothing is faked between the two halves but SQS
/// itself (the memory sender's recorded bodies are handed to the worker as
/// the Lambda event shape).
/// </summary>
public sealed class PipelineEndToEndTests(ITestOutputHelper output) : IAsyncLifetime, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly CaseWorld _world = new();

    private MemoryJobSender Sender => (MemoryJobSender)_world.Service<IJobSender>();

    private MemoryDocumentBlobStore Blobs => (MemoryDocumentBlobStore)_world.Service<IDocumentBlobStore>();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    private WorkerStores Stores() => new(
        _world.Service<IJobStore>(),
        _world.Service<ICaseStore>(),
        _world.Service<IDebtorStore>(),
        _world.Service<ICaseEntityStore>(),
        _world.Service<IPacketStore>(),
        _world.Service<IDocumentStore>(),
        _world.Service<ICandidateStore>(),
        _world.Service<IAccessLog>(),
        _world.Service<ITaxIdStore>());

    private WorkerComposition Worker(IExtractionModel? extraction = null) =>
        new(Blobs, _world.Service<ITaxIdCipher>(), null, extraction) { Clock = new FixedTime(Now), Log = new WorkerLog() };

    /// <summary>What the poller does: receive every message sent, hand each over as the Lambda event, delete on success.</summary>
    private async Task<int> DeliverAsync(WorkerComposition composition)
    {
        var bodies = Sender.Drain();
        foreach (var body in bodies)
        {
            output.WriteLine($"SQS body: {body}");
            await composition.HandleAsync(new JsonObject { ["Records"] = new JsonArray(new JsonObject { ["body"] = body }) }, Stores(), HttpAssert.Ct);
        }

        return bodies.Count;
    }

    /// <summary>The reference Chapter 7 case laid into the case the API opened: its debtors (tax ids sealed), every record.</summary>
    private async Task<string> SeedReferenceCaseAsync()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var opened = (await _world.StoredAsync(caseId))!;
        var file = CaseFiles.Named("reference-ch7");
        var data = CaseDatas.Of(file with
        {
            Case = opened,
            Debtors = [.. file.Debtors.Select(debtor => debtor with { CaseId = caseId })],
        });
        var debtors = _world.Service<IDebtorStore>();
        foreach (var debtor in data.Debtors)
        {
            await debtors.PutAsync(debtor, HttpAssert.Ct);
            if (debtor.TaxId is { } reference && file.TaxIds.TryGetValue(debtor.FilingRole.ToWire(), out var digits))
            {
                await TaxIdRules.StoreAsync(new TaxIdInput(reference.Kind, digits), reference, opened.FirmId, caseId, _world.Service<ITaxIdCipher>(), _world.Service<ITaxIdStore>(), cancellationToken: HttpAssert.Ct);
            }
        }

        foreach (var (_, entities) in data.Collections())
        {
            foreach (var entity in entities)
            {
                await _world.Service<ICaseEntityStore>().CreateAsync(entity, HttpAssert.Ct);
            }
        }

        return caseId;
    }

    [Fact]
    public async Task Accept_message_worker_packet_assembly_succeeded_and_the_packet_url_is_served()
    {
        var caseId = await SeedReferenceCaseAsync();

        // 1. Accept: 202, queued, the row written before the message.
        var accepted = await _world.Post($"/v1/cases/{caseId}/jobs", """{"kind": "packet_assembly"}""");
        Assert.Equal(202, accepted.Status);
        var jobId = accepted.Str("id")!;
        output.WriteLine($"accept: {accepted.Status} {accepted.Body}");
        Assert.Equal("queued", accepted.Str("status"));
        Assert.Equal($$"""{"version":1,"jobId":"{{jobId}}","caseId":"{{caseId}}","kind":"packet_assembly"}""", Assert.Single(Sender.Bodies));

        // 2. Deliver: the worker consumes exactly that message.
        Assert.Equal(1, await DeliverAsync(Worker()));

        // 3. Status: succeeded, one attempt, the packet in the result.
        var status = await _world.Get($"/v1/cases/{caseId}/jobs/{jobId}");
        output.WriteLine($"status: {status.Status} {Summarise(status.Object)}");
        Assert.Equal(200, status.Status);
        Assert.Equal("succeeded", status.Str("status"));
        Assert.Equal(1, status.Object["attempts"]!.GetValue<int>());
        var result = status.Object["result"]!;
        Assert.True(result["outcome"]!.GetValue<string>() == "assembled", result.ToJsonString());
        var packetId = result["packet"]!["id"]!.GetValue<string>();

        // 4. The packet is listed, its URL minted, its bytes hash to the record.
        var listed = await _world.Get($"/v1/cases/{caseId}/packets");
        Assert.Equal([packetId], listed.Object["packets"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()));
        var url = await _world.Get($"/v1/cases/{caseId}/packets/{packetId}/url");
        output.WriteLine($"packet url: {url.Status} {url.Body}");
        Assert.Equal(200, url.Status);
        Assert.Equal("GET", url.Str("method"));
        var stored = (await _world.Service<IPacketStore>().GetAsync(caseId, packetId, HttpAssert.Ct))!;
        var bytes = (await Blobs.GetBytesAsync(stored.StorageRef, HttpAssert.Ct))!;
        Assert.Equal(stored.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal(result["packet"]!["sha256"]!.GetValue<string>(), stored.Sha256);
        output.WriteLine($"packet: {stored.FileName}, {bytes.Length} bytes, sha256 {stored.Sha256}, {stored.Parts.Count} parts");

        // 5. The case is pinned, and a redelivery of the same message is a no-op.
        Assert.NotNull((await _world.StoredAsync(caseId))!.ConstantsSetId);
        await Worker().HandleAsync(new JsonObject { ["Records"] = new JsonArray(new JsonObject { ["body"] = $$"""{"version":1,"jobId":"{{jobId}}","caseId":"{{caseId}}","kind":"packet_assembly"}""" }) }, Stores(), HttpAssert.Ct);
        Assert.Single((await _world.Get($"/v1/cases/{caseId}/packets")).Object["packets"]!.AsArray());
        Assert.Equal(1, (await _world.Get($"/v1/cases/{caseId}/jobs/{jobId}")).Object["attempts"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_review_without_a_key_fails_honestly_through_the_status_read()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var jobId = (await _world.Post($"/v1/cases/{caseId}/jobs", """{"kind": "petition_review"}""")).Str("id");
        await DeliverAsync(Worker());
        var body = (await _world.Get($"/v1/cases/{caseId}/jobs/{jobId}")).Object;
        Assert.Equal("failed", body.Str("status"));
        Assert.Equal("""{"category":"not_configured","message":"AI review is not configured in this environment yet."}""", body["failure"]!.ToJsonString());
    }

    [Fact]
    public async Task The_full_extraction_loop_ends_in_confirmed_case_data()
    {
        // test_extraction_review_routes.py's done-when: upload → the
        // auto-triggered job → the worker fills the queue → the API reviews
        // it → the case holds confirmed records, with no unreviewed path.
        var caseId = await _world.OpenCaseIdAsync();
        var upload = await _world.Post($"/v1/cases/{caseId}/documents", """{"kind": "credit_report", "fileName": "synthetic.pdf", "contentType": "application/pdf", "byteSize": 10}""");
        var documentId = upload.Object["document"]!["id"]!.GetValue<string>();
        Blobs.AcceptUpload($"cases/{caseId}/{documentId}", 10, content: "%PDF-1.7 x"u8.ToArray());
        Assert.Equal(200, (await _world.Post($"/v1/cases/{caseId}/documents/{documentId}/complete", null)).Status);

        var model = new ScriptedExtractionModel(JsonNode.Parse("""
            {"creditors": [
              {"name": "First Example Bank", "address": {"city": "Exampleville"}, "page": 2, "confidence": 0.95, "claims": [
                {"account_last4": "4321", "amount": "1234.56", "claim_class": "nonpriority_unsecured", "page": 2, "confidence": 0.9},
                {"account_last4": "9999-0000-1111-2222", "amount": "$8,050.00", "claim_class": "secured", "page": 3, "confidence": 0.8}]},
              {"name": "Example Collections LLC", "address": {"raw": "PO Box 9, Exampleville FL 33101"}, "page": 5, "confidence": 0.7, "claims": [
                {"account_last4": "0007", "amount": "310.00", "claim_class": "nonpriority_unsecured", "page": 5, "confidence": 0.75}]}]}
            """)!.AsObject());
        Assert.Equal(1, await DeliverAsync(Worker(model)));
        Assert.Equal("10", Assert.Single(model.Requests).Data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var queue = (await _world.Get($"/v1/cases/{caseId}/extraction/candidates?status=pending")).Object["candidates"]!.AsArray();
        Assert.Equal(5, queue.Count);
        foreach (var type in new[] { "creditors", "claims" })
        {
            foreach (var row in queue.Where(row => row!["entityType"]!.GetValue<string>() == type))
            {
                var reviewed = await _world.Post($"/v1/cases/{caseId}/extraction/candidates/{row!["id"]!.GetValue<string>()}/review", """{"action": "accept"}""");
                Assert.True(reviewed.Status == 200, reviewed.Body);
            }
        }

        var creditors = await _world.Service<ICaseEntityStore>().ListTypedAsync(caseId, CreditorRules.Kind, HttpAssert.Ct);
        var claims = await _world.Service<ICaseEntityStore>().ListTypedAsync(caseId, ClaimRules.Kind, HttpAssert.Ct);
        Assert.Equal(2, creditors.Count);
        Assert.Equal(3, claims.Count);
        Assert.Subset(creditors.Select(c => c.Id).ToHashSet(), claims.Select(c => c.Body.CreditorId!).ToHashSet());
        Assert.All(creditors.Cast<ICaseEntity>().Concat(claims), record => Assert.All(record.Provenance.Values, entry =>
        {
            Assert.Equal(Alice, entry.ConfirmedBy);
            Assert.NotNull(entry.ConfirmedAt);
        }));
        Assert.Empty((await _world.Get($"/v1/cases/{caseId}/extraction/candidates?status=pending")).Object["candidates"]!.AsArray());
        Assert.All((await _world.Get($"/v1/cases/{caseId}/extraction/candidates")).Object["candidates"]!.AsArray(), row => Assert.Equal("accepted", row!["status"]!.GetValue<string>()));
        output.WriteLine($"extraction loop: document {documentId} ({DocumentKind.CreditReport.ToWire()}) → 5 candidates → {creditors.Count} creditors + {claims.Count} claims confirmed");
    }

    private static string Summarise(JsonObject job) =>
        $"status={job.Str("status")} attempts={job["attempts"]} outcome={job["result"]?["outcome"]} packet={job["result"]?["packet"]?["id"]} file={job["result"]?["packet"]?["fileName"]} bytes={job["result"]?["packet"]?["byteSize"]}";

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
