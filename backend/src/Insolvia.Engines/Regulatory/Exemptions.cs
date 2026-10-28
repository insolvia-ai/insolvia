using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static Insolvia.Engines.Regulatory.ReleaseParsing;

namespace Insolvia.Engines.Regulatory;

// The exemption dataset: loader and resolution for the `exemptions/*` series
// (from core/exemptions.py). Which states launch supports is ADR 0017's
// provisional decision; the data is the registry's (ADR 0014). Every figure
// is cited and tiered, resolution follows effective-dating.md exactly (the
// release in force on the filing date, ties to the highest sequence, refusing
// a date before the series begins), and money stays two-decimal strings.

/// <summary>What kind of property an exemption protects — the analyzer's axis.</summary>
public enum ExemptionCategory
{
    /// <summary>A homestead.</summary>
    [JsonStringEnumMemberName("homestead")]
    Homestead,

    /// <summary>Personal property.</summary>
    [JsonStringEnumMemberName("personal_property")]
    PersonalProperty,

    /// <summary>A vehicle.</summary>
    [JsonStringEnumMemberName("vehicle")]
    Vehicle,

    /// <summary>Household goods.</summary>
    [JsonStringEnumMemberName("household_goods")]
    HouseholdGoods,

    /// <summary>Jewelry.</summary>
    [JsonStringEnumMemberName("jewelry")]
    Jewelry,

    /// <summary>Tools of the trade.</summary>
    [JsonStringEnumMemberName("tools_of_trade")]
    ToolsOfTrade,

    /// <summary>A wildcard.</summary>
    [JsonStringEnumMemberName("wildcard")]
    Wildcard,

    /// <summary>Life insurance.</summary>
    [JsonStringEnumMemberName("life_insurance")]
    LifeInsurance,

    /// <summary>Health aids.</summary>
    [JsonStringEnumMemberName("health_aids")]
    HealthAids,

    /// <summary>Support and benefits.</summary>
    [JsonStringEnumMemberName("support_and_benefits")]
    SupportAndBenefits,

    /// <summary>Personal injury.</summary>
    [JsonStringEnumMemberName("personal_injury")]
    PersonalInjury,

    /// <summary>Wages.</summary>
    [JsonStringEnumMemberName("wages")]
    Wages,

    /// <summary>Retirement.</summary>
    [JsonStringEnumMemberName("retirement")]
    Retirement,
}

/// <summary>How an entry's figure was verified (ingestion-time provenance).</summary>
public enum Verification
{
    /// <summary>Two independent official government sources agree.</summary>
    [JsonStringEnumMemberName("primary_corroborated")]
    PrimaryCorroborated,

    /// <summary>One official government source.</summary>
    [JsonStringEnumMemberName("primary")]
    Primary,

    /// <summary>Unofficial mirror(s) of the statute text.</summary>
    [JsonStringEnumMemberName("mirror")]
    Mirror,

    /// <summary>Not independently confirmed — never acceptable on a dollar amount.</summary>
    [JsonStringEnumMemberName("unverified")]
    Unverified,
}

/// <summary>One document a figure was checked against, and when.</summary>
public sealed record Source(string Title, string Url, DateOnly Accessed);

/// <summary>One claimable exemption: what 106C's "specific laws" column cites. Money is two-decimal strings.</summary>
public sealed record ExemptionEntry(
    string EntryId,
    ExemptionCategory Category,
    string Description,
    string Citation,
    string? Amount,
    bool Unlimited,
    Verification Verification,
    IReadOnlyList<Source> Sources,
    string? PerItemAmount = null,
    string? JointAmount = null,
    string? WildcardCarryoverFrom = null,
    string? WildcardCarryoverCap = null,
    DateOnly? FigureSince = null,
    string Notes = "");

/// <summary>A body of exemption law a debtor may claim under (106C line 1).</summary>
public sealed record ExemptionScheme(
    string SchemeId,
    string Jurisdiction,
    string Name,
    bool? OptedOutOfFederal,
    string? OptOutCitation,
    IReadOnlyList<ExemptionEntry> Entries,
    string Notes = "");

/// <summary>A federal cap that is not itself claimable but limits what is (§ 522(p)/(q)).</summary>
public sealed record StatutoryLimit(
    string LimitId,
    string Citation,
    string Description,
    string Amount,
    DateOnly EffectiveDate,
    DateOnly? NextAdjustment,
    Verification Verification,
    IReadOnlyList<Source> Sources,
    string Notes = "");

/// <summary>One immutable release of an exemption series (effective-dating.md).</summary>
public sealed record ExemptionRelease(
    string SeriesId,
    DateOnly EffectiveDate,
    int Sequence,
    string SourceUrl,
    DateOnly? SourcePublished,
    string? SourceSha256,
    string Notes,
    ExemptionScheme Scheme,
    IReadOnlyList<StatutoryLimit> Limits) : IRelease
{
    /// <summary><c>series@date[+sequence]</c>.</summary>
    public string ReleaseId => Releases.Id(SeriesId, EffectiveDate, Sequence);
}

/// <summary>The exemption registry: load, validate, resolve (Python's <c>core/exemptions.py</c>).</summary>
public static class ExemptionRegistry
{
    /// <summary>The next scheduled § 104(b) adjustment of every federal figure.</summary>
    public static readonly DateOnly NextFederalAdjustment = new(2028, 4, 1);

    /// <summary>The launch set, per ADR 0017.</summary>
    public static readonly IReadOnlyList<string> LaunchStates = ["FL", "TX", "GA"];

    /// <summary>The federal series.</summary>
    public const string FederalSeries = "exemptions/federal";

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<ExemptionRelease>>> Shipped =
        new(() => LoadRegistry(RegistryTree.Shipped));

    /// <summary>The committed registry, shipped inside this assembly.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ExemptionRelease>> Registry => Shipped.Value;

    /// <summary>Loads and validates every exemption release under a registry root.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ExemptionRelease>> LoadRegistry(RegistryTree root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var series = new SortedDictionary<string, IReadOnlyList<ExemptionRelease>>(StringComparer.Ordinal);
        foreach (var seriesName in root.ChildDirectories("exemptions"))
        {
            var seriesId = $"exemptions/{seriesName}";
            var loaded = root.ChildDirectories($"exemptions/{seriesName}")
                .Select(release => LoadRelease(root, $"exemptions/{seriesName}/{release}", release, seriesId))
                .ToList();
            if (loaded.Count == 0)
            {
                throw Fail(seriesId, "series has no releases");
            }

            series[seriesId] = Releases.Ordered(loaded, seriesId);
        }

        if (series.Count == 0)
        {
            throw new MalformedReleaseException("the exemptions registry is empty");
        }

        return series;
    }

    /// <summary>The series ids, sorted.</summary>
    public static IReadOnlyList<string> SeriesIds() => [.. Registry.Keys.Order(StringComparer.Ordinal)];

    /// <summary>A series' releases, oldest first.</summary>
    public static IReadOnlyList<ExemptionRelease> ReleasesOf(string seriesId) =>
        Registry.TryGetValue(seriesId, out var found) ? found : throw new EngineKeyException($"unknown series {Py.Repr(seriesId)}");

    /// <summary>The release with the greatest effective date on or before <paramref name="asOf"/>.</summary>
    public static ExemptionRelease PickRelease(IReadOnlyList<ExemptionRelease> candidates, DateOnly asOf) =>
        Releases.Pick(candidates, asOf, "refusing to compute on data that does not describe that date");

    /// <summary>The release in force on <paramref name="asOf"/>.</summary>
    public static ExemptionRelease Resolve(string seriesId, DateOnly asOf) => PickRelease(ReleasesOf(seriesId), asOf);

    /// <summary>That exact release.</summary>
    public static ExemptionRelease Get(string seriesId, string releaseId) =>
        ReleasesOf(seriesId).FirstOrDefault(release => release.ReleaseId == releaseId)
        ?? throw new EngineKeyException($"series {Py.Repr(seriesId)} has no release {Py.Repr(releaseId)}");

    /// <summary>Newest by (effective date, sequence).</summary>
    public static ExemptionRelease Latest(string seriesId) => ReleasesOf(seriesId)[^1];

    /// <summary>
    /// The schemes a debtor domiciled in <paramref name="state"/> may elect
    /// between (106C line 1): the state's alone where it opted out, the state's
    /// and the federal set otherwise. Throws <see cref="EngineKeyException"/>
    /// for a state outside the launch set.
    /// </summary>
    public static IReadOnlyList<ExemptionScheme> SchemesForState(string state, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(state);
        var seriesId = $"exemptions/{Core.PyText.Lower(state)}";
        if (!LaunchStates.Contains(state.ToUpperInvariant()) || !Registry.ContainsKey(seriesId))
        {
            throw new EngineKeyException($"state {Py.Repr(state)} is not in the launch set ('FL', 'TX', 'GA') (ADR 0017)");
        }

        var scheme = Resolve(seriesId, asOf).Scheme;
        return scheme.OptedOutOfFederal == true ? [scheme] : [scheme, Resolve(FederalSeries, asOf).Scheme];
    }

    /// <summary>The § 522 caps in force on <paramref name="asOf"/>.</summary>
    public static IReadOnlyList<StatutoryLimit> FederalLimits(DateOnly asOf) => Resolve(FederalSeries, asOf).Limits;

    private static ExemptionEntry Entry(JsonNode? value, string where)
    {
        if (value is not JsonObject raw)
        {
            throw Fail(where, "entry is not an object");
        }

        var entryId = StrField(raw, "entry_id", where);
        where = $"{where} entry {entryId}";
        if (!Core.Wire.TryParse<ExemptionCategory>(Core.Json.AsString(raw["category"]), out var category))
        {
            throw Fail(where, $"{Py.Repr(raw["category"])} is not a valid Category");
        }

        var verification = ParseVerification(raw["verification"], where);
        var unlimited = raw["unlimited"] is JsonValue flag && flag.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            ? flag.GetValue<bool>()
            : throw Fail(where, "unlimited must be a boolean");
        var carryFrom = raw["wildcard_carryover_from"];
        if (carryFrom is not null && Core.Json.AsString(carryFrom) is null)
        {
            throw Fail(where, "wildcard_carryover_from must be a string or null");
        }

        var notes = Notes(raw, where);
        return new ExemptionEntry(
            EntryId: entryId,
            Category: category,
            Description: StrField(raw, "description", where),
            Citation: StrField(raw, "citation", where),
            Amount: MoneyOrNone(raw["amount"], where, "amount"),
            Unlimited: unlimited,
            Verification: verification,
            Sources: Sources(raw["sources"], where),
            PerItemAmount: MoneyOrNone(raw["per_item_amount"], where, "per_item_amount"),
            JointAmount: MoneyOrNone(raw["joint_amount"], where, "joint_amount"),
            WildcardCarryoverFrom: Core.Json.AsString(carryFrom),
            WildcardCarryoverCap: MoneyOrNone(raw["wildcard_carryover_cap"], where, "wildcard_carryover_cap"),
            FigureSince: DateOrNone(raw["figure_since"], where, "figure_since"),
            Notes: notes);
    }

    private static StatutoryLimit Limit(JsonNode? value, string where)
    {
        if (value is not JsonObject raw)
        {
            throw Fail(where, "limit is not an object");
        }

        var limitId = StrField(raw, "limit_id", where);
        where = $"{where} limit {limitId}";
        var amount = MoneyOrNone(raw["amount"], where, "amount");
        var effective = DateOrNone(raw["effective_date"], where, "effective_date");
        if (amount is null || effective is null)
        {
            throw Fail(where, "amount and effective_date are required");
        }

        var verification = ParseVerification(raw["verification"], where);
        var notes = Notes(raw, where);
        return new StatutoryLimit(
            LimitId: limitId,
            Citation: StrField(raw, "citation", where),
            Description: StrField(raw, "description", where),
            Amount: amount,
            EffectiveDate: effective.Value,
            NextAdjustment: DateOrNone(raw["next_adjustment"], where, "next_adjustment"),
            Verification: verification,
            Sources: Sources(raw["sources"], where),
            Notes: notes);
    }

    private static ExemptionRelease LoadRelease(RegistryTree root, string directory, string name, string seriesId)
    {
        var where = $"{seriesId}/{name}";
        var (effective, sequence) = ParseDirectoryName(name, where);
        var manifest = LoadJson(root, $"{directory}/manifest.json", where);
        var source = ManifestSource(manifest, seriesId, effective, sequence, where);
        var sha256 = Sha256(source, where);
        var manifestNotes = ManifestNotes(manifest, where);

        var payload = LoadJson(root, $"{directory}/scheme.json", where);
        if (payload["entries"] is not JsonArray entriesRaw || entriesRaw.Count == 0)
        {
            throw Fail(where, "payload entries missing or empty");
        }

        var limitsRaw = payload.TryGetPropertyValue("limits", out var limitsNode) ? limitsNode : new JsonArray();
        if (limitsRaw is not JsonArray limitsList)
        {
            throw Fail(where, "payload limits must be a list");
        }

        var optedOutNode = payload["opted_out_of_federal"];
        bool? optedOut = optedOutNode is null ? null : Core.Json.AsBool(optedOutNode) ?? throw Fail(where, "opted_out_of_federal must be a boolean or null");
        var citationNode = payload["opt_out_citation"];
        var optOutCitation = citationNode is null ? null : Core.Json.AsString(citationNode) ?? throw Fail(where, "opt_out_citation must be a string or null");
        var schemeNotes = Notes(payload, where, "payload notes must be a string");

        var scheme = new ExemptionScheme(
            SchemeId: StrField(payload, "scheme_id", where),
            Jurisdiction: StrField(payload, "jurisdiction", where),
            Name: StrField(payload, "name", where),
            OptedOutOfFederal: optedOut,
            OptOutCitation: optOutCitation,
            Entries: [.. entriesRaw.Select(raw => Entry(raw, where))],
            Notes: schemeNotes);
        return new ExemptionRelease(
            SeriesId: seriesId,
            EffectiveDate: effective,
            Sequence: sequence,
            SourceUrl: StrField(source, "url", where),
            SourcePublished: DateOrNone(source["published"], where, "source.published"),
            SourceSha256: sha256,
            Notes: manifestNotes,
            Scheme: scheme,
            Limits: [.. limitsList.Select(raw => Limit(raw, where))]);
    }
}
