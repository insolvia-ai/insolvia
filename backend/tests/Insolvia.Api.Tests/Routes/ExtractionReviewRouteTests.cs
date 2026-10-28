using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Candidates;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Claims;
using Insolvia.Core.Creditors;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using Insolvia.Core.Provenance;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_extraction_review_routes.py, translated: who may confirm is a
/// permission; the queue shows what a reviewer needs; acceptance is the one
/// door into the case, minting confirmed machine provenance (staff_typed for
/// a corrected field); a second review loses with a 409; candidate-id
/// indirection resolves to the accepted record.
/// </summary>
public sealed class ExtractionReviewRouteTests : IAsyncLifetime, IDisposable
{
    private const string DocumentId = "00000000-0000-4000-8000-00000000d0c1";

    private static readonly CandidateOrigin Extraction = new(OriginChannel.Extraction, "scripted-extractor", Alice);
    private static readonly CandidateOrigin Mcp = new(OriginChannel.Mcp, "harness-client", Alice);

    private readonly CaseWorld _world = new();

    private ICandidateStore Candidates => _world.Service<ICandidateStore>();

    private ICaseEntityStore Entities => _world.Service<ICaseEntityStore>();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    private async Task<Candidate> PlantAsync(string caseId, string entityType = "creditors", string? payload = null, CandidateOrigin? origin = null)
    {
        origin ??= Extraction;
        var extraction = origin.Channel == OriginChannel.Extraction;
        var candidate = CandidateRules.Create(
            new ProposalDraft(entityType, JsonNode.Parse(payload ?? """{"name": "First Example Bank", "address": {"city": "Exampleville"}}""")!.AsObject(), null, null),
            caseId,
            origin,
            extraction ? DocumentId : null,
            extraction ? 0.9 : null,
            extraction ? new JsonObject { ["document_id"] = DocumentId, ["page"] = 2 } : null);
        await Candidates.CreateAsync(candidate, HttpAssert.Ct);
        return candidate;
    }

    private Task<Answer> Queue(string caseId, string subject = Alice, string query = "") => _world.Get($"/v1/cases/{caseId}/extraction/candidates{query}", subject);

    private Task<Answer> Review(string caseId, string candidateId, string body, string subject = Alice) =>
        _world.Post($"/v1/cases/{caseId}/extraction/candidates/{candidateId}/review", body, subject);

    private async Task<IReadOnlyList<CaseEntity<CreditorBody>>> CreditorsAsync(string caseId) => await Entities.ListTypedAsync(caseId, CreditorRules.Kind, HttpAssert.Ct);

    // ── Who may confirm is a permission ─────────────────────────────

    [Theory]
    [InlineData("GET", "/v1/cases/any-id/extraction/candidates")]
    [InlineData("POST", "/v1/cases/any-id/extraction/candidates/any-cand/review")]
    public async Task Every_route_refuses_an_unauthenticated_caller(string method, string path) =>
        Assert.Equal(401, (await _world.World.SendAsync(new HttpMethod(method), path, json: "{}")).Status);

    [Fact]
    public async Task Hidden_is_a_403_on_every_route()
    {
        // Staff defaults leave extraction_review hidden.
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId);
        Assert.Equal(403, (await Queue(caseId, Greg)).Status);
        Assert.Equal(403, (await Review(caseId, candidate.Id, """{"action": "accept"}""", Greg)).Status);
    }

    [Fact]
    public async Task View_only_reads_the_queue_and_confirms_nothing()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId);
        await _world.ReplaceAsync(FirmWorld.Member(Dana, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.ExtractionReview, PermissionLevel.ViewOnly)));
        Assert.Equal(200, (await Queue(caseId, Dana)).Status);
        Assert.Equal(403, (await Review(caseId, candidate.Id, """{"action": "accept"}""", Dana)).Status);
        Assert.Equal(403, (await Review(caseId, candidate.Id, """{"action": "reject"}""", Dana)).Status);
        Assert.Equal(CandidateStatus.Pending, (await Candidates.GetAsync(caseId, candidate.Id, HttpAssert.Ct))!.Status);
        Assert.Empty(await CreditorsAsync(caseId));
    }

    [Fact]
    public async Task Another_firms_queue_is_not_found()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId);
        Assert.Equal(404, (await Queue(caseId, Carol)).Status);
        Assert.Equal(404, (await Review(caseId, candidate.Id, """{"action": "accept"}""", Carol)).Status);
        Assert.Contains(_world.Events, e => e.Action == AccessAction.ExtractionRead && e.Outcome == AccessOutcome.Denied);
    }

    // ── The queue ───────────────────────────────────────────────────

    [Fact]
    public async Task The_queue_lists_what_a_reviewer_needs()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId);
        var row = Assert.Single((await Queue(caseId, query: "?status=pending")).Object["candidates"]!.AsArray())!.AsObject();
        Assert.Equal(candidate.Id, row.Str("id"));
        Assert.Equal("creditors", row.Str("entityType"));
        Assert.Equal("pending", row.Str("status"));
        Assert.Equal("First Example Bank", row["payload"]!["name"]!.GetValue<string>());
        Assert.Equal("extraction", row["origin"]!["channel"]!.GetValue<string>());
        Assert.Equal("scripted-extractor", row["origin"]!["clientId"]!.GetValue<string>());
        Assert.Equal(DocumentId, row.Str("documentId"));
        Assert.Equal(2, row["locator"]!["page"]!.GetValue<int>());
        Assert.Equal(0.9, row["confidence"]!.GetValue<double>());
        Assert.False(row.ContainsKey("question"));
    }

    [Fact]
    public async Task The_status_filter_narrows_and_validates()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var kept = await PlantAsync(caseId);
        var rejected = await PlantAsync(caseId);
        Assert.Equal(200, (await Review(caseId, rejected.Id, """{"action": "reject"}""")).Status);
        Assert.Equal([kept.Id], (await Queue(caseId, query: "?status=pending")).Object["candidates"]!.AsArray().Select(row => row!["id"]!.GetValue<string>()));
        Assert.Equal(2, (await Queue(caseId)).Object["candidates"]!.AsArray().Count);
        Assert.Equal(400, (await Queue(caseId, query: "?status=maybe")).Status);
    }

    // ── Acceptance: the one door into the case ──────────────────────

    [Fact]
    public async Task Accepting_writes_the_record_with_confirmed_machine_provenance()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId);

        var answer = await Review(caseId, candidate.Id, """{"action": "accept"}""");

        Assert.Equal(200, answer.Status);
        Assert.Equal("accepted", answer.Object["candidate"]!["status"]!.GetValue<string>());
        Assert.Equal(answer.Object["record"]!["id"]!.GetValue<string>(), answer.Object["candidate"]!["resultingRecordId"]!.GetValue<string>());
        var record = Assert.Single(await CreditorsAsync(caseId));
        Assert.Equal("First Example Bank", record.Body.Name);
        var entry = record.Provenance["name"];
        Assert.Equal(ProvenanceSource.AiExtracted, entry.Source);
        Assert.Equal(Alice, entry.ConfirmedBy);
        Assert.NotNull(entry.ConfirmedAt);
        Assert.Equal(candidate.Id, entry.ExtractionId);
        Assert.Equal(DocumentId, entry.DocumentId);
        Assert.Equal(0.9, entry.Confidence);
        Assert.Equal(2, entry.Locator!["page"]!.GetValue<int>());

        var stored = (await Candidates.GetAsync(caseId, candidate.Id, HttpAssert.Ct))!;
        Assert.Equal(CandidateStatus.Accepted, stored.Status);
        Assert.Equal(Alice, stored.ConfirmedBy);
        Assert.Equal(record.Id, stored.ResultingRecordId);
        Assert.Equal(entry.ConfirmedAt, stored.ConfirmedAt);
    }

    [Fact]
    public async Task A_corrected_field_is_staff_typed_and_the_rest_stays_machine()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId);

        var answer = await Review(caseId, candidate.Id, """{"action": "accept", "correctedPayload": {"name": "First Example Bank NA", "address": {"city": "Exampleville"}}}""");

        Assert.Equal(200, answer.Status);
        Assert.Equal("corrected", answer.Object["candidate"]!["status"]!.GetValue<string>());
        var record = Assert.Single(await CreditorsAsync(caseId));
        Assert.Equal("First Example Bank NA", record.Body.Name);
        Assert.Equal(ProvenanceSource.StaffTyped, record.Provenance["name"].Source);
        Assert.Equal(Alice, record.Provenance["name"].ConfirmedBy);
        Assert.Equal(ProvenanceSource.AiExtracted, record.Provenance["address.city"].Source);
        var stored = (await Candidates.GetAsync(caseId, candidate.Id, HttpAssert.Ct))!;
        Assert.Equal(CandidateStatus.Corrected, stored.Status);
        Assert.Equal("First Example Bank NA", stored.CorrectedPayload!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_mcp_proposal_confirms_into_imported()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId, origin: Mcp);
        Assert.Equal(200, (await Review(caseId, candidate.Id, """{"action": "accept"}""")).Status);
        var record = Assert.Single(await CreditorsAsync(caseId));
        Assert.Equal(ProvenanceSource.Imported, record.Provenance["name"].Source);
        Assert.Equal(Alice, record.Provenance["name"].ConfirmedBy);
    }

    [Fact]
    public async Task Rejecting_retains_the_candidate_and_writes_nothing()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId);
        var answer = await Review(caseId, candidate.Id, """{"action": "reject"}""");
        Assert.Equal(200, answer.Status);
        Assert.Equal("rejected", answer.Object["candidate"]!["status"]!.GetValue<string>());
        Assert.Empty(await CreditorsAsync(caseId));
        var stored = (await Candidates.GetAsync(caseId, candidate.Id, HttpAssert.Ct))!;
        Assert.Equal(CandidateStatus.Rejected, stored.Status);
        Assert.Equal(Alice, stored.ConfirmedBy);
    }

    [Fact]
    public async Task A_reviewed_candidate_refuses_a_second_review()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId);
        Assert.Equal(200, (await Review(caseId, candidate.Id, """{"action": "accept"}""")).Status);
        Assert.Equal(409, (await Review(caseId, candidate.Id, """{"action": "reject"}""")).Status);
        Assert.Single(await CreditorsAsync(caseId));
    }

    [Fact]
    public async Task Two_racing_reviewers_write_one_record()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId);
        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Review(caseId, candidate.Id, """{"action": "accept"}""")));
        Assert.Single(answers, answer => answer.Status == 200);
        Assert.All(answers.Where(answer => answer.Status != 200), answer => Assert.Equal(409, answer.Status));
        Assert.Single(await CreditorsAsync(caseId));
    }

    [Fact]
    public async Task An_unknown_candidate_is_not_found()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(404, (await Review(caseId, "no-such-cand", """{"action": "accept"}""")).Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"action": "maybe"}""")]
    [InlineData("""{"action": "reject", "correctedPayload": {"name": "X"}}""")]
    [InlineData("""{"action": "accept", "correctedPayload": "not-an-object"}""")]
    public async Task A_malformed_review_is_rejected(string body)
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId);
        Assert.Equal(400, (await Review(caseId, candidate.Id, body)).Status);
    }

    [Fact]
    public async Task A_debtor_proposal_is_not_acceptable_in_app_yet()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var candidate = await PlantAsync(caseId, "debtors", """{"filing_role": "debtor_1"}""", Mcp);
        Assert.Equal(400, (await Review(caseId, candidate.Id, """{"action": "accept"}""")).Status);
        Assert.Equal(200, (await Review(caseId, candidate.Id, """{"action": "reject"}""")).Status);
    }

    // ── Candidate-id indirection ────────────────────────────────────

    [Fact]
    public async Task A_claim_waits_for_its_creditor_and_then_links_to_the_real_record()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var creditor = await PlantAsync(caseId);
        var claim = await PlantAsync(caseId, "claims", $$"""{"creditor_id": "{{creditor.Id}}", "claim_class": "nonpriority_unsecured", "amount": "310.00"}""");

        var early = await Review(caseId, claim.Id, """{"action": "accept"}""");
        Assert.Equal(400, early.Status);
        Assert.Contains("accepted yet", early.Object["fields"]!["creditor_id"]!.GetValue<string>(), StringComparison.Ordinal);

        var accepted = (await Review(caseId, creditor.Id, """{"action": "accept"}""")).Object;
        Assert.Equal(200, (await Review(caseId, claim.Id, """{"action": "accept"}""")).Status);
        var record = Assert.Single(await Entities.ListTypedAsync(caseId, ClaimRules.Kind, HttpAssert.Ct));
        Assert.Equal(accepted["record"]!["id"]!.GetValue<string>(), record.Body.CreditorId);
        Assert.Equal(ProvenanceSource.AiExtracted, record.Provenance["creditor_id"].Source);
    }
}
