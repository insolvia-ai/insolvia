using System.Globalization;
using System.Text.Json.Nodes;
using static Insolvia.Engines.Regulatory.ReleaseParsing;

namespace Insolvia.Engines.Regulatory;

/// <summary>
/// One named statutory figure — what a form floor or a means-test threshold
/// cites. <see cref="NextAdjustment"/> is null for figures § 104 never touches.
/// </summary>
public sealed record DollarAmount(
    string AmountId,
    string Citation,
    string Description,
    string Amount,
    Verification Verification,
    IReadOnlyList<Source> Sources,
    DateOnly? FigureSince,
    DateOnly? NextAdjustment,
    string Notes = "")
{
    /// <summary>The figure as a decimal (Python's <c>Decimal(self.amount)</c>).</summary>
    public decimal Value => decimal.Parse(Amount, CultureInfo.InvariantCulture);
}

/// <summary>One immutable release of <c>code/dollar-amounts</c>; its id is what <c>case.constants_set_id</c> stores.</summary>
public sealed record DollarAmountsRelease(
    string SeriesId,
    DateOnly EffectiveDate,
    int Sequence,
    string SourceUrl,
    DateOnly? SourcePublished,
    string? SourceSha256,
    string Notes,
    IReadOnlyList<DollarAmount> Amounts) : IRelease
{
    /// <summary><c>series@date[+sequence]</c>.</summary>
    public string ReleaseId => Releases.Id(SeriesId, EffectiveDate, Sequence);

    /// <summary>The named figure; <see cref="EngineKeyException"/> when the release has none.</summary>
    public DollarAmount Amount(string amountId) =>
        Amounts.FirstOrDefault(amount => amount.AmountId == amountId)
        ?? throw new EngineKeyException($"{ReleaseId} has no amount {Exact.Repr(amountId)}");
}

/// <summary>The statutory dollar amounts: load, validate, resolve (Python's <c>core/dollar_amounts.py</c>).</summary>
public static class DollarAmounts
{
    /// <summary>The next scheduled § 104(b) adjustment.</summary>
    public static readonly DateOnly NextSection104Adjustment = new(2028, 4, 1);

    /// <summary>The series id.</summary>
    public const string SeriesId = "code/dollar-amounts";

    private static readonly Lazy<IReadOnlyList<DollarAmountsRelease>> Shipped = new(() => LoadRegistry(RegistryTree.Shipped));

    /// <summary>The committed releases, oldest first.</summary>
    public static IReadOnlyList<DollarAmountsRelease> All => Shipped.Value;

    /// <summary>Loads and validates every release of the series under a registry root.</summary>
    public static IReadOnlyList<DollarAmountsRelease> LoadRegistry(RegistryTree root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var loaded = root.ChildDirectories("code/dollar-amounts")
            .Select(name => LoadRelease(root, $"code/dollar-amounts/{name}", name))
            .ToList();
        if (loaded.Count == 0)
        {
            throw Fail(SeriesId, "series has no releases");
        }

        return Releases.Ordered(loaded, SeriesId);
    }

    /// <summary>The release with the greatest effective date on or before <paramref name="asOf"/>.</summary>
    public static DollarAmountsRelease PickRelease(IReadOnlyList<DollarAmountsRelease> candidates, DateOnly asOf) =>
        Releases.Pick(candidates, asOf, "refusing to compute on figures that do not describe that date");

    /// <summary>The release in force on <paramref name="asOf"/>.</summary>
    public static DollarAmountsRelease Resolve(DateOnly asOf) => PickRelease(All, asOf);

    /// <summary>That exact release — must succeed forever for any id ever pinned.</summary>
    public static DollarAmountsRelease Get(string releaseId) =>
        All.FirstOrDefault(release => release.ReleaseId == releaseId)
        ?? throw new EngineKeyException($"series {Exact.Repr(SeriesId)} has no release {Exact.Repr(releaseId)}");

    /// <summary>Newest by (effective date, sequence).</summary>
    public static DollarAmountsRelease Latest() => All[^1];

    private static DollarAmount ParseAmount(JsonNode? value, string where)
    {
        if (value is not JsonObject raw)
        {
            throw Fail(where, "amount is not an object");
        }

        var amountId = StrField(raw, "amount_id", where);
        where = $"{where} amount {amountId}";
        var figure = Money(raw["amount"], where, "amount");
        var verification = ParseVerification(raw["verification"], where);
        if (verification == Verification.Unverified)
        {
            throw Fail(where, "a statutory dollar amount may never be unverified");
        }

        var notes = Notes(raw, where);
        return new DollarAmount(
            AmountId: amountId,
            Citation: StrField(raw, "citation", where),
            Description: StrField(raw, "description", where),
            Amount: figure,
            Verification: verification,
            Sources: Sources(raw["sources"], where),
            FigureSince: DateOrNone(raw["figure_since"], where, "figure_since"),
            NextAdjustment: DateOrNone(raw["next_adjustment"], where, "next_adjustment"),
            Notes: notes);
    }

    private static DollarAmountsRelease LoadRelease(RegistryTree root, string directory, string name)
    {
        var where = $"{SeriesId}/{name}";
        var (effective, sequence) = ParseDirectoryName(name, where);
        var manifest = LoadJson(root, $"{directory}/manifest.json", where);
        var source = ManifestSource(manifest, SeriesId, effective, sequence, where);
        var sha256 = Sha256(source, where);
        var manifestNotes = ManifestNotes(manifest, where);

        var payload = LoadJson(root, $"{directory}/amounts.json", where);
        if (payload["amounts"] is not JsonArray amountsRaw || amountsRaw.Count == 0)
        {
            throw Fail(where, "payload amounts missing or empty");
        }

        var amounts = amountsRaw.Select(raw => ParseAmount(raw, where)).ToList();
        if (amounts.Select(amount => amount.AmountId).Distinct(StringComparer.Ordinal).Count() != amounts.Count)
        {
            throw Fail(where, "duplicate amount ids");
        }

        return new DollarAmountsRelease(
            SeriesId: SeriesId,
            EffectiveDate: effective,
            Sequence: sequence,
            SourceUrl: StrField(source, "url", where),
            SourcePublished: DateOrNone(source["published"], where, "source.published"),
            SourceSha256: sha256,
            Notes: manifestNotes,
            Amounts: amounts);
    }
}
