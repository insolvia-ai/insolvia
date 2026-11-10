using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Api.Analysis;
using Insolvia.Api.Cases;
using Insolvia.Api.Tests.Support;
using Insolvia.Domain.Engines.CreditorMatrix;
using Insolvia.Domain.Engines.Exemptions;
using Insolvia.Domain.Engines.Liens;
using Insolvia.Domain.Engines.MeansTest;
using Insolvia.Domain.Engines.Plans;
using Insolvia.Domain.Engines.Projections;
using Insolvia.Domain.Forms;
using Insolvia.Domain.Forms.Tests.Support;
using Insolvia.Domain.Jobs;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// The API's typed views of an engine's output against the engine's own
/// <c>Json(...)</c>, which stays because the Python-oracle parity suite, the
/// worker or the stores still read it: the same JSON, member for member, on
/// the four parity case files and on the same case with no records at all.
/// </summary>
public sealed class EngineViewParityTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var name in AnalysisWorld.ParityCaseFiles)
        {
            data.Add(name, false);
            data.Add(name, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_means_test_view_is_the_engines_json(string name, bool empty)
    {
        var trace = MeansTestTraces.Trace(CaseFile(name, empty));
        AssertSame(MeansTestTraces.Json(trace), MeansTestView.Of(trace));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_exemption_analysis_view_is_the_engines_json(string name, bool empty)
    {
        var analysis = ExemptionAnalyzer.Analyse(Chapter13Plan.ExemptionCaseOf(CaseFile(name, empty)), Today);
        AssertSame(ExemptionAnalyzer.Json(analysis), ExemptionAnalysisView.Of(analysis));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_creditor_matrix_view_is_the_engines_json(string name, bool empty)
    {
        var caseFile = CaseFile(name, empty);
        var matrix = CreditorMatrices.Generate(caseFile.Creditors, CreditorMatrices.FormatForCourt(caseFile.Case.Court));
        AssertSame(CreditorMatrices.Json(matrix), CreditorMatrixView.Of(matrix));
    }

    [Fact]
    public void A_creditor_matrix_problem_view_is_the_engines_json()
    {
        var matrix = new GeneratedMatrix(null, 2, 0, [new MatrixProblem("cred-1", "address.line1", "Missing."), new MatrixProblem(null, "creditors", "None.")]);
        AssertSame(CreditorMatrices.Json(matrix), CreditorMatrixView.Of(matrix));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_lien_view_is_the_engines_json(string name, bool empty)
    {
        var figures = LienEngine.Derive(CaseFile(name, empty));
        AssertSame(LienEngine.Json(figures), LienView.Of(figures));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_plan_calculation_view_is_the_engines_json(string name, bool empty)
    {
        var caseFile = CaseFile(name, empty);
        var calculation = Chapter13Plan.Calculate(Chapter13Plan.BuildPlanInputs(caseFile, Chapter13Plan.ExemptionCaseOf(caseFile), Today), caseFile.Plans.Count > 0 ? caseFile.Plans[0] : null);
        AssertSame(Chapter13Plan.Json(calculation), PlanCalculationView.Of(calculation));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_gate_problem_view_is_packet_assemblys_json(string name, bool empty)
    {
        var data = empty ? new CaseData(CaseDatas.Named(name).Case) : CaseDatas.Named(name);
        var problems = FormsHub.Summaries(data, Today).SelectMany(summary => summary.Problems).ToList();
        Assert.True(!empty || problems.Count > 0);
        foreach (var problem in problems.Append(new PacketProblem("form/b101", "item-1", "name", "Missing.")))
        {
            AssertSame(PacketAssembly.ProblemJson(problem), PacketProblemView.Of(problem));
        }
    }

    [Fact]
    public void An_output_options_view_is_the_recorded_shape()
    {
        foreach (var options in new[] { FormOverlay.DefaultOutputOptions, new OutputOptions { DraftWatermark = true, PrintDate = true, SignaturePages = SignaturePagesMode.Omit, SignElectronically = true, AmendedOnly = true, Forms = ["b101", "b106ab"] } })
        {
            AssertSame(FormOverlay.OutputOptionsJson(options), OutputOptionsView.Of(options));
        }
    }

    [Fact]
    public void A_packet_view_is_the_recorded_shape()
    {
        var options = new OutputOptions { SignaturePages = SignaturePagesMode.Only, Forms = ["b101"] };
        var packet = Packets.NewPacket(Guid.NewGuid().ToString(), 7, "job-1", 1024, "abc", new Dictionary<string, string> { ["form/b101"] = "2024-12" }, "constants-1", 3, "sub", options, [new PacketPart("01-b101.pdf", 10, 2)]);
        AssertSame(Packets.PacketJson(packet), PacketView.Of(packet));
    }

    [Fact]
    public void A_job_view_is_the_stored_jobs_json()
    {
        var at = new DateTimeOffset(2026, 3, 2, 15, 4, 5, 123, TimeSpan.Zero);
        var bare = new Job("job-1", "case-1", JobRules.EchoKind, JobStatus.Queued, "sub", at, at);
        var full = bare with
        {
            Kind = JobRules.PacketAssemblyKind,
            Status = JobStatus.Failed,
            DocumentId = "doc-1",
            Options = FormOverlay.OutputOptionsJson(new OutputOptions { DraftWatermark = true, Forms = ["b101"] }),
            Attempts = 2,
            Failure = new JobFailure("render_failed", "It broke."),
            Result = new JsonObject { ["packetId"] = "p-1", ["parts"] = new JsonArray(1, 2) },
        };
        foreach (var job in new[] { bare, full })
        {
            AssertSame(JobRules.ToJson(job), JobView.Of(job));
        }
    }

    private static CaseFile CaseFile(string name, bool empty)
    {
        var caseFile = CaseDatas.Named(name).ToCaseFile();
        return empty ? new CaseFile(caseFile.Case) : caseFile;
    }

    private static void AssertSame<TView>(JsonNode engine, TView view)
    {
        var typed = JsonSerializer.SerializeToNode(view, WireJson.Options);
        Assert.True(JsonNode.DeepEquals(engine, typed), $"engine {engine.ToJsonString()}\nview   {typed?.ToJsonString()}");
    }
}
