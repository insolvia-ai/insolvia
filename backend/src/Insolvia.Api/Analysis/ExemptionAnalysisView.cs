using Insolvia.Domain.Engines;
using Insolvia.Domain.Engines.Exemptions;
using Insolvia.Domain.Engines.Regulatory;

namespace Insolvia.Api.Analysis;

/// <summary>
/// The Schedule C workbench: which exemption scheme applies, what each
/// statute has left, and what the claims on each asset cover. Money is a
/// decimal string to the cent; a figure that cannot be derived yet is null.
/// </summary>
/// <param name="AsOf">The date the law is resolved as of, <c>YYYY-MM-DD</c>.</param>
/// <param name="AsOfSource">Where that date came from.</param>
/// <param name="Election">106C line 1: the federal or the state scheme.</param>
/// <param name="Entries">Every claimable statute of the effective scheme and what is left under it.</param>
/// <param name="Limits">The statutory caps that apply across entries.</param>
/// <param name="Lookbacks">The § 522 look-back windows as calendar dates.</param>
/// <param name="Assets">Each asset's equity and what the claims on it cover.</param>
/// <param name="Warnings">What a preparer should check, for a person.</param>
/// <param name="Problems">What is missing, for a person.</param>
public sealed record ExemptionAnalysisView(
    string AsOf,
    string AsOfSource,
    ExemptionElectionView Election,
    IReadOnlyList<ExemptionEntryView> Entries,
    IReadOnlyList<ExemptionLimitView> Limits,
    ExemptionLookbacksView Lookbacks,
    IReadOnlyList<ExemptionAssetView> Assets,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Problems)
{
    /// <summary>The view of <paramref name="analysis"/>.</summary>
    /// <param name="analysis">The engine's analysis.</param>
    /// <returns>The view.</returns>
    public static ExemptionAnalysisView Of(ExemptionAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var election = analysis.Election;
        var lookbacks = analysis.Lookbacks;
        return new(
            Exact.Iso(analysis.AsOf),
            analysis.AsOfSource,
            new(election.Stored, election.Effective, election.State, election.OptedOut, election.OptOutCitation, [.. election.Options.Select(option => new ExemptionElectionOption(option.Value, option.SchemeId, option.Name))]),
            [.. analysis.Entries.Select(ExemptionEntryView.Of)],
            [.. analysis.Limits.Select(limit => new ExemptionLimitView(limit.LimitId, limit.Citation, limit.Description, limit.Amount))],
            new(Exact.Iso(lookbacks.Section522o), Exact.Iso(lookbacks.Section522p), Exact.Iso(lookbacks.Section522q), Exact.Iso(lookbacks.DomicilePeriodStart)),
            [.. analysis.Assets.Select(ExemptionAssetView.Of)],
            analysis.Warnings,
            analysis.Problems);
    }

    // The engine's rounding: ROUND_HALF_EVEN to the cent.
    internal static string? Money(decimal? value) => value is { } present ? Money(present) : null;

    internal static string Money(decimal value) => Exact.Money(value, Rounding.HalfEven);
}

/// <summary>106C line 1 as resolved for this case.</summary>
/// <param name="Stored">The answer the case stores; null when unanswered.</param>
/// <param name="Effective">The answer in effect (the state's when it opted out).</param>
/// <param name="State">The state whose law applies; null when unknown.</param>
/// <param name="OptedOut">Whether that state opted out of the federal scheme; null when unknown.</param>
/// <param name="OptOutCitation">The opt-out statute; null when none.</param>
/// <param name="Options">The answers the case may store.</param>
public sealed record ExemptionElectionView(string? Stored, string? Effective, string? State, bool? OptedOut, string? OptOutCitation, IReadOnlyList<ExemptionElectionOption> Options);

/// <summary>One answer to 106C line 1.</summary>
/// <param name="Value">The stored value.</param>
/// <param name="SchemeId">The exemption scheme it means.</param>
/// <param name="Name">The scheme, for a person.</param>
public sealed record ExemptionElectionOption(string Value, string SchemeId, string Name);

/// <summary>One claimable statute and what this case has left under it.</summary>
/// <param name="EntryId">The statute's id in the scheme.</param>
/// <param name="Category">What kind of property it covers.</param>
/// <param name="Description">The statute, for a person.</param>
/// <param name="Citation">The statute's citation.</param>
/// <param name="Unlimited">Whether it has no dollar limit.</param>
/// <param name="Limit">Its limit for this case; null when unlimited or not derivable.</param>
/// <param name="PerItemAmount">A per-item cap; null when none.</param>
/// <param name="Carryover">Wildcard carried over into it; null when none applies.</param>
/// <param name="Claimed">Claimed under it so far.</param>
/// <param name="Available">Left to claim; null when unlimited or not derivable.</param>
/// <param name="Notes">The scheme's notes on it; empty when none.</param>
public sealed record ExemptionEntryView(
    string EntryId,
    ExemptionCategory Category,
    string Description,
    string Citation,
    bool Unlimited,
    string? Limit,
    string? PerItemAmount,
    string? Carryover,
    string Claimed,
    string? Available,
    string Notes)
{
    /// <summary>The view of <paramref name="row"/>.</summary>
    /// <param name="row">The entry's availability.</param>
    /// <returns>The view.</returns>
    public static ExemptionEntryView Of(EntryAvailability row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var entry = row.Entry;
        return new(entry.EntryId, entry.Category, entry.Description, entry.Citation, entry.Unlimited, ExemptionAnalysisView.Money(row.Limit), entry.PerItemAmount, ExemptionAnalysisView.Money(row.Carryover), ExemptionAnalysisView.Money(row.Claimed), ExemptionAnalysisView.Money(row.Available), entry.Notes);
    }
}

/// <summary>A statutory cap.</summary>
/// <param name="LimitId">The cap's id.</param>
/// <param name="Citation">Its citation.</param>
/// <param name="Description">The cap, for a person.</param>
/// <param name="Amount">The cap's amount.</param>
public sealed record ExemptionLimitView(string LimitId, string Citation, string Description, string Amount);

/// <summary>The § 522 look-back windows: each the first day the rule reaches back to.</summary>
/// <param name="Section522o">§ 522(o)'s ten years.</param>
/// <param name="Section522p">§ 522(p)'s 1215 days.</param>
/// <param name="Section522q">§ 522(q)'s ten years.</param>
/// <param name="DomicilePeriodStart">The start of § 522(b)(3)(A)'s 730-day domicile period.</param>
public sealed record ExemptionLookbacksView(string Section522o, string Section522p, string Section522q, string DomicilePeriodStart);

/// <summary>One asset's equity and what the claims on it cover.</summary>
/// <param name="AssetId">The asset record.</param>
/// <param name="Description">The asset, as described.</param>
/// <param name="Category">The asset's category.</param>
/// <param name="CurrentValue">Its value; null when not entered.</param>
/// <param name="Liens">The secured claims against it.</param>
/// <param name="NetEquity">Value less liens; null without a value.</param>
/// <param name="Claimed">Claimed exempt.</param>
/// <param name="Unexempt">Equity left unprotected; null without a value.</param>
/// <param name="HomesteadCap">The § 522(p) cap applied; null when none.</param>
/// <param name="CapApplied">Whether the cap cut the claim.</param>
/// <param name="Claims">The exemption records on the asset.</param>
/// <param name="Suggestions">What each statute could still protect on this asset, by entry id.</param>
public sealed record ExemptionAssetView(
    string AssetId,
    string? Description,
    string? Category,
    string? CurrentValue,
    string Liens,
    string? NetEquity,
    string Claimed,
    string? Unexempt,
    string? HomesteadCap,
    bool CapApplied,
    IReadOnlyList<ExemptionClaimView> Claims,
    IReadOnlyDictionary<string, string> Suggestions)
{
    /// <summary>The view of <paramref name="row"/>.</summary>
    /// <param name="row">The asset's figures.</param>
    /// <returns>The view.</returns>
    public static ExemptionAssetView Of(AssetExemptions row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var suggestions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entryId, amount) in row.Suggestions)
        {
            suggestions[entryId] = ExemptionAnalysisView.Money(amount);
        }

        return new(
            row.AssetId,
            row.Description,
            row.Category,
            ExemptionAnalysisView.Money(row.CurrentValue),
            ExemptionAnalysisView.Money(row.Liens),
            ExemptionAnalysisView.Money(row.NetEquity),
            ExemptionAnalysisView.Money(row.Claimed),
            ExemptionAnalysisView.Money(row.Unexempt),
            ExemptionAnalysisView.Money(row.HomesteadCap),
            row.CapApplied,
            [.. row.Claims.Select(claim => new ExemptionClaimView(claim.ExemptionId, claim.StatuteCitation, ExemptionAnalysisView.Money(claim.Amount), claim.ClaimsFullFmv, claim.AcquiredWithin1215Days, ExemptionAnalysisView.Money(claim.Claimed), claim.KnownStatute))],
            suggestions);
    }
}

/// <summary>One exemption record on an asset, and what it counts as.</summary>
/// <param name="ExemptionId">The exemption record.</param>
/// <param name="StatuteCitation">The statute it cites; null when not entered.</param>
/// <param name="Amount">The amount entered; null when not entered.</param>
/// <param name="ClaimsFullFmv">Whether it claims 100% of fair market value.</param>
/// <param name="AcquiredWithin1215Days">Whether the interest was acquired inside § 522(p)'s window.</param>
/// <param name="Claimed">What it counts as.</param>
/// <param name="KnownStatute">Whether the citation is in the effective scheme.</param>
public sealed record ExemptionClaimView(string ExemptionId, string? StatuteCitation, string? Amount, bool? ClaimsFullFmv, bool? AcquiredWithin1215Days, string Claimed, bool KnownStatute);
