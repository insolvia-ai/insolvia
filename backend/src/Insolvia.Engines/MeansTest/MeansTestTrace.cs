using System.Text.Json.Nodes;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Projections.Forms;

namespace Insolvia.Engines.MeansTest;

/// <summary>One run of the test for the screen: the inputs as the engine read them, the outcome when reached, and every reason it was not.</summary>
public sealed record MeansTestTrace(
    DateOnly AsOf,
    string AsOfSource,
    IReadOnlyDictionary<string, string> ReleaseIds,
    MeansTestCase Case,
    string? MaritalFilingStatus,
    string MaritalSource,
    MeansTestResult? Result,
    IReadOnlyList<string> Problems);

/// <summary>
/// <c>GET /v1/cases/&lt;id&gt;/means-test</c>'s body (from <c>core/means_test_trace.py</c>):
/// assembled from the same calls the B122 projections make, and nothing
/// else — no arithmetic here. Progressive: the engine's refusals become
/// problems next to whatever did compute.
/// </summary>
public static class MeansTestTraces
{
    /// <summary>The trace for one case file — pure, and never throwing a refusal.</summary>
    public static MeansTestTrace Trace(CaseFile caseFile)
    {
        var (asOf, asOfSource) = B122A1.MeansTestAsOf(caseFile);
        var test = B122A2.BuildMeansTestCase(caseFile);
        var problems = new List<string>(test.Cmi.Problems);

        var (status, statusSource) = B122A1.MaritalFilingStatus(caseFile);
        if (status is null)
        {
            problems.Add(statusSource);
        }

        MeansTestResult? result = null;
        IReadOnlyDictionary<string, string> releaseIds = new Dictionary<string, string>(StringComparer.Ordinal);
        MeansTestData? data = null;
        try
        {
            data = MeansTestEngine.ResolveData(asOf);
        }
        catch (EngineLookupException error)
        {
            problems.Add(Exact.Str(error));
        }

        if (data is not null)
        {
            releaseIds = data.ReleaseIds;
            try
            {
                result = MeansTestEngine.Run(test, data);
            }
            catch (MeansTestException error)
            {
                problems.AddRange(error.Problems);
            }
        }

        return new MeansTestTrace(asOf, asOfSource, releaseIds, test, status, statusSource, result, problems);
    }

    /// <summary>The wire shape: money as strings, absent figures null, outcome <c>undetermined</c> when the engine refused.</summary>
    public static JsonObject Json(MeansTestTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var inputs = trace.Case.Inputs;
        var exemption = trace.Case.Chapter == 7 ? MeansTestEngine.PresumptionExemption(inputs) : null;
        var result = trace.Result;
        var releaseIds = new JsonObject();
        foreach (var (series, release) in trace.ReleaseIds)
        {
            releaseIds[series] = release;
        }

        return new JsonObject
        {
            ["asOf"] = Exact.Iso(trace.AsOf),
            ["asOfSource"] = trace.AsOfSource,
            ["chapter"] = trace.Case.Chapter,
            ["releaseIds"] = releaseIds,
            ["jurisdiction"] = new JsonObject
            {
                ["state"] = string.IsNullOrEmpty(trace.Case.State) ? null : trace.Case.State,
                ["county"] = string.IsNullOrEmpty(trace.Case.County) ? null : trace.Case.County,
                ["district"] = trace.Case.District,
            },
            ["maritalFilingStatus"] = new JsonObject
            {
                ["value"] = trace.MaritalFilingStatus,
                ["source"] = trace.MaritalSource,
            },
            ["household"] = new JsonObject
            {
                ["peopleUnder65"] = inputs.PeopleUnder65,
                ["people65OrOlder"] = inputs.People65OrOlder,
                ["medianHouseholdSize"] = SizeJson(MeansTestEngine.MedianHouseholdSize(inputs)),
                ["irsFamilySize"] = SizeJson(MeansTestEngine.IrsFamilySize(inputs)),
                ["irsHousingFamilySize"] = SizeJson(MeansTestEngine.IrsHousingFamilySize(inputs)),
                ["childrenUnder18"] = trace.Case.ChildrenUnder18,
            },
            ["exemptions"] = new JsonObject
            {
                ["nonConsumerDebts"] = inputs.NonConsumerDebts == true,
                ["disabledVeteran"] = inputs.DisabledVeteran == true,
                ["reservistNationalGuard"] = inputs.ReservistNationalGuard == true,
                ["applied"] = exemption?.Flag,
                ["rule"] = exemption?.Rule,
                ["available"] = new JsonArray([.. MeansTestEngine.PresumptionExemptions.Select(item => (JsonNode)item.Flag)]),
            },
            ["cmi"] = CmiJson(trace.Case.Cmi),
            ["debt"] = new JsonObject
            {
                ["priorityTotal"] = trace.Case.PriorityDebtTotal,
                ["nonpriorityUnsecuredTotal"] = trace.Case.NonpriorityUnsecuredTotal,
            },
            ["comparison"] = result?.Comparison is { } comparison ? ComparisonJson(comparison) : null,
            ["outcome"] = result is not null ? result.Outcome : "undetermined",
            ["determinedBy"] = result?.DeterminedBy,
            ["lines"] = new JsonArray([.. (result?.Lines ?? []).Select(line => (JsonNode)LineJson(line))]),
            ["chapter13"] = Chapter13Json(result),
            ["problems"] = new JsonArray([.. trace.Problems.Select(problem => (JsonNode)problem)]),
        };
    }

    /// <summary>The CMI derivation's wire shape.</summary>
    public static JsonObject CmiJson(CmiResult cmi)
    {
        ArgumentNullException.ThrowIfNull(cmi);
        return new JsonObject
        {
            ["window"] = new JsonObject
            {
                ["filingDate"] = Exact.Iso(cmi.Window.FilingDate),
                ["start"] = Exact.Iso(cmi.Window.Start),
                ["end"] = Exact.Iso(cmi.Window.End),
                ["months"] = new JsonArray([.. cmi.Window.Months.Select(month => (JsonNode)month)]),
            },
            ["columns"] = new JsonArray([.. cmi.Columns.Select(column => (JsonNode)ColumnJson(column))]),
            ["combinedMonthlyTotal"] = cmi.CombinedMonthlyTotal,
            ["annualized"] = cmi.Annualized,
            ["gaps"] = new JsonArray([.. cmi.Gaps.Select(gap => (JsonNode)new JsonObject
            {
                ["employer"] = gap.Employer,
                ["months"] = new JsonArray([.. gap.Months.Select(month => (JsonNode)month)]),
            })]),
            ["problems"] = new JsonArray([.. cmi.Problems.Select(problem => (JsonNode)problem)]),
        };
    }

    private static JsonObject EntryJson(CmiEntry entry) => new()
    {
        ["receivedOn"] = entry.ReceivedOn,
        ["amount"] = entry.Amount,
        ["description"] = entry.Description,
    };

    private static JsonObject CmiLineJson(CmiLine line)
    {
        var body = new JsonObject
        {
            ["category"] = line.Category,
            ["label"] = line.Label,
            ["totalReceived"] = line.TotalReceived,
            ["monthlyAverage"] = line.MonthlyAverage,
            ["citation"] = line.Citation,
            ["note"] = line.Note,
            ["entries"] = new JsonArray([.. line.Entries.Select(entry => (JsonNode)EntryJson(entry))]),
        };
        if (line.GrossMonthlyAverage is not null)
        {
            body["grossMonthlyAverage"] = line.GrossMonthlyAverage;
        }

        if (line.ExpensesMonthlyAverage is not null)
        {
            body["expensesMonthlyAverage"] = line.ExpensesMonthlyAverage;
        }

        return body;
    }

    private static JsonObject ColumnJson(CmiColumn column) => new()
    {
        ["column"] = column.Column,
        ["lines"] = new JsonArray([.. column.Lines.Select(line => (JsonNode)CmiLineJson(line))]),
        ["excluded"] = new JsonArray([.. column.Excluded.Select(line => (JsonNode)CmiLineJson(line))]),
        ["monthlyTotal"] = column.MonthlyTotal,
    };

    private static JsonObject ComparisonJson(MedianComparison comparison) => new()
    {
        ["state"] = comparison.State,
        ["householdSize"] = comparison.HouseholdSize,
        ["monthlyCmi"] = comparison.MonthlyCmi,
        ["annualizedCmi"] = comparison.AnnualizedCmi,
        ["annualMedian"] = comparison.AnnualMedian,
        ["aboveMedian"] = comparison.AboveMedian,
        ["source"] = comparison.Source,
    };

    private static JsonObject LineJson(MeansTestLine line) => new()
    {
        ["form"] = line.Form,
        ["line"] = line.Line,
        ["label"] = line.Label,
        ["amount"] = line.Amount,
        ["source"] = line.Source,
    };

    private static JsonObject? Chapter13Json(MeansTestResult? result)
    {
        if (result is null || result.Chapter != 13)
        {
            return null;
        }

        var commitment = result.Commitment!;
        return new JsonObject
        {
            ["commitmentPeriodMonths"] = commitment.Months,
            ["commitmentSource"] = commitment.Source,
            ["commitmentMaritalAdjustment"] = commitment.MaritalAdjustment,
            ["commitmentMonthlyIncome"] = commitment.MonthlyIncome,
            ["commitmentAnnualizedIncome"] = commitment.AnnualizedIncome,
            ["commitmentAnnualMedian"] = commitment.AnnualMedian,
            ["disposableIncomeRequired"] = result.Outcome == "above_median",
            ["monthlyDisposableIncome"] = result.DisposableIncome,
        };
    }

    private static JsonObject SizeJson((int Size, string Source)? size) => size is { } value
        ? new JsonObject { ["value"] = value.Size, ["source"] = value.Source }
        : new JsonObject { ["value"] = null, ["source"] = null };
}
