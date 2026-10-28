using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Assets;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Core.Creditors;
using Insolvia.Core.Expenses;
using Insolvia.Core.Fields;
using Insolvia.Core.Firms;
using Insolvia.Core.Plans;
using Insolvia.Core.Ports;
using Insolvia.Engines.CreditorMatrix;
using Insolvia.Engines.Exemptions;
using Insolvia.Engines.Liens;
using Insolvia.Engines.MeansTest;
using Insolvia.Engines.Plans;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Standards;

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
        routes.MapPost("/v1/cases/{caseId}/plan-calculation", PlanScenariosAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
    }

    /// <summary>
    /// The § 707(b) trace as of the expected filing date, over the whole case
    /// file — the same read path the packet takes, so the endpoint and B122
    /// read the same records.
    /// </summary>
    private static async Task<IResult> MeansTestAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var stored = await CaseFileReader.ReadAsync(target, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        return FlaskJson.Result(MeansTestTraces.Json(MeansTestTraces.Trace(stored.CaseFile)));
    }

    /// <summary>The Schedule C workbench: only the collections the arithmetic reads, resolved as of the petition's date or today.</summary>
    private static async Task<IResult> ExemptionAnalysisAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var inputs = await CaseFileReader.ReadExemptionCaseAsync(target, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        return FlaskJson.Result(ExemptionAnalyzer.Json(ExemptionAnalyzer.Analyse(inputs, context.Today())));
    }

    /// <summary>
    /// The court's mailing list in the court's own format, or every reason it
    /// cannot be produced yet — 200 either way: the problem report is the
    /// resource the intake screen renders, not an error.
    /// </summary>
    private static async Task<IResult> CreditorMatrixAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var creditors = await context.Port<ICaseEntityStore>().ListTypedAsync(caseId, CreditorRules.Kind, cancellationToken);
        var matrix = CreditorMatrices.Generate([.. creditors.Select(entity => (entity.Id, entity.Body))], CreditorMatrices.FormatForCourt(target.Court));
        // GLBA: the case id and whether a file was produced — never a
        // creditor name, a count, or a problem message.
        context.Log(MatrixLogger, "creditor matrix generated", ("case_id", caseId), ("generated", matrix.Content is not null));
        return FlaskJson.Result(CreditorMatrices.Json(matrix));
    }

    /// <summary>The derived collateral figures, from the two collections the arithmetic reads.</summary>
    private static async Task<IResult> LiensAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var entities = context.Port<ICaseEntityStore>();
        var caseFile = new CaseFile(target)
        {
            Assets = [.. (await entities.ListTypedAsync(caseId, AssetRules.Kind, cancellationToken)).Select(entity => (entity.Id, entity.Body))],
            Claims = [.. (await entities.ListTypedAsync(caseId, ClaimRules.Kind, cancellationToken)).Select(entity => (entity.Id, entity.Body))],
        };
        return FlaskJson.Result(LienEngine.Json(LienEngine.Derive(caseFile)));
    }

    /// <summary>
    /// The IRS National and Local Standards for the case's jurisdiction and
    /// household, as of its creation date (the filing-date stand-in). An
    /// <see cref="Engines.EngineLookupException"/> — no release covers the date
    /// at all — is deliberately not caught: the generic 500 is the honest answer.
    /// </summary>
    private static async Task<IResult> StandardsAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var debtors = await context.Port<IDebtorStore>().ListForCaseAsync(target.Id, cancellationToken);
        var dependents = await context.Port<ICaseEntityStore>().ListTypedAsync(target.Id, ExpenseRules.Dependent, cancellationToken);
        var asOf = DateOnly.FromDateTime(target.CreatedAt.UtcDateTime);
        return FlaskJson.Result(StandardsJson(IrsStandards.Resolve(asOf, debtors, [.. dependents.Select(entity => entity.Body)])));
    }

    /// <summary>The stored plan's figures; a case with no plan still gets its liquidation floor.</summary>
    private static async Task<IResult> PlanCalculationAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var (inputs, plans) = await PlanInputsAsync(context, target, cancellationToken);
        var calculation = Chapter13Plan.Json(Chapter13Plan.Calculate(inputs, plans.Count > 0 ? plans[0] : null));
        if (plans.Count > 1)
        {
            // The packet gate owns the cardinality; the calculator says which
            // record it read rather than refusing.
            calculation["warnings"]!.AsArray().Insert(0, $"This case has {plans.Count} plan records; the first created is used.");
        }

        return FlaskJson.Result(calculation);
    }

    /// <summary>
    /// Unsaved alternatives, each calculated against the same case; nothing
    /// is written. Reachability first, so a 400 is never an oracle for a case
    /// id the caller cannot see; the body before the records, so a malformed
    /// request costs no collection reads.
    /// </summary>
    private static async Task<IResult> PlanScenariosAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var scenarios = Scenarios(await context.BodyAsync(MaxPlanRequestBytes));
        var (inputs, _) = await PlanInputsAsync(context, target, cancellationToken);
        return FlaskJson.Result(new JsonObject
        {
            ["scenarios"] = new JsonArray([.. scenarios.Select(scenario => (JsonNode)new JsonObject
            {
                ["label"] = FlaskJson.Text(scenario.Label),
                ["calculation"] = Chapter13Plan.Json(Chapter13Plan.Calculate(inputs, scenario.Plan)),
            })]),
        });
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

    /// <summary>
    /// standards.py's <c>_standards_json</c>: money and jurisdiction facts as
    /// strings, null where a figure could not resolve — NOT pruned, because the
    /// client needs to know which figure is missing to label its box.
    /// </summary>
    internal static JsonObject StandardsJson(StandardsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new JsonObject
        {
            ["asOf"] = FlaskJson.Date(result.AsOf),
            ["state"] = FlaskJson.Text(result.State),
            ["county"] = FlaskJson.Text(result.County),
            ["householdSize"] = result.HouseholdSize is { } size ? JsonValue.Create(size) : null,
            ["jurisdictionSource"] = FlaskJson.Text(result.JurisdictionSource),
            ["nationalStandards"] = new JsonObject
            {
                ["releaseId"] = FlaskJson.Text(result.NationalReleaseId),
                ["allowance"] = FlaskJson.Text(result.NationalAllowance),
                ["oopHealthcareUnder65"] = FlaskJson.Text(result.OopHealthcareUnder65),
                ["oopHealthcare65AndOlder"] = FlaskJson.Text(result.OopHealthcare65AndOlder),
            },
            ["localStandards"] = new JsonObject
            {
                ["releaseId"] = FlaskJson.Text(result.LocalReleaseId),
                ["housingNonMortgage"] = FlaskJson.Text(result.HousingNonMortgage),
                ["housingMortgageRent"] = FlaskJson.Text(result.HousingMortgageRent),
                ["transportationPublicNational"] = FlaskJson.Text(result.TransportationPublicNational),
                ["transportationOwnershipOneCar"] = FlaskJson.Text(result.TransportationOwnershipOneCar),
                ["transportationOwnershipTwoCars"] = FlaskJson.Text(result.TransportationOwnershipTwoCars),
                ["transportationOperatingOneCar"] = FlaskJson.Text(result.TransportationOperatingOneCar),
                ["transportationOperatingTwoCars"] = FlaskJson.Text(result.TransportationOperatingTwoCars),
            },
            ["problems"] = new JsonArray([.. result.Problems.Select(problem => (JsonNode)problem)]),
        };
    }
}
