using System.Globalization;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Engines;
using Insolvia.Engines.Projections;

namespace Insolvia.Forms;

/// <summary>The hub row's one number: an item count (Schedules A/B-H) or a dollar total (I, J).</summary>
public sealed record FormMetric(string Kind, string Value);

/// <summary>One row of the forms hub: a form this case files, its metric, and the problems that belong to it.</summary>
public sealed record FormSummary(string Series, string Form, string Title, string OfficialNumber, FormMetric? Metric, IReadOnlyList<PacketProblem> Problems);

/// <summary>
/// The forms hub (from <c>core/forms_hub.py</c>, issue 13.2): per-form
/// status and the single-form preview, walking packet assembly's own
/// functions per form rather than defining a second "required forms".
/// </summary>
public static class FormsHub
{
    /// <summary>Collection source to every form series it feeds (case-data-model.md's Feeds column).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FormProblemSources = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["petitions"] = ["form/b101"],
        ["means_test_inputs"] = ["form/b122a2", "form/b122c1", "form/b122c2"],
        ["households"] = ["form/b106j", "form/b106j2"],
        ["income_summaries"] = ["form/b106i"],
        ["claims"] = ["form/b106d", "form/b106ef"],
        ["exemptions"] = ["form/b106c"],
        ["employments"] = ["form/b106i"],
        ["pay_period_records"] = ["form/b122a1", "form/b122a2", "form/b122c1", "form/b122c2"],
        ["other_income_records"] = ["form/b122a1", "form/b122a2", "form/b122c1", "form/b122c2"],
        ["expenses"] = ["form/b106j", "form/b106j2"],
        ["dependents"] = ["form/b106j", "form/b106j2"],
        ["codebtors"] = ["form/b106h"],
        ["contract_leases"] = ["form/b108", "form/b113"],
        ["plans"] = ["form/b113"],
    };

    private static readonly string[] UniversalSources = ["case", "debtors"];

    /// <summary>
    /// Every completeness problem, grouped by the form series it belongs to —
    /// one entry per form this case files, in filing order. A universal
    /// problem (case, debtors) appears under every form.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<PacketProblem>> GroupProblems(CaseData data, OutputOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var seriesIds = PacketAssembly.PacketFormSeriesFor(data);
        var byForm = new OrderedDictionary<string, List<PacketProblem>>(StringComparer.Ordinal);
        foreach (var series in seriesIds)
        {
            byForm[series] = [];
        }

        foreach (var problem in PacketAssembly.CompletenessProblems(data, options))
        {
            var targets = UniversalSources.Contains(problem.Source)
                ? seriesIds
                : (FormProblemSources.TryGetValue(problem.Source, out var fed) ? fed : []).Where(byForm.ContainsKey).ToList();
            foreach (var series in targets)
            {
                byForm[series].Add(problem);
            }
        }

        var grouped = new OrderedDictionary<string, IReadOnlyList<PacketProblem>>(StringComparer.Ordinal);
        foreach (var (series, problems) in byForm)
        {
            grouped[series] = problems;
        }

        return grouped;
    }

    /// <summary>
    /// The release this case's form prints from: the pinned one once the case
    /// is pinned, the one in force on <paramref name="asOf"/> while it floats.
    /// <see cref="EngineLookupException"/> when none is.
    /// </summary>
    public static FormRelease ResolveCaseForm(Case @case, string seriesId, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(@case);
        if (@case.FormRevisions is { } revisions && revisions.TryGetValue(seriesId, out var pin))
        {
            return FormTemplates.Get(seriesId, $"{seriesId}@{pin}");
        }

        return FormTemplates.Resolve(seriesId, asOf);
    }

    private static Dictionary<string, FormMetric> Metrics(CaseData data, CaseFile caseFile)
    {
        var totals = CaseSummaries.Summarise(data).Totals;
        string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
        return new Dictionary<string, FormMetric>(StringComparer.Ordinal)
        {
            ["form/b106ab"] = new("count", Count(data.Assets.Count)),
            ["form/b106c"] = new("count", Count(data.Exemptions.Count)),
            ["form/b106d"] = new("count", Count(ProjectionHelpers.ClaimsOf(caseFile, ClaimClass.Secured).Count)),
            ["form/b106ef"] = new("count", Count(ProjectionHelpers.ClaimsOf(caseFile, ClaimClass.PriorityUnsecured, ClaimClass.NonpriorityUnsecured).Count)),
            ["form/b106g"] = new("count", Count(data.ContractLeases.Count)),
            ["form/b106h"] = new("count", Count(data.Codebtors.Count + data.CommunityHouseholdMembers.Count)),
            ["form/b106i"] = new("total", Engines.Exact.F(totals.MonthlyIncome)),
            ["form/b106j"] = new("total", Engines.Exact.F(totals.MonthlyExpenses)),
        };
    }

    /// <summary>Every form this case files, with its metric and its own slice of the completeness list, in filing order.</summary>
    public static IReadOnlyList<FormSummary> Summaries(CaseData data, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(data);
        var caseFile = data.ToCaseFile();
        var grouped = GroupProblems(data);
        var metrics = Metrics(data, caseFile);
        var summaries = new List<FormSummary>();
        foreach (var seriesId in PacketAssembly.PacketFormSeriesFor(data))
        {
            IReadOnlyList<PacketProblem> problems = grouped.TryGetValue(seriesId, out var found) ? found : [];
            string title, officialNumber, form;
            try
            {
                var release = ResolveCaseForm(data.Case, seriesId, asOf);
                (title, officialNumber, form) = (release.Title, release.OfficialNumber, release.Form);
            }
            catch (EngineLookupException error)
            {
                (title, officialNumber, form) = (string.Empty, string.Empty, seriesId["form/".Length..]);
                problems = [.. problems, new PacketProblem(seriesId, null, string.Empty, Engines.Exact.Str(error))];
            }

            summaries.Add(new FormSummary(seriesId, form, title, officialNumber, metrics.GetValueOrDefault(seriesId), problems));
        }

        return summaries;
    }

    /// <summary>The metric's wire shape.</summary>
    public static JsonObject FormMetricJson(FormMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return new JsonObject { ["kind"] = metric.Kind, ["value"] = metric.Value };
    }

    /// <summary>
    /// Render exactly ONE form through the packet's own pipeline — the same
    /// gate, projection, options and engine — so a preview is byte-identical
    /// to what the packet would print, or refused with the reasons.
    /// </summary>
    public static (byte[]? Pdf, IReadOnlyList<PacketProblem> Problems) RenderFormPreview(
        CaseData data,
        string seriesId,
        DateOnly asOf,
        DateTimeOffset printedAt,
        OutputOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= FormOverlay.DefaultOutputOptions;
        var grouped = GroupProblems(data, options);
        if (grouped.TryGetValue(seriesId, out var gate) && gate.Count > 0)
        {
            return (null, gate);
        }

        var caseFile = data.ToCaseFile();
        var renderCaseFile = caseFile;
        if (options.AmendedOnly)
        {
            if (!PacketAssembly.AmendmentRenderSeries(data, PacketAssembly.PacketFormSeriesFor(data)).Contains(seriesId))
            {
                return (null, [new PacketProblem(seriesId, null, string.Empty, "This form is not part of an amendment: no item it prints is marked amended.")]);
            }

            renderCaseFile = PacketAssembly.FilteredForRender(data, seriesId).ToCaseFile();
        }

        FormRelease release;
        try
        {
            release = options.AmendedOnly ? FormTemplates.Resolve(seriesId, asOf) : ResolveCaseForm(data.Case, seriesId, asOf);
        }
        catch (EngineLookupException error)
        {
            return (null, [new PacketProblem(seriesId, null, string.Empty, Engines.Exact.Str(error))]);
        }

        FieldValues values;
        try
        {
            values = FormProjections.Project(release, renderCaseFile);
        }
        catch (FormProjectionException error)
        {
            return (null, [.. error.Problems.Select(message => new PacketProblem(seriesId, null, string.Empty, message))]);
        }

        var signed = FormOverlay.ApplyAmendmentOptions(
            release,
            FormOverlay.ApplySignatureOptions(release, values, caseFile, options, asOf),
            options.AmendedOnly);
        byte[] rendered;
        try
        {
            rendered = FormFill.Fill(release, signed);
        }
        catch (FormFillException error)
        {
            return (null, [.. error.Problems.Select(message => new PacketProblem(seriesId, null, string.Empty, message))]);
        }

        if (FormOverlay.ApplyOutputStamps(rendered, release, options, printedAt) is not { } stamped)
        {
            return (null, [new PacketProblem(seriesId, null, string.Empty, "This form has no signature lines, so there is nothing to show with signature pages only.")]);
        }

        return (stamped, []);
    }

    /// <summary>Where one rendered preview's bytes live: <c>form-previews/&lt;case&gt;/&lt;preview&gt;.pdf</c>, server-minted uuids only.</summary>
    public static string FormPreviewObjectKey(string caseId, string previewId)
    {
        ArgumentNullException.ThrowIfNull(caseId);
        ArgumentNullException.ThrowIfNull(previewId);
        if (!Packets.Uuid().IsMatch(caseId) || !Packets.Uuid().IsMatch(previewId))
        {
            throw new ValidationException("object keys are built from server-minted uuids only");
        }

        return $"form-previews/{caseId}/{previewId}.pdf";
    }

    /// <summary>A fresh preview id.</summary>
    public static string NewPreviewId() => Guid.NewGuid().ToString();
}
