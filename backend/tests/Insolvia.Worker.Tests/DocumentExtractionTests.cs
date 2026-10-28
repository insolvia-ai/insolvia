using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Candidates;
using Insolvia.Core.Cases;
using Insolvia.Core.Documents;
using Insolvia.Core.Jobs;
using Insolvia.Core.Memory;
using Insolvia.Worker.Workers;

namespace Insolvia.Worker.Tests;

/// <summary>test_extraction.py, translated: the deterministic refusals, what leaves for the model, and the candidates (never case data) each kind yields.</summary>
public sealed class DocumentExtractionTests
{
    private const string Firm = "00000000-0000-4000-8000-00000000f18a";
    private const string Alice = "00000000-0000-4000-8000-00000000a11c";

    // Synthetic stand-ins for uploaded bytes: the worker must pass EXACTLY these to the port.
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7 synthetic fixture bytes");

    private const string CreditReportRaw = """
        {"creditors": [
          {"name": "First Example Bank",
           "address": {"line1": "1 Example Way", "line2": null, "city": "Exampleville", "state": "FL", "postal_code": "33101", "raw": null},
           "page": 2, "confidence": 0.95,
           "claims": [
             {"account_last4": "4321", "amount": "1234.56", "date_incurred": "2021-06-01", "claim_class": "nonpriority_unsecured", "disputed": false, "page": 2, "confidence": 0.9},
             {"account_last4": "9999-0000-1111-2222", "amount": "$8,050.00", "date_incurred": null, "claim_class": "secured", "disputed": null, "page": 3, "confidence": 0.8}]},
          {"name": "Example Collections LLC",
           "address": {"line1": null, "line2": null, "city": null, "state": null, "postal_code": null, "raw": "PO Box 9, Exampleville FL 33101"},
           "page": 5, "confidence": 0.7,
           "claims": [
             {"account_last4": "0007", "amount": "310.00", "date_incurred": "2023-01-15", "claim_class": "nonpriority_unsecured", "disputed": true, "page": 5, "confidence": 0.75}]}]}
        """;

    private const string PayStubRaw = """
        {"employer": {"name": "Example Industries Inc", "address": {"line1": "200 Example Blvd", "line2": null, "city": "Exampleville", "state": "FL", "postal_code": "33101", "raw": null}, "page": 1, "confidence": 0.98},
         "pay_periods": [
           {"period_start": "2026-08-01", "period_end": "2026-08-14", "pay_date": "2026-08-19", "gross": "2400.00", "net": "1890.50", "frequency": "biweekly",
            "deductions": [{"category": "tax", "amount": "410.00", "description": "Fed W/H"}, {"category": "insurance", "amount": "99.50", "description": "Medical premium"}],
            "page": 1, "confidence": 0.92},
           {"period_start": "2026-08-15", "period_end": "2026-08-28", "pay_date": "2026-09-02", "gross": "2400.00", "net": "1890.50", "frequency": "biweekly",
            "deductions": [], "page": 2, "confidence": 0.9}]}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Raw(string json) => JsonNode.Parse(json)!.AsObject();

    private static async Task<(Case Case, DocumentExtractionDeps Deps, MemoryDocumentBlobStore Blobs, MemoryCandidateStore Candidates, MemoryAccessLog Log)> BuildAsync(ScriptedExtractionModel? model)
    {
        var cases = new MemoryCaseStore();
        var (created, assignment) = CaseRules.Create(CaseRules.ParseCreation(Raw("""{"chapter": 7, "court": "flmb", "division": "tampa"}"""), requireClients: false), Firm, Alice);
        await cases.CreateAsync(created, assignment, cancellationToken: Ct);
        var blobs = new MemoryDocumentBlobStore();
        var candidates = new MemoryCandidateStore();
        var log = new MemoryAccessLog();
        return (created, new DocumentExtractionDeps(cases, new MemoryDocumentStore(), blobs, candidates, log, model) { Log = new RecordingJobLog() }, blobs, candidates, log);
    }

    private static async Task<Document> StoredAsync(DocumentExtractionDeps deps, MemoryDocumentBlobStore blobs, string caseId, string kind = "credit_report", string contentType = "application/pdf", byte[]? data = null, long? byteSize = null, bool confirm = true)
    {
        data ??= PdfBytes;
        var draft = DocumentRules.ParseUpload(Raw($$"""{"kind": "{{kind}}", "fileName": "synthetic.pdf", "contentType": "{{contentType}}", "byteSize": {{byteSize ?? data.Length}}}"""));
        var document = DocumentRules.Create(draft, caseId, Alice);
        if (confirm)
        {
            blobs.AcceptUpload(document.StorageRef, data.Length, content: data);
            document = DocumentRules.Confirm(document, new StoredBlob(data.Length, new string('e', 32)));
            if (byteSize is { } size)
            {
                document = document with { ByteSize = size };
            }
        }

        await deps.Documents.CreateAsync(document, Ct);
        return document;
    }

    private static Job Accept(string caseId, string documentId) => JobRules.NewJob(JobRules.DocumentExtractionKind, caseId, Alice, documentId);

    private static async Task<string> RefusalAsync(DocumentExtractionDeps deps, Job job) =>
        (await Assert.ThrowsAsync<JobException>(() => DocumentExtraction.RunAsync(job, deps, Ct))).Category;

    // ── Deterministic refusals ──────────────────────────────────────

    [Fact]
    public async Task An_unconfigured_environment_fails_the_job_honestly()
    {
        var (created, deps, blobs, _, _) = await BuildAsync(null);
        var document = await StoredAsync(deps, blobs, created.Id);
        Assert.Equal("not_configured", await RefusalAsync(deps, Accept(created.Id, document.Id)));
    }

    [Fact]
    public async Task A_vanished_case_is_a_deterministic_failure()
    {
        var (_, deps, _, _, _) = await BuildAsync(new ScriptedExtractionModel());
        Assert.Equal("case_not_found", await RefusalAsync(deps, Accept("00000000-0000-4000-8000-000000000404", "doc-1")));
    }

    [Fact]
    public async Task A_vanished_document_is_a_deterministic_failure()
    {
        var (created, deps, _, _, _) = await BuildAsync(new ScriptedExtractionModel());
        Assert.Equal("document_not_found", await RefusalAsync(deps, Accept(created.Id, "not-a-document")));
    }

    [Fact]
    public async Task A_kind_with_no_extractor_is_refused()
    {
        var (created, deps, blobs, _, _) = await BuildAsync(new ScriptedExtractionModel());
        var document = await StoredAsync(deps, blobs, created.Id, kind: "bank_statement");
        Assert.Equal("not_extractable", await RefusalAsync(deps, Accept(created.Id, document.Id)));
    }

    [Fact]
    public async Task A_pending_upload_is_refused()
    {
        var (created, deps, blobs, _, _) = await BuildAsync(new ScriptedExtractionModel());
        var document = await StoredAsync(deps, blobs, created.Id, confirm: false);
        Assert.Equal("not_uploaded", await RefusalAsync(deps, Accept(created.Id, document.Id)));
    }

    [Theory]
    [InlineData("image/heic")]
    [InlineData("image/tiff")]
    public async Task A_format_the_model_api_cannot_take_is_refused(string contentType)
    {
        var (created, deps, blobs, _, _) = await BuildAsync(new ScriptedExtractionModel());
        var document = await StoredAsync(deps, blobs, created.Id, contentType: contentType);
        Assert.Equal("unsupported_format", await RefusalAsync(deps, Accept(created.Id, document.Id)));
    }

    [Fact]
    public async Task An_oversized_document_is_refused()
    {
        var (created, deps, blobs, _, _) = await BuildAsync(new ScriptedExtractionModel());
        var document = await StoredAsync(deps, blobs, created.Id, byteSize: DocumentExtraction.MaxDocumentBytes + 1);
        Assert.Equal("too_large", await RefusalAsync(deps, Accept(created.Id, document.Id)));
    }

    [Fact]
    public async Task Missing_bytes_are_a_deterministic_failure()
    {
        var (created, deps, blobs, _, _) = await BuildAsync(new ScriptedExtractionModel());
        var document = await StoredAsync(deps, blobs, created.Id, confirm: false);
        await deps.Documents.UpdateAsync(DocumentRules.Confirm(document, new StoredBlob(PdfBytes.Length, new string('e', 32))), Ct);
        Assert.Equal("bytes_missing", await RefusalAsync(deps, Accept(created.Id, document.Id)));
    }

    // ── What leaves for the model API ───────────────────────────────

    [Fact]
    public async Task The_model_is_shown_the_document_and_nothing_else()
    {
        var model = new ScriptedExtractionModel(Raw("""{"creditors": []}"""));
        var (created, deps, blobs, _, _) = await BuildAsync(model);
        var document = await StoredAsync(deps, blobs, created.Id);

        await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);

        var request = Assert.Single(model.Requests);
        Assert.Equal(PdfBytes, request.Data.ToArray());
        Assert.Equal("application/pdf", request.MediaType);
        Assert.Equal(DocumentExtraction.CreditReportSystemPrompt, request.SystemPrompt);
        Assert.DoesNotContain(created.Id, request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Social Security", request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_document_kind_gets_its_own_prompt_and_schema()
    {
        Assert.Equal(JobRules.ExtractableDocumentKinds.ToHashSet(), DocumentExtraction.Extractors.Keys.ToHashSet());
        var model = new ScriptedExtractionModel(Raw("""{"employer": {}, "pay_periods": []}"""));
        var (created, deps, blobs, _, _) = await BuildAsync(model);
        var document = await StoredAsync(deps, blobs, created.Id, kind: "pay_stub");
        await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);
        var request = Assert.Single(model.Requests);
        Assert.Equal(DocumentExtraction.PayStubSystemPrompt, request.SystemPrompt);
        Assert.True(JsonNode.DeepEquals(DocumentExtraction.PayStubSchema(), request.Schema));
    }

    // ── Credit report → creditor and claim candidates (8.7) ────────

    [Fact]
    public async Task A_credit_report_yields_creditor_and_claim_candidates()
    {
        var model = new ScriptedExtractionModel(Raw(CreditReportRaw), "scripted-extractor");
        var (created, deps, blobs, candidates, _) = await BuildAsync(model);
        var document = await StoredAsync(deps, blobs, created.Id);

        var result = await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);

        Assert.Equal("extracted", result["outcome"]!.GetValue<string>());
        Assert.Equal(document.Id, result["documentId"]!.GetValue<string>());
        Assert.Equal("credit_report", result["documentKind"]!.GetValue<string>());
        Assert.Equal("scripted-extractor", result["model"]!.GetValue<string>());
        Assert.Equal(0, result["skipped"]!.GetValue<int>());

        var stored = await candidates.ListForCaseAsync(created.Id, Ct);
        Assert.Equal(["creditors", "claims", "claims", "creditors", "claims"], stored.Select(c => c.EntityType));
        Assert.Equal(stored.Select(c => c.Id).ToHashSet(), result["candidates"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()).ToHashSet());

        var (bank, card, auto, collections, collectionsClaim) = (stored[0], stored[1], stored[2], stored[3], stored[4]);
        Assert.Equal("First Example Bank", bank.Payload["name"]!.GetValue<string>());
        Assert.Equal("Exampleville", bank.Payload["address"]!["city"]!.GetValue<string>());
        Assert.Equal(0.95, bank.Confidence);
        Assert.True(JsonNode.DeepEquals(Raw($$"""{"document_id": "{{document.Id}}", "page": 2}"""), bank.Locator));
        Assert.Equal(document.Id, bank.DocumentId);
        Assert.Equal(new CandidateOrigin(OriginChannel.Extraction, "scripted-extractor", Alice), bank.Origin);
        Assert.Equal(CandidateStatus.Pending, bank.Status);

        // Candidate-id indirection: each claim names its creditor CANDIDATE.
        Assert.Equal(bank.Id, card.Payload["creditor_id"]!.GetValue<string>());
        Assert.Equal(bank.Id, auto.Payload["creditor_id"]!.GetValue<string>());
        Assert.Equal(collections.Id, collectionsClaim.Payload["creditor_id"]!.GetValue<string>());
        Assert.Equal("1234.56", card.Payload["amount"]!.GetValue<string>());
        Assert.False(card.Payload["disputed"]!.GetValue<bool>());
        Assert.StartsWith("PO Box 9", collections.Payload["address"]!["raw"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Account_numbers_are_stored_last_four_only()
    {
        var (created, deps, blobs, candidates, _) = await BuildAsync(new ScriptedExtractionModel(Raw(CreditReportRaw)));
        var document = await StoredAsync(deps, blobs, created.Id);
        await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);
        var secured = (await candidates.ListForCaseAsync(created.Id, Ct)).Single(c => Json.AsString(c.Payload["claim_class"]) == "secured");
        Assert.Equal("2222", secured.Payload["account_last4"]!.GetValue<string>());
        Assert.Equal("8050.00", secured.Payload["amount"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_tax_id_shaped_value_never_lands_in_a_candidate()
    {
        var raw = Raw("""{"creditors": [{"name": "Example Bank re 123-45-6789", "address": {"line1": null, "line2": null, "city": null, "state": null, "postal_code": null, "raw": null}, "page": 1, "confidence": 0.5, "claims": []}]}""");
        var (created, deps, blobs, candidates, _) = await BuildAsync(new ScriptedExtractionModel(raw));
        var document = await StoredAsync(deps, blobs, created.Id);
        await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);
        var candidate = Assert.Single(await candidates.ListForCaseAsync(created.Id, Ct));
        Assert.DoesNotContain("123-45-6789", candidate.Payload.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("[tax id removed]", candidate.Payload["name"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_the_parser_refuses_is_skipped_and_counted()
    {
        var raw = Raw("""
            {"creditors": [{"name": "First Example Bank", "address": {"line1": "1 Example Way"}, "page": 1, "confidence": 0.9, "claims": [
              {"account_last4": "1111", "amount": "not-a-number", "date_incurred": null, "claim_class": "nonpriority_unsecured", "disputed": null, "page": 1, "confidence": 0.9},
              {"account_last4": "2222", "amount": "50.00", "date_incurred": null, "claim_class": "nonpriority_unsecured", "disputed": null, "page": 1, "confidence": 0.9}]}]}
            """);
        var (created, deps, blobs, candidates, _) = await BuildAsync(new ScriptedExtractionModel(raw));
        var document = await StoredAsync(deps, blobs, created.Id);
        var result = await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);
        Assert.Equal(1, result["skipped"]!.GetValue<int>());
        Assert.Equal(["creditors", "claims"], (await candidates.ListForCaseAsync(created.Id, Ct)).Select(c => c.EntityType));
    }

    [Fact]
    public async Task An_unusable_creditor_skips_its_claims_too()
    {
        var raw = Raw(CreditReportRaw);
        var first = raw["creditors"]![0]!.AsObject();
        first["name"] = null;
        raw["creditors"] = new JsonArray(first.DeepClone());
        var (created, deps, blobs, candidates, _) = await BuildAsync(new ScriptedExtractionModel(raw));
        var document = await StoredAsync(deps, blobs, created.Id);
        var result = await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);
        Assert.Equal(3, result["skipped"]!.GetValue<int>());
        Assert.Empty(await candidates.ListForCaseAsync(created.Id, Ct));
    }

    [Fact]
    public async Task An_empty_answer_stores_nothing_and_succeeds()
    {
        var (created, deps, blobs, _, _) = await BuildAsync(new ScriptedExtractionModel(Raw("""{"creditors": []}""")));
        var document = await StoredAsync(deps, blobs, created.Id);
        var result = await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);
        Assert.Empty(result["candidates"]!.AsArray());
        Assert.Equal(0, result["skipped"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_runaway_generation_is_capped()
    {
        var creditors = new JsonArray([.. Enumerable.Range(0, DocumentExtraction.MaxCandidatesPerRun + 25).Select(n => (JsonNode)Raw($$"""{"name": "Creditor {{n}}", "address": {}, "page": null, "confidence": 0.5, "claims": []}"""))]);
        var (created, deps, blobs, _, _) = await BuildAsync(new ScriptedExtractionModel(new JsonObject { ["creditors"] = creditors }));
        var document = await StoredAsync(deps, blobs, created.Id);
        var result = await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);
        Assert.Equal(DocumentExtraction.MaxCandidatesPerRun, result["candidates"]!.AsArray().Count);
        Assert.Equal(25, result["skipped"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_run_is_access_logged_against_the_preparer()
    {
        var (created, deps, blobs, _, log) = await BuildAsync(new ScriptedExtractionModel(Raw("""{"creditors": []}""")));
        var document = await StoredAsync(deps, blobs, created.Id);
        await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);
        var recorded = Assert.Single(log.Events);
        Assert.Equal(created.Id, recorded.CaseId);
        Assert.Equal(Alice, recorded.Principal);
        Assert.Equal(AccessAction.DocumentExtract, recorded.Action);
    }

    // ── Pay stubs → employment and pay-period candidates (8.8) ─────

    [Fact]
    public async Task Pay_stubs_yield_employment_and_dated_pay_period_candidates()
    {
        var (created, deps, blobs, candidates, _) = await BuildAsync(new ScriptedExtractionModel(Raw(PayStubRaw), "scripted-extractor"));
        var document = await StoredAsync(deps, blobs, created.Id, kind: "pay_stub");

        var result = await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);

        Assert.Equal("pay_stub", result["documentKind"]!.GetValue<string>());
        Assert.Equal(0, result["skipped"]!.GetValue<int>());
        var stored = await candidates.ListForCaseAsync(created.Id, Ct);
        Assert.Equal(3, stored.Count);
        var (employment, first, second) = (stored[0], stored[1], stored[2]);
        Assert.Equal("employments", employment.EntityType);
        Assert.Equal("Example Industries Inc", employment.Payload["employer_name"]!.GetValue<string>());
        Assert.Equal("employed", employment.Payload["status"]!.GetValue<string>());
        Assert.Equal("pay_period_records", first.EntityType);
        Assert.Equal(employment.Id, first.Payload["employment_id"]!.GetValue<string>());
        Assert.Equal(employment.Id, second.Payload["employment_id"]!.GetValue<string>());
        Assert.Equal(("2026-08-01", "2026-08-14"), (first.Payload["period_start"]!.GetValue<string>(), first.Payload["period_end"]!.GetValue<string>()));
        Assert.Equal("2026-08-19", first.Payload["pay_date"]!.GetValue<string>());
        Assert.Equal("2026-09-02", second.Payload["pay_date"]!.GetValue<string>());
        Assert.Equal("2400.00", first.Payload["gross"]!.GetValue<string>());
        Assert.Equal("1890.50", first.Payload["net"]!.GetValue<string>());
        Assert.Equal("biweekly", first.Payload["frequency"]!.GetValue<string>());
        var deductions = first.Payload["deductions"]!.AsArray();
        Assert.Equal(["tax", "insurance"], deductions.Select(d => d!["category"]!.GetValue<string>()));
        Assert.Equal(["d1", "d2"], deductions.Select(d => d!["id"]!.GetValue<string>()));
        Assert.Equal("Medical premium", deductions[1]!["description"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(Raw($$"""{"document_id": "{{document.Id}}", "page": 1}"""), first.Locator));
    }

    [Fact]
    public async Task A_stub_without_a_readable_employer_skips_everything()
    {
        var raw = Raw(PayStubRaw);
        raw["employer"] = Raw("""{"name": null, "address": {}, "page": null, "confidence": 0.1}""");
        var (created, deps, blobs, candidates, _) = await BuildAsync(new ScriptedExtractionModel(raw));
        var document = await StoredAsync(deps, blobs, created.Id, kind: "pay_stub");
        var result = await DocumentExtraction.RunAsync(Accept(created.Id, document.Id), deps, Ct);
        Assert.Equal(3, result["skipped"]!.GetValue<int>());
        Assert.Empty(await candidates.ListForCaseAsync(created.Id, Ct));
    }

    // ── The coercion layer, directly ────────────────────────────────

    [Fact]
    public void Credit_report_coercion_survives_junk()
    {
        var outcome = DocumentExtraction.ParseCreditReportOutput(Raw("""{"creditors": ["nonsense", 7, null]}"""));
        Assert.Empty(outcome.Specs);
        Assert.Equal(0, outcome.Skipped);
    }

    [Fact]
    public void Pay_stub_coercion_survives_a_missing_shape()
    {
        var outcome = DocumentExtraction.ParsePayStubOutput([]);
        Assert.Empty(outcome.Specs);
        Assert.Equal(1, outcome.Skipped);
    }
}
