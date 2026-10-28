using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Core.Plans;
using Insolvia.Engines.CreditorMatrix;
using Insolvia.Engines.Exemptions;
using Insolvia.Engines.Liens;
using Insolvia.Engines.MeansTest;
using Insolvia.Engines.Plans;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Standards;

namespace Insolvia.Engines.Tests.Support;

/// <summary>
/// Produces, for one case file, exactly the document Parity/generate.py
/// writes from the Python — so the two can be diffed value by value.
/// </summary>
internal static class Oracle
{
    public static readonly DateOnly Today = new(2026, 10, 1);

    public static JsonObject Outputs(CaseFile caseFile)
    {
        var forms = new JsonObject();
        foreach (var series in FormTemplates.SeriesIds())
        {
            var release = FormTemplates.Latest(series);
            forms[series] = Outcome(() => FormProjections.Project(release, caseFile).ToJson());
        }

        PlanBody? plan = caseFile.Plans.Count > 0 ? caseFile.Plans[0] : null;
        return new JsonObject
        {
            ["forms"] = forms,
            ["means_test"] = Outcome(() => MeansTestTraces.Json(MeansTestTraces.Trace(caseFile))),
            ["liens"] = Outcome(() => LienEngine.Json(LienEngine.Derive(caseFile))),
            ["exemptions"] = Outcome(() => ExemptionAnalyzer.Json(ExemptionAnalyzer.Analyse(Chapter13Plan.ExemptionCaseOf(caseFile), Today))),
            ["plan"] = Outcome(() => Chapter13Plan.Json(Chapter13Plan.Calculate(Chapter13Plan.BuildPlanInputs(caseFile, Chapter13Plan.ExemptionCaseOf(caseFile), Today), plan))),
            ["standards"] = Outcome(() => StandardsJson(IrsStandards.Resolve(Today, caseFile.Debtors, caseFile.Dependents))),
            ["creditor_matrix"] = Outcome(() => CreditorMatrices.Json(CreditorMatrices.Generate(caseFile.Creditors, CreditorMatrices.FormatForCourt(caseFile.Case.Court)))),
        };
    }

    /// <summary>Every path where the two documents disagree, compared order-insensitively for objects.</summary>
    public static List<string> Diff(JsonNode? expected, JsonNode? actual, string path = "")
    {
        var differences = new List<string>();
        Walk(expected, actual, path, differences);
        return differences;
    }

    /// <summary>How many leaf values a document holds — the "fields matched" count.</summary>
    public static int Leaves(JsonNode? node) => node switch
    {
        JsonObject map => map.Sum(pair => Leaves(pair.Value)),
        JsonArray list => list.Sum(Leaves),
        _ => 1,
    };

    private static JsonObject Outcome(Func<JsonNode> call)
    {
        try
        {
            return new JsonObject { ["value"] = call() };
        }
        catch (FormProjectionException error)
        {
            return new JsonObject { ["problems"] = new JsonArray([.. error.Problems.Select(problem => (JsonNode)problem)]) };
        }
        catch (Exception error) when (error is EngineLookupException or EngineValueException or MeansTestException or MalformedReleaseException)
        {
            return new JsonObject
            {
                ["error"] = error switch
                {
                    EngineKeyException => "KeyError",
                    EngineLookupException => "LookupError",
                    MeansTestException => "MeansTestError",
                    _ => "ValueError",
                },
            };
        }
    }

    private static JsonObject StandardsJson(StandardsResult result) => new()
    {
        ["as_of"] = Py.Iso(result.AsOf),
        ["state"] = result.State,
        ["county"] = result.County,
        ["household_size"] = result.HouseholdSize,
        ["jurisdiction_source"] = result.JurisdictionSource,
        ["national_release_id"] = result.NationalReleaseId,
        ["national_allowance"] = result.NationalAllowance,
        ["oop_healthcare_under_65"] = result.OopHealthcareUnder65,
        ["oop_healthcare_65_and_older"] = result.OopHealthcare65AndOlder,
        ["local_release_id"] = result.LocalReleaseId,
        ["housing_non_mortgage"] = result.HousingNonMortgage,
        ["housing_mortgage_rent"] = result.HousingMortgageRent,
        ["transportation_public_national"] = result.TransportationPublicNational,
        ["transportation_ownership_one_car"] = result.TransportationOwnershipOneCar,
        ["transportation_ownership_two_cars"] = result.TransportationOwnershipTwoCars,
        ["transportation_operating_one_car"] = result.TransportationOperatingOneCar,
        ["transportation_operating_two_cars"] = result.TransportationOperatingTwoCars,
        ["problems"] = new JsonArray([.. result.Problems.Select(problem => (JsonNode)problem)]),
    };

    private static void Walk(JsonNode? expected, JsonNode? actual, string path, List<string> differences)
    {
        switch (expected)
        {
            case JsonObject map when actual is JsonObject other:
                foreach (var key in map.Select(pair => pair.Key).Union(other.Select(pair => pair.Key)).Order(StringComparer.Ordinal))
                {
                    if (!map.ContainsKey(key))
                    {
                        differences.Add($"{path}/{key}: unexpected {other[key]?.ToJsonString()}");
                    }
                    else if (!other.ContainsKey(key))
                    {
                        differences.Add($"{path}/{key}: missing {map[key]?.ToJsonString()}");
                    }
                    else
                    {
                        Walk(map[key], other[key], $"{path}/{key}", differences);
                    }
                }

                return;
            case JsonArray list when actual is JsonArray other && list.Count == other.Count:
                for (var index = 0; index < list.Count; index++)
                {
                    Walk(list[index], other[index], $"{path}[{index}]", differences);
                }

                return;
            default:
                if (!JsonNode.DeepEquals(expected, actual) && !SameNumber(expected, actual))
                {
                    differences.Add($"{path}: expected {expected?.ToJsonString() ?? "null"} got {actual?.ToJsonString() ?? "null"}");
                }

                return;
        }
    }

    private static bool SameNumber(JsonNode? expected, JsonNode? actual) =>
        expected is JsonValue left && actual is JsonValue right
        && left.GetValueKind() == JsonValueKind.Number && right.GetValueKind() == JsonValueKind.Number
        && left.ToJsonString() == right.ToJsonString();
}
