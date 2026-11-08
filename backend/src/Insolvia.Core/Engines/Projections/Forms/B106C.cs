using Insolvia.Core.Assets;
using Insolvia.Core.Cases;
using Insolvia.Core.Debtors;
using Insolvia.Core.Exemptions;
using Insolvia.Engines.Regulatory;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B106C @ 2025-04-01 (revision 04/25) — Schedule C's mapping (from
/// <c>form_projections/b106c.py</c>). Line 1 is forced by the opt-out rule
/// where the law forces it and is the case's election otherwise; line 3's
/// § 522(q) cap is a registry figure. Both registry reads resolve as of the
/// case's creation date.
/// </summary>
public static class B106C
{
    private const string HomesteadCapLimitId = "us-homestead-misconduct-cap";

    /// <summary>The values for <c>form/b106c@2025-04-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        Caption(caseFile, values);

        if (ExemptionSetExport(caseFile) is { } export)
        {
            values["line_1_exemption_set"] = new OptionFill(export);
        }

        for (var index = 0; index < caseFile.Exemptions.Count; index++)
        {
            var exemption = caseFile.Exemptions[index];
            var asset = caseFile.Asset(exemption.AssetId);
            if (asset is not null && !string.IsNullOrEmpty(asset.Description))
            {
                RowFill(release, values, "line_2_property_description", index, new TextFill(asset.Description), problems);
            }

            if (asset?.ValuePortionOwned is { } value)
            {
                RowFill(release, values, "line_2_current_value", index, new TextFill(FormatMoney(value)), problems);
            }

            if (exemption.ClaimsFullFmv is { } fullFmv)
            {
                RowFill(release, values, "line_2_exemption_kind", index, new OptionFill(fullFmv ? "fair market" : "On"), problems);
            }

            if (exemption.ClaimsFullFmv != true && exemption.Amount is { } claimed)
            {
                RowFill(release, values, "line_2_exemption_amount", index, new TextFill(FormatMoney(claimed)), problems);
            }

            if (!string.IsNullOrEmpty(exemption.StatuteCitation))
            {
                RowFill(release, values, "line_2_statute_citation", index, new TextFill(exemption.StatuteCitation), problems);
            }
        }

        var (overCap, acquired) = HomesteadAnswers(caseFile);
        values["line_3_homestead_over_cap"] = YesNo(release, "line_3_homestead_over_cap", overCap);
        if (overCap && acquired is { } within)
        {
            values["line_3_acquired_within_1215_days"] = YesNo(release, "line_3_acquired_within_1215_days", within);
        }

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }

    private static string? ExemptionSetExport(CaseFile caseFile)
    {
        var state = caseFile.Debtor(FilingRole.Debtor1)?.Body.ResidenceAddress.State;
        if (!string.IsNullOrEmpty(state))
        {
            IReadOnlyList<ExemptionScheme> schemes;
            try
            {
                schemes = ExemptionRegistry.SchemesForState(state, caseFile.CreatedOn);
            }
            catch (EngineLookupException)
            {
                schemes = [];
            }

            if (schemes.Count == 1)
            {
                return "state and federal";
            }
        }

        return caseFile.Case.ExemptionSet switch
        {
            ExemptionSet.StateAndFederalNonbankruptcy => "state and federal",
            ExemptionSet.Federal => "federal",
            _ => null,
        };
    }

    /// <summary>What one claim exempts: the asset's owned value when it claims the full FMV, else its stated amount (Python's <c>_claimed_amount</c>; the Case Upload file's stat field 35 sums it).</summary>
    public static decimal ClaimedAmount(ExemptionBody exemption, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(exemption);
        ArgumentNullException.ThrowIfNull(caseFile);
        return exemption.ClaimsFullFmv == true ? Amount(caseFile.Asset(exemption.AssetId)?.ValuePortionOwned) : Amount(exemption.Amount);
    }

    private static (bool OverCap, bool? Acquired) HomesteadAnswers(CaseFile caseFile)
    {
        var homesteads = caseFile.Exemptions
            .Where(exemption => caseFile.Asset(exemption.AssetId) is { Category: AssetCategory.RealProperty })
            .ToList();
        decimal? cap = ExemptionRegistry.FederalLimits(caseFile.CreatedOn)
            .Where(limit => limit.LimitId == HomesteadCapLimitId)
            .Select(limit => (decimal?)Exact.Dec(limit.Amount))
            .FirstOrDefault();
        var claimed = homesteads.Aggregate(0m, (sum, exemption) => sum + ClaimedAmount(exemption, caseFile));
        var overCap = cap is not null && claimed > cap;
        var answers = homesteads.Where(e => e.AcquiredWithin1215Days is not null).Select(e => e.AcquiredWithin1215Days!.Value).ToList();
        bool? acquired = answers.Count > 0 ? answers.Any(answer => answer) : null;
        return (overCap, acquired);
    }
}
