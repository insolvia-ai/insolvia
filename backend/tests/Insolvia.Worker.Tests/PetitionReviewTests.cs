using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Jobs;
using Insolvia.Core.Memory;
using Insolvia.Forms;
using Insolvia.Forms.Tests.Support;
using Insolvia.Worker.Tests.Support;
using Insolvia.Worker.Workers;

namespace Insolvia.Worker.Tests;

/// <summary>test_petition_review.py, translated: the worker, what leaves for the model API (and what never does), and the findings boundary.</summary>
public sealed partial class PetitionReviewTests
{
    private const string FindingJson = """{"severity": "high", "category": "consistency", "form": "form/b106i", "line": "4_combined_monthly_income", "message": "Schedule I income disagrees with the SOFA income answers."}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject AFinding => JsonNode.Parse(FindingJson)!.AsObject();

    private static PetitionReviewDeps Deps(PipelineWorld world, ScriptedReviewModel? model) =>
        new(world.Cases, world.Debtors, world.Entities, world.Packets, world.AccessLog, world.TaxIds, world.Cipher, model) { Clock = world.Clock, Log = world.Log };

    private static Job Review(string caseId) => JobRules.NewJob(JobRules.PetitionReviewKind, caseId, "subject-1");

    private static Task<JsonObject> AssembleAsync(PipelineWorld world) =>
        PacketAssemblyWorker.RunAsync(JobRules.NewJob(JobRules.PacketAssemblyKind, world.CaseId, "subject-1"), world.PacketDeps, Ct);

    // ── The worker ──────────────────────────────────────────────────

    [Fact]
    public async Task An_unconfigured_environment_fails_the_job_honestly()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));
        Assert.Equal("not_configured", (await Assert.ThrowsAsync<JobException>(() => PetitionReview.RunAsync(Review(world.CaseId), Deps(world, null), Ct))).Category);
    }

    [Fact]
    public async Task A_vanished_case_is_a_deterministic_failure()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));
        Assert.Equal("case_not_found", (await Assert.ThrowsAsync<JobException>(() => PetitionReview.RunAsync(Review("00000000-0000-4000-8000-000000000404"), Deps(world, new ScriptedReviewModel()), Ct))).Category);
    }

    [Fact]
    public async Task A_case_the_gate_refuses_reports_the_same_problems_as_assembly()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7") with { Petitions = [] });
        var result = await PetitionReview.RunAsync(Review(world.CaseId), Deps(world, new ScriptedReviewModel()), Ct);
        Assert.Equal("blocked", result["outcome"]!.GetValue<string>());
        Assert.Contains(result["problems"]!.AsArray(), problem => problem!["source"]!.GetValue<string>() == "petitions");
    }

    [Fact]
    public async Task Review_before_any_assembly_is_blocked_not_run()
    {
        var model = new ScriptedReviewModel();
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));
        var result = await PetitionReview.RunAsync(Review(world.CaseId), Deps(world, model), Ct);
        Assert.Equal("blocked", result["outcome"]!.GetValue<string>());
        Assert.Contains("assemble", result["problems"]![0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(model.Documents);
    }

    [Fact]
    public async Task A_case_edited_after_assembly_is_blocked_until_reassembled()
    {
        var data = PipelineWorld.Reference("reference-ch7");
        var world = await PipelineWorld.SeedAsync(data);
        await AssembleAsync(world);
        var first = data.Creditors[0];
        await world.Entities.CreateAsync(first with { Id = "creditor-late", CreatedAt = first.CreatedAt.AddDays(30), Body = first.Body with { Name = "Wholly New Creditor LLC" } }, Ct);
        var model = new ScriptedReviewModel();

        var result = await PetitionReview.RunAsync(Review(world.CaseId), Deps(world, model), Ct);

        Assert.Equal("blocked", result["outcome"]!.GetValue<string>());
        Assert.Contains("changed", result["problems"]![0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(model.Documents);
    }

    [Fact]
    public async Task A_reviewed_packet_reports_findings_against_the_stored_packet()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));
        var assembled = await AssembleAsync(world);
        var model = new ScriptedReviewModel([AFinding], "claude-test");

        var result = await PetitionReview.RunAsync(Review(world.CaseId), Deps(world, model), Ct);

        Assert.Equal("reviewed", result["outcome"]!.GetValue<string>());
        var report = result["report"]!;
        Assert.Equal(assembled["packet"]!["id"]!.GetValue<string>(), report["packetId"]!.GetValue<string>());
        Assert.Equal(assembled["packet"]!["sha256"]!.GetValue<string>(), report["packetSha256"]!.GetValue<string>());
        Assert.Equal("claude-test", report["model"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(new JsonArray(AFinding), report["findings"]));
        Assert.Contains(world.AccessLog.Events, e => e.Action == AccessAction.PetitionReview && e.Principal == "subject-1");
        Assert.Equal(
            [("debtor_1", "petition_review"), ("debtor_2", "petition_review")],
            world.AccessLog.Events.Where(e => e.Action == AccessAction.TaxIdRead && e.Purpose == "petition_review").Select(e => (e.FilingRole, e.Purpose)));

        // The number stopped there: B121 is dropped wholesale before the scrub.
        var document = Assert.Single(model.Documents);
        Assert.DoesNotContain("\"form/b121\"", document, StringComparison.Ordinal);
        Assert.DoesNotContain("987-65-4321", document, StringComparison.Ordinal);
        Assert.DoesNotContain("87-65-4322", document, StringComparison.Ordinal);

        // GLBA: the log line carries counts and ids, never a finding's words.
        Assert.DoesNotContain(world.Log.Records, record => record.Fields.Values.Any(value => value?.ToString()?.Contains("SOFA", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task A_clean_review_carries_an_empty_findings_list()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));
        await AssembleAsync(world);
        var result = await PetitionReview.RunAsync(Review(world.CaseId), Deps(world, new ScriptedReviewModel()), Ct);
        Assert.Equal("reviewed", result["outcome"]!.GetValue<string>());
        Assert.Empty(result["report"]!["findings"]!.AsArray());
    }

    // ── What leaves for the model API ───────────────────────────────

    private static string Document()
    {
        var data = CaseDatas.Named("reference-ch7");
        var (packet, problems) = PacketAssembly.Assemble(data, PipelineWorld.Today, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.Empty(problems);
        return PetitionReview.ReviewDocument(data, packet!);
    }

    [Fact]
    public void The_document_shows_the_projected_forms_and_the_matrix()
    {
        var document = Document();
        Assert.Contains("\"form/b106i\"", document, StringComparison.Ordinal);
        Assert.Contains("4_combined_monthly_income", document, StringComparison.Ordinal);
        Assert.Contains("\"creditor_matrix\"", document, StringComparison.Ordinal);
        Assert.Contains("\"confirmed_records\"", document, StringComparison.Ordinal);
    }

    [Fact]
    public void The_document_is_deterministic_sorted_and_ascii() =>
        Assert.Equal(Document(), Document());

    [Fact]
    public void The_document_is_pythons_json_dumps_shape()
    {
        var document = Document();
        Assert.All(document, ch => Assert.InRange(ch, (char)0x20, (char)0x7e));
        var parsed = JsonNode.Parse(document)!.AsObject();
        Assert.Equal(["case", "confirmed_records", "creditor_matrix", "forms"], parsed.Select(pair => pair.Key));
        Assert.StartsWith("{\"case\": {\"chapter\": 7, \"district\": ", document, StringComparison.Ordinal);
    }

    [Fact]
    public void The_document_never_carries_a_tax_id_shape()
    {
        var document = Document();
        Assert.DoesNotContain("\"form/b121\"", document, StringComparison.Ordinal);
        Assert.DoesNotMatch(TaxIdShape(), document);
        Assert.DoesNotContain("87-65-4322", document, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tax_id\"", document, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"tax_id": "123-45-6789", "name": "A"}""", """{"name":"A"}""")]
    [InlineData("""{"ssn": "x", "itin": "y"}""", "{}")]
    [InlineData("""{"note": "SSN 123-45-6789 on file"}""", """{"note":"SSN [tax id removed] on file"}""")]
    [InlineData("""{"nested": [{"social_security_number": "z", "keep": 1}]}""", """{"nested":[{"keep":1}]}""")]
    [InlineData("""{"amount": "1234.56"}""", """{"amount":"1234.56"}""")]
    public void Scrub_removes_tax_identifiers_and_nothing_else(string value, string expected) =>
        Assert.Equal(expected, PetitionReview.Scrub(JsonNode.Parse(value))!.ToJsonString());

    // ── The findings boundary ───────────────────────────────────────

    [Fact]
    public void Parse_findings_validates_and_orders()
    {
        var findings = PetitionReview.ParseFindings(new JsonObject { ["findings"] = new JsonArray(AFinding) });
        Assert.Equal([new ReviewFinding("high", "consistency", "form/b106i", "4_combined_monthly_income", "Schedule I income disagrees with the SOFA income answers.")], findings);
        Assert.True(JsonNode.DeepEquals(AFinding, PetitionReview.FindingJson(findings[0])));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"findings": "not a list"}""")]
    [InlineData("""{"findings": ["not an object"]}""")]
    [InlineData("""{"findings": [{"severity": "catastrophic", "category": "consistency", "form": "f", "line": "", "message": "m"}]}""")]
    [InlineData("""{"findings": [{"severity": "high", "category": "vibes", "form": "f", "line": "", "message": "m"}]}""")]
    [InlineData("""{"findings": [{"severity": "high", "category": "consistency", "form": "f", "line": "", "message": "  "}]}""")]
    public void Parse_findings_refuses_malformed_output(string raw) =>
        Assert.Contains("review model", Assert.Throws<FormatException>(() => PetitionReview.ParseFindings(JsonNode.Parse(raw)!.AsObject())).Message, StringComparison.Ordinal);

    [Fact]
    public void Parse_findings_caps_the_list_and_the_message()
    {
        var finding = AFinding;
        finding["message"] = new string('x', 2000);
        var findings = PetitionReview.ParseFindings(new JsonObject { ["findings"] = new JsonArray([.. Enumerable.Range(0, 80).Select(_ => (JsonNode)finding.DeepClone())]) });
        Assert.Equal(50, findings.Count);
        Assert.Equal(500, findings[0].Message.Length);
    }

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex TaxIdShape();
}
