using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Assets;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Core.Debtors;
using Insolvia.Core.Exemptions;
using Insolvia.Core.Petitions;
using Insolvia.Core.Sofa;
using Insolvia.Engines.Liens;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Regulatory;

namespace Insolvia.Engines.Exemptions;

// The Schedule C workbench's arithmetic (from core/exemption_analysis.py,
// issue #346): which exemptions the case may claim, what it has claimed
// against each statute, and what each asset still leaves exposed.
//
// Rounding: `_money` is `value.quantize(_CENT)` — the context default,
// ROUND_HALF_EVEN; the inputs are cent-exact, so it fixes the scale.

/// <summary>What the analysis reads.</summary>
public sealed record ExemptionCase(Case Case)
{
    /// <summary>The debtor records.</summary>
    public IReadOnlyList<Debtor> Debtors { get; init; } = [];

    /// <summary>The petition.</summary>
    public PetitionBody? Petition { get; init; }

    /// <summary>The assets, with ids.</summary>
    public IReadOnlyList<(string Id, AssetBody Body)> Assets { get; init; } = [];

    /// <summary>The claims, with ids.</summary>
    public IReadOnlyList<(string Id, ClaimBody Body)> Claims { get; init; } = [];

    /// <summary>The exemption records, with ids.</summary>
    public IReadOnlyList<(string Id, ExemptionBody Body)> Exemptions { get; init; } = [];

    /// <summary>B107's entries (the domicile warning reads the prior addresses).</summary>
    public IReadOnlyList<SofaEntryBody> SofaEntries { get; init; } = [];
}

/// <summary>One answer the case may store for 106C line 1, and the scheme it means.</summary>
public sealed record ElectionOption(string Value, string SchemeId, string Name);

/// <summary>106C line 1 as resolved for this case.</summary>
public sealed record Election(string? Stored, string? Effective, string? State, bool? OptedOut, string? OptOutCitation, IReadOnlyList<ElectionOption> Options);

/// <summary>One claimable statute and what this case has left under it.</summary>
public sealed record EntryAvailability(ExemptionEntry Entry, decimal? Limit, decimal? Carryover, decimal Claimed, decimal? Available);

/// <summary>One exemption record on an asset, and what it counts as.</summary>
public sealed record AssetClaim(string ExemptionId, string? StatuteCitation, decimal? Amount, bool? ClaimsFullFmv, bool? AcquiredWithin1215Days, decimal Claimed, bool KnownStatute);

/// <summary>One asset's equity and what the claims on it cover.</summary>
public sealed record AssetExemptions(
    string AssetId,
    string? Description,
    string? Category,
    decimal? CurrentValue,
    decimal Liens,
    decimal? NetEquity,
    decimal Claimed,
    decimal? Unexempt,
    decimal? HomesteadCap,
    bool CapApplied,
    IReadOnlyList<AssetClaim> Claims,
    IReadOnlyList<(string EntryId, decimal Amount)> Suggestions);

/// <summary>The § 522 windows as calendar dates.</summary>
public sealed record Lookbacks(DateOnly Section522o, DateOnly Section522p, DateOnly Section522q, DateOnly DomicilePeriodStart);

/// <summary>The whole analysis.</summary>
public sealed record ExemptionAnalysis(
    DateOnly AsOf,
    string AsOfSource,
    Election Election,
    IReadOnlyList<EntryAvailability> Entries,
    IReadOnlyList<StatutoryLimit> Limits,
    Lookbacks Lookbacks,
    IReadOnlyList<AssetExemptions> Assets,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Problems);

/// <summary>The exemption workbench (Python's <c>core/exemption_analysis.py</c>).</summary>
public static class ExemptionAnalyzer
{
    /// <summary>106C line 1's state answer.</summary>
    public const string StateElection = "state_and_federal_nonbankruptcy";

    /// <summary>106C line 1's federal answer.</summary>
    public const string FederalElection = "federal";

    /// <summary>The § 522(p) cap's id on the federal series.</summary>
    public const string Homestead1215DayCapId = "us-homestead-1215-day-cap";

    /// <summary>§ 522(o): ten years.</summary>
    public const int Section522oYears = 10;

    /// <summary>§ 522(p): 1,215 days.</summary>
    public const int Section522pDays = 1215;

    /// <summary>§ 522(q): five years.</summary>
    public const int Section522qYears = 5;

    /// <summary>§ 522(b)(3)(A): the 730-day domicile period.</summary>
    public const int DomicilePeriodDays = 730;

    private const decimal Zero = 0.00m;

    /// <summary>The registry resolution date and its source: the expected filing date, else today.</summary>
    public static (DateOnly AsOf, string Source) ResolutionDate(PetitionBody? petition, DateOnly today) =>
        petition?.ExpectedFilingDate is { } expected ? (expected, "expected_filing_date") : (today, "today");

    /// <summary><paramref name="day"/> with the year stepped back, a leap day clamped to the 28th.</summary>
    public static DateOnly YearsBefore(DateOnly day, int years)
    {
        var year = day.Year - years;
        return day.Month == 2 && day.Day == 29 && !DateTime.IsLeapYear(year) ? new DateOnly(year, 2, 28) : new DateOnly(year, day.Month, day.Day);
    }

    /// <summary>The three § 522 windows and the domicile period, from <paramref name="asOf"/>.</summary>
    public static Lookbacks ComputeLookbacks(DateOnly asOf) => new(
        YearsBefore(asOf, Section522oYears),
        asOf.AddDays(-Section522pDays),
        YearsBefore(asOf, Section522qYears),
        asOf.AddDays(-DomicilePeriodDays));

    /// <summary>Why <paramref name="election"/> cannot be stored for a debtor in <paramref name="state"/>, or null where it can — the PATCH route's check.</summary>
    public static string? ElectionRefusal(string election, string? state, DateOnly asOf)
    {
        if (election != FederalElection || state is null)
        {
            return null;
        }

        IReadOnlyList<ExemptionScheme> schemes;
        try
        {
            schemes = ExemptionRegistry.SchemesForState(state, asOf);
        }
        catch (EngineLookupException)
        {
            return null;
        }

        var scheme = schemes[0];
        return scheme.OptedOutOfFederal == true
            ? $"{state} has opted out of the federal § 522(d) exemptions ({scheme.OptOutCitation ?? "None"}); the state scheme is the only answer."
            : null;
    }

    /// <summary>Whether a claim's citation draws down this entry.</summary>
    public static bool Cites(ExemptionEntry entry, string? citation)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return citation is not null && Authorities(entry.Citation).Contains(string.Join(' ', TextRules.SplitWhitespace(citation)));
    }

    /// <summary>The § 522(b)(3)(A) warning, as far as the case file can tell, or null.</summary>
    public static string? DomicileWarning(ExemptionCase inputs, string? state, DateOnly asOf, DateOnly periodStart)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        foreach (var prior in inputs.SofaEntries.Select(entry => entry.Payload).OfType<PriorAddress>())
        {
            if (prior.WhichDebtor is not (SofaDebtor.Debtor1 or SofaDebtor.Both))
            {
                continue;
            }

            var movedOut = prior.ToDate;
            if (movedOut is { } moved && moved < periodStart)
            {
                continue;
            }

            var where = string.IsNullOrEmpty(state) ? string.Empty : $" in {state}";
            var when = movedOut is { } date ? $"on {Exact.Iso(date)}" : "on a date not yet recorded";
            return $"Debtor 1 left a prior address {when}, inside the 730 days before {Exact.Iso(asOf)}. Under § 522(b)(3)(A) the exemptions of the "
                + $"state where the debtor was domiciled for the 180 days before {Exact.Iso(periodStart)} may govern, not the current "
                + $"residence{where}. Confirm the domicile before claiming.";
        }

        return null;
    }

    /// <summary>The analysis of one loaded case; <paramref name="today"/> is the caller's clock.</summary>
    public static ExemptionAnalysis Analyse(ExemptionCase inputs, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var problems = new List<string>();
        var warnings = new List<string>();

        var (asOf, asOfSource) = ResolutionDate(inputs.Petition, today);
        var lookbacks = ComputeLookbacks(asOf);
        var state = DebtorState(inputs.Debtors);
        var schemes = Schemes(state, asOf, problems);
        var (election, scheme) = ResolveElection(inputs.Case.ExemptionSet?.ToWire(), state, schemes, problems);

        IReadOnlyList<StatutoryLimit> limits;
        try
        {
            limits = ExemptionRegistry.FederalLimits(asOf);
        }
        catch (EngineLookupException refusal)
        {
            problems.Add(Exact.Str(refusal));
            limits = [];
        }

        var values = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        foreach (var (assetId, asset) in inputs.Assets)
        {
            values[assetId] = AssetValue(asset);
        }

        var joint = inputs.Debtors.Any(debtor => debtor.FilingRole == FilingRole.Debtor2);
        var entries = Entries(scheme, inputs, values, joint);
        var assets = Assets(inputs, entries, values, HomesteadCap(limits));

        if (scheme is not null)
        {
            var unknown = inputs.Exemptions
                .Select(pair => pair.Body.StatuteCitation)
                .OfType<string>()
                .Where(citation => !scheme.Entries.Any(entry => Cites(entry, citation)))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (unknown.Count > 0)
            {
                warnings.Add($"Claims cite a statute outside the {scheme.Name} scheme: {string.Join(", ", unknown)}. They do not draw down any limit in this table.");
            }
        }

        if (DomicileWarning(inputs, state, asOf, lookbacks.DomicilePeriodStart) is { } moved)
        {
            warnings.Add(moved);
        }

        return new ExemptionAnalysis(asOf, asOfSource, election, entries, limits, lookbacks, assets, warnings, problems);
    }

    /// <summary>The wire shape: camelCase, money as strings, null where a figure is not derivable.</summary>
    public static JsonObject Json(ExemptionAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var election = analysis.Election;
        return new JsonObject
        {
            ["asOf"] = Exact.Iso(analysis.AsOf),
            ["asOfSource"] = analysis.AsOfSource,
            ["election"] = new JsonObject
            {
                ["stored"] = election.Stored,
                ["effective"] = election.Effective,
                ["state"] = election.State,
                ["optedOut"] = election.OptedOut,
                ["optOutCitation"] = election.OptOutCitation,
                ["options"] = new JsonArray([.. election.Options.Select(option => (JsonNode)new JsonObject { ["value"] = option.Value, ["schemeId"] = option.SchemeId, ["name"] = option.Name })]),
            },
            ["entries"] = new JsonArray([.. analysis.Entries.Select(row => (JsonNode)new JsonObject
            {
                ["entryId"] = row.Entry.EntryId,
                ["category"] = row.Entry.Category.ToWire(),
                ["description"] = row.Entry.Description,
                ["citation"] = row.Entry.Citation,
                ["unlimited"] = row.Entry.Unlimited,
                ["limit"] = MoneyJson(row.Limit),
                ["perItemAmount"] = row.Entry.PerItemAmount,
                ["carryover"] = MoneyJson(row.Carryover),
                ["claimed"] = MoneyJson(row.Claimed),
                ["available"] = MoneyJson(row.Available),
                ["notes"] = row.Entry.Notes,
            })]),
            ["limits"] = new JsonArray([.. analysis.Limits.Select(limit => (JsonNode)new JsonObject
            {
                ["limitId"] = limit.LimitId,
                ["citation"] = limit.Citation,
                ["description"] = limit.Description,
                ["amount"] = limit.Amount,
            })]),
            ["lookbacks"] = new JsonObject
            {
                ["section522o"] = Exact.Iso(analysis.Lookbacks.Section522o),
                ["section522p"] = Exact.Iso(analysis.Lookbacks.Section522p),
                ["section522q"] = Exact.Iso(analysis.Lookbacks.Section522q),
                ["domicilePeriodStart"] = Exact.Iso(analysis.Lookbacks.DomicilePeriodStart),
            },
            ["assets"] = new JsonArray([.. analysis.Assets.Select(row => (JsonNode)AssetJson(row))]),
            ["warnings"] = new JsonArray([.. analysis.Warnings.Select(warning => (JsonNode)warning)]),
            ["problems"] = new JsonArray([.. analysis.Problems.Select(problem => (JsonNode)problem)]),
        };
    }

    private static decimal Money(decimal value) => Exact.Quantize(value, Rounding.HalfEven);

    private static string? MoneyJson(decimal? value) => value is { } present ? Exact.F(Money(present)) : null;

    private static JsonObject AssetJson(AssetExemptions row)
    {
        var suggestions = new JsonObject();
        foreach (var (entryId, amount) in row.Suggestions)
        {
            suggestions[entryId] = MoneyJson(amount);
        }

        return new JsonObject
        {
            ["assetId"] = row.AssetId,
            ["description"] = row.Description,
            ["category"] = row.Category,
            ["currentValue"] = MoneyJson(row.CurrentValue),
            ["liens"] = MoneyJson(row.Liens),
            ["netEquity"] = MoneyJson(row.NetEquity),
            ["claimed"] = MoneyJson(row.Claimed),
            ["unexempt"] = MoneyJson(row.Unexempt),
            ["homesteadCap"] = MoneyJson(row.HomesteadCap),
            ["capApplied"] = row.CapApplied,
            ["claims"] = new JsonArray([.. row.Claims.Select(claim => (JsonNode)new JsonObject
            {
                ["exemptionId"] = claim.ExemptionId,
                ["statuteCitation"] = claim.StatuteCitation,
                ["amount"] = MoneyJson(claim.Amount),
                ["claimsFullFmv"] = claim.ClaimsFullFmv,
                ["acquiredWithin1215Days"] = claim.AcquiredWithin1215Days,
                ["claimed"] = MoneyJson(claim.Claimed),
                ["knownStatute"] = claim.KnownStatute,
            })]),
            ["suggestions"] = suggestions,
        };
    }

    private static string? DebtorState(IReadOnlyList<Debtor> debtors)
    {
        var state = debtors.FirstOrDefault(debtor => debtor.FilingRole == FilingRole.Debtor1)?.Body.ResidenceAddress.State;
        return string.IsNullOrEmpty(state) ? null : state.Trim().ToUpperInvariant();
    }

    private static IReadOnlyList<ExemptionScheme> Schemes(string? state, DateOnly asOf, List<string> problems)
    {
        if (state is null)
        {
            problems.Add("Debtor 1 has no residence state on file; which exemptions apply depends on it (§ 522(b)(3)(A)).");
            return [];
        }

        try
        {
            return ExemptionRegistry.SchemesForState(state, asOf);
        }
        catch (EngineKeyException)
        {
            problems.Add($"Exemptions for {state} are not supported yet — Schedule C can be prepared for FL, TX and GA (ADR 0017).");
        }
        catch (EngineLookupException refusal)
        {
            problems.Add(Exact.Str(refusal));
        }

        return [];
    }

    private static (Election Election, ExemptionScheme? Scheme) ResolveElection(string? stored, string? state, IReadOnlyList<ExemptionScheme> schemes, List<string> problems)
    {
        if (schemes.Count == 0)
        {
            return (new Election(stored, null, state, null, null, []), null);
        }

        var stateScheme = schemes[0];
        var federal = schemes.Count > 1 ? schemes[1] : null;
        var options = new List<ElectionOption> { new(StateElection, stateScheme.SchemeId, stateScheme.Name) };
        if (federal is not null)
        {
            options.Add(new ElectionOption(FederalElection, federal.SchemeId, federal.Name));
        }

        var optedOut = stateScheme.OptedOutOfFederal == true;
        string? effective;
        if (optedOut)
        {
            if (stored == FederalElection)
            {
                problems.Add($"The case elects the federal § 522(d) list, but {state} has opted out ({stateScheme.OptOutCitation ?? "None"}); the state scheme is used.");
            }

            effective = StateElection;
        }
        else if (stored is null)
        {
            problems.Add($"Choose the exemption scheme: {state} lets the debtor claim the state scheme or the federal § 522(d) list (106C line 1).");
            effective = null;
        }
        else
        {
            effective = stored;
        }

        var scheme = effective is null ? null : effective == FederalElection ? federal : stateScheme;
        return (new Election(stored, effective, state, optedOut, stateScheme.OptOutCitation, options), scheme);
    }

    private static decimal? AssetValue(AssetBody? asset)
    {
        if (asset is null)
        {
            return null;
        }

        foreach (var candidate in new[] { asset.ValuePortionOwned, asset.ValueEntire })
        {
            if (candidate is { } value)
            {
                return Money(value);
            }
        }

        return null;
    }

    private static decimal ClaimedAmount(ExemptionBody claim, decimal? assetValue, decimal? limit)
    {
        if (claim.ClaimsFullFmv == true)
        {
            if (assetValue is not { } value)
            {
                return Zero;
            }

            return limit is { } cap ? Money(Exact.Min(value, cap)) : value;
        }

        return Money(claim.Amount ?? 0m);
    }

    private static decimal? BaseLimit(ExemptionEntry entry, bool joint)
    {
        if (joint && entry.JointAmount is not null)
        {
            return Exact.Dec(entry.JointAmount);
        }

        return entry.Amount is not null ? Exact.Dec(entry.Amount) : null;
    }

    private static HashSet<string> Authorities(string citation)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal);
        var whole = string.Join(' ', TextRules.SplitWhitespace(citation));
        forms.Add(whole);
        foreach (var piece in whole.Split(';'))
        {
            var part = piece.Trim();
            forms.Add(part);
            if (part.EndsWith(')') && part.Contains(" (", StringComparison.Ordinal))
            {
                forms.Add(part[..part.LastIndexOf(" (", StringComparison.Ordinal)].Trim());
            }
        }

        return forms;
    }

    private static decimal ClaimedUnder(ExemptionEntry entry, decimal? limit, ExemptionCase inputs, Dictionary<string, decimal?> values)
    {
        var total = Zero;
        foreach (var (_, claim) in inputs.Exemptions)
        {
            if (!Cites(entry, claim.StatuteCitation))
            {
                continue;
            }

            total += ClaimedAmount(claim, values.GetValueOrDefault(claim.AssetId ?? string.Empty), limit);
        }

        return Money(total);
    }

    private static List<EntryAvailability> Entries(ExemptionScheme? scheme, ExemptionCase inputs, Dictionary<string, decimal?> values, bool joint)
    {
        if (scheme is null)
        {
            return [];
        }

        var byId = new Dictionary<string, ExemptionEntry>(StringComparer.Ordinal);
        foreach (var entry in scheme.Entries)
        {
            byId[entry.EntryId] = entry;
        }

        var rows = new List<EntryAvailability>();
        foreach (var entry in scheme.Entries)
        {
            var limit = BaseLimit(entry, joint);
            decimal? carryover = null;
            var homestead = entry.WildcardCarryoverFrom is not null ? byId.GetValueOrDefault(entry.WildcardCarryoverFrom) : null;
            if (homestead is not null && entry.WildcardCarryoverCap is not null && BaseLimit(homestead, joint) is { } homesteadLimit)
            {
                var unused = Exact.Max(homesteadLimit - ClaimedUnder(homestead, homesteadLimit, inputs, values), Zero);
                carryover = Money(Exact.Min(unused, Exact.Dec(entry.WildcardCarryoverCap)));
                limit = (limit ?? Zero) + carryover.Value;
            }

            var claimed = ClaimedUnder(entry, limit, inputs, values);
            rows.Add(new EntryAvailability(
                entry,
                limit is { } present ? Money(present) : null,
                carryover,
                claimed,
                limit is { } cap ? Money(Exact.Max(cap - claimed, Zero)) : null));
        }

        return rows;
    }

    private static decimal? HomesteadCap(IReadOnlyList<StatutoryLimit> limits) =>
        limits.Where(limit => limit.LimitId == Homestead1215DayCapId).Select(limit => (decimal?)Exact.Dec(limit.Amount)).FirstOrDefault();

    private static List<AssetExemptions> Assets(ExemptionCase inputs, List<EntryAvailability> entries, Dictionary<string, decimal?> values, decimal? cap)
    {
        var figures = LienEngine.Derive(new CaseFile(inputs.Case) { Assets = inputs.Assets, Claims = inputs.Claims });
        var rows = new List<AssetExemptions>();
        foreach (var (assetId, asset) in inputs.Assets)
        {
            var current = values[assetId];
            var liens = figures.Asset(assetId)?.SecuredTotal ?? Zero;
            decimal? netEquity = current is { } value ? Money(Exact.Max(value - liens, Zero)) : null;

            var claims = new List<AssetClaim>();
            foreach (var (exemptionId, claim) in inputs.Exemptions)
            {
                if (claim.AssetId != assetId)
                {
                    continue;
                }

                var row = entries.FirstOrDefault(entry => Cites(entry.Entry, claim.StatuteCitation));
                claims.Add(new AssetClaim(
                    exemptionId,
                    claim.StatuteCitation,
                    claim.Amount is { } amount ? Money(amount) : null,
                    claim.ClaimsFullFmv,
                    claim.AcquiredWithin1215Days,
                    ClaimedAmount(claim, current, row?.Limit),
                    row is not null));
            }

            var claimed = Money(claims.Aggregate(Zero, (sum, claim) => sum + claim.Claimed));

            decimal? homesteadCap = null;
            var capApplied = false;
            if (cap is { } capAmount && asset.Category == AssetCategory.RealProperty && claims.Any(claim => claim.AcquiredWithin1215Days == true))
            {
                homesteadCap = Money(capAmount);
                if (claimed > homesteadCap)
                {
                    capApplied = true;
                }
            }

            var counted = homesteadCap is { } h ? Exact.Min(claimed, h) : claimed;
            decimal? unexempt = netEquity is { } equity ? Money(Exact.Max(equity - counted, Zero)) : null;

            var suggestions = new List<(string EntryId, decimal Amount)>();
            foreach (var row in entries)
            {
                var known = new[] { unexempt, row.Available }.Where(figure => figure is not null).Select(figure => figure!.Value).ToList();
                if (known.Count > 0)
                {
                    suggestions.Add((row.Entry.EntryId, Money(known.Aggregate(Exact.Min))));
                }
            }

            rows.Add(new AssetExemptions(assetId, asset.Description, asset.Category?.ToWire(), current, Money(liens), netEquity, claimed, unexempt, homesteadCap, capApplied, claims, suggestions));
        }

        return rows;
    }
}
