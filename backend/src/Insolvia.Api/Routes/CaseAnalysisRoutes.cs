using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Api.Analysis;
using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Domain;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.Assets;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Claims;
using Insolvia.Domain.Creditors;
using Insolvia.Domain.Engines.CreditorMatrix;
using Insolvia.Domain.Engines.Exemptions;
using Insolvia.Domain.Engines.Liens;
using Insolvia.Domain.Engines.MeansTest;
using Insolvia.Domain.Engines.Plans;
using Insolvia.Domain.Engines.Projections;
using Insolvia.Domain.Engines.Standards;
using Insolvia.Domain.Expenses;
using Insolvia.Domain.Fields;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Plans;
using Insolvia.Domain.Ports;

namespace Insolvia.Api.Routes;

/// <summary>
/// The computed case analyses (18.15 part 2): <c>means-test</c>,
/// <c>exemption-analysis</c>, <c>creditor-matrix</c>, <c>liens</c>,
/// <c>standards</c> and <c>plan-calculation</c> — routes/means_test.py,
/// exemption_analysis.py, creditor_matrix.py, liens.py, standards.py and
/// plan.py. Each is authorisation, one read of the collections its engine
/// needs, and the engine's own wire shape; none writes. A literal segment
/// beside <c>/v1/cases/{caseId}/{collection}</c> wins by ASP.NET's route
/// precedence, as the static rule did under Werkzeug's ranking. Every one
/// answers 200 on a reachable case — what is missing is a <c>problem</c>
/// beside whatever did compute — and the undistinguishing 404 otherwise,
/// the read logged either way.
/// </summary>
/// <remarks>
/// Not here: <c>GET …/summary</c> (case_summary.py). It runs the packet's
/// completeness gate, which is packet assembly — 18.16 (forms).
/// </remarks>
public sealed class CaseAnalysisRoutes : IEndpointModule
{
    // plan.py: a plan is a nested document; enough to compare a handful of
    // alternatives side by side, each running the whole waterfall.
    private const int MaxPlanRequestBytes = 256 * 1024;
    private const int MaxScenarios = 5;
    private const string MatrixLogger = "insolvia_api.api.routes.creditor_matrix";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/means-test", MeansTestAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/exemption-analysis", ExemptionAnalysisAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/creditor-matrix", CreditorMatrixAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/liens", LiensAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/standards", StandardsAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/plan-calculation", PlanCalculationAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/cases/{caseId}/plan-calculation", PlanScenariosAsync).RequestBody<PlanScenariosRequest>().RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
    }

    /// <summary>The § 707(b) means test as of the expected filing date.</summary>
    /// <remarks>
    /// Over the whole case file — the same read path the packet takes, so the
    /// endpoint and B122 read the same records.
    /// </remarks>
    internal static async Task<MeansTestView> MeansTestAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var stored = await CaseFileReader.ReadAsync(target, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        return MeansTestView.Of(MeansTestTraces.Trace(stored.CaseFile));
    }

    /// <summary>The Schedule C exemption workbench.</summary>
    /// <remarks>Only the collections the arithmetic reads, resolved as of the petition's date or today.</remarks>
    internal static async Task<ExemptionAnalysisView> ExemptionAnalysisAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var inputs = await CaseFileReader.ReadExemptionCaseAsync(target, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        return ExemptionAnalysisView.Of(ExemptionAnalyzer.Analyse(inputs, context.Today()));
    }

    /// <summary>The court's creditor mailing list, or every reason it cannot be produced yet.</summary>
    /// <remarks>200 either way: the problem report is the resource the intake screen renders, not an error.</remarks>
    internal static async Task<CreditorMatrixView> CreditorMatrixAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var creditors = await context.Port<ICaseEntityStore>().ListTypedAsync(caseId, CreditorRules.Kind, cancellationToken);
        var matrix = CreditorMatrices.Generate([.. creditors.Select(entity => (entity.Id, entity.Body))], CreditorMatrices.FormatForCourt(target.Court));
        // GLBA: the case id and whether a file was produced — never a
        // creditor name, a count, or a problem message.
        context.Log(MatrixLogger, "creditor matrix generated", ("case_id", caseId), ("generated", matrix.Content is not null));
        return CreditorMatrixView.Of(matrix);
    }

    /// <summary>The derived collateral figures of the case's secured claims.</summary>
    internal static async Task<LienView> LiensAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var entities = context.Port<ICaseEntityStore>();
        var caseFile = new CaseFile(target)
        {
            Assets = [.. (await entities.ListTypedAsync(caseId, AssetRules.Kind, cancellationToken)).Select(entity => (entity.Id, entity.Body))],
            Claims = [.. (await entities.ListTypedAsync(caseId, ClaimRules.Kind, cancellationToken)).Select(entity => (entity.Id, entity.Body))],
        };
        return LienView.Of(LienEngine.Derive(caseFile));
    }

    /// <summary>The IRS National and Local Standards for the case's jurisdiction and household.</summary>
    /// <remarks>
    /// As of the case's creation date (the filing-date stand-in). A date no
    /// release covers at all is a 500.
    /// </remarks>
    internal static async Task<StandardsView> StandardsAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var debtors = await context.Port<IDebtorStore>().ListForCaseAsync(target.Id, cancellationToken);
        var dependents = await context.Port<ICaseEntityStore>().ListTypedAsync(target.Id, ExpenseRules.Dependent, cancellationToken);
        var asOf = DateOnly.FromDateTime(target.CreatedAt.UtcDateTime);
        // An EngineLookupException — no release covers the date — is
        // deliberately not caught: the generic 500 is the honest answer.
        return StandardsView.Of(IrsStandards.Resolve(asOf, debtors, [.. dependents.Select(entity => entity.Body)]));
    }

    /// <summary>The stored Chapter 13 plan's figures.</summary>
    /// <remarks>A case with no plan still gets its liquidation floor; with several, the first created is used, and a warning says so.</remarks>
    internal static async Task<PlanCalculationView> PlanCalculationAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var (inputs, plans) = await PlanInputsAsync(context, target, cancellationToken);
        var calculation = PlanCalculationView.Of(Chapter13Plan.Calculate(inputs, plans.Count > 0 ? plans[0] : null));
        // The packet gate owns the cardinality; the calculator says which
        // record it read rather than refusing.
        return plans.Count > 1
            ? calculation with { Warnings = [$"This case has {plans.Count} plan records; the first created is used.", .. calculation.Warnings] }
            : calculation;
    }

    /// <summary>Calculate unsaved plan alternatives against the case; nothing is written.</summary>
    /// <remarks>At most five scenarios; each malformed one is named by its path (<c>scenarios[0].plan.term_months</c>).</remarks>
    internal static async Task<PlanScenarioList> PlanScenariosAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        // Reachability first, so a 400 is never an oracle for a case id the
        // caller cannot see; the body before the records, so a malformed
        // request costs no collection reads.
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var scenarios = Scenarios(await context.BodyAsync(MaxPlanRequestBytes));
        var (inputs, _) = await PlanInputsAsync(context, target, cancellationToken);
        return new PlanScenarioList([.. scenarios.Select(scenario => new PlanScenario(scenario.Label, PlanCalculationView.Of(Chapter13Plan.Calculate(inputs, scenario.Plan))))]);
    }

    // plan.py's `_inputs`: the whole file read once, the stored plans beside it.
    private static async Task<(PlanInputs Inputs, IReadOnlyList<PlanBody> Plans)> PlanInputsAsync(HttpContext context, Case target, CancellationToken cancellationToken)
    {
        var stored = await CaseFileReader.ReadAsync(target, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        var inputs = Chapter13Plan.BuildPlanInputs(stored.CaseFile, stored.ExemptionCase, context.Today());
        return (inputs, stored.CaseFile.Plans);
    }

    // plan.py's `_scenarios`: every malformed scenario named by its path.
    private static List<(string? Label, PlanBody Plan)> Scenarios(JsonObject payload)
    {
        if (payload["scenarios"] is not JsonArray { Count: > 0 } raw)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["scenarios"] = "Must be a non-empty list." });
        }

        if (raw.Count > MaxScenarios)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["scenarios"] = $"At most {MaxScenarios} scenarios at a time." });
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsed = new List<(string? Label, PlanBody Plan)>();
        for (var index = 0; index < raw.Count; index++)
        {
            var path = $"scenarios[{index}]";
            if (raw[index] is not JsonObject scenario)
            {
                errors[path] = "Must be an object.";
                continue;
            }

            var label = FieldParsers.Text(scenario["label"], $"{path}.label", errors);
            if (scenario["plan"] is not JsonObject plan)
            {
                errors[$"{path}.plan"] = "Must be an object.";
                continue;
            }

            try
            {
                parsed.Add((label, PlanRules.Kind.ParseBody((JsonObject)plan.DeepClone())));
            }
            catch (FieldValidationException error)
            {
                foreach (var (field, message) in error.Fields)
                {
                    errors[$"{path}.plan.{field}"] = message;
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return parsed;
    }
}

/// <summary>The unsaved plan alternatives, each calculated against the same case, in request order.</summary>
/// <param name="Scenarios">The calculations.</param>
public sealed record PlanScenarioList(IReadOnlyList<PlanScenario> Scenarios);

/// <summary>One unsaved plan alternative's figures.</summary>
/// <param name="Label">The label sent with it; null when none was.</param>
/// <param name="Calculation">The figures.</param>
public sealed record PlanScenario(string? Label, PlanCalculationView Calculation);

/// <summary>Unsaved plan alternatives to calculate (<c>POST …/plan-calculation</c>): one to five.</summary>
/// <param name="Scenarios">The alternatives.</param>
public sealed record PlanScenariosRequest(IReadOnlyList<PlanScenarioRequest> Scenarios);

/// <summary>One unsaved plan alternative.</summary>
/// <param name="Label">A label to tell it apart by.</param>
/// <param name="Plan">The plan, as a plan record's body is written.</param>
public sealed record PlanScenarioRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label,
    PlanBody Plan);
