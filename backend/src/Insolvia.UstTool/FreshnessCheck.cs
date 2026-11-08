using System.Text;
using System.Text.RegularExpressions;
using Insolvia.Domain.Engines.Regulatory;

namespace Insolvia.UstTool;

/// <summary>
/// The freshness tripwire for the UST means-testing datasets (issue #99;
/// scripts/check-ust-data.py, ported). A release the authority has made
/// effective that we have not ingested means the product computes on stale
/// data — an incident, not a surprise. This asks justice.gov for the current
/// means-testing period, downloads that period's spreadsheets, and compares
/// their SHA-256 digests against what the committed registry ingested. Any
/// artifact the registry has never seen means a human must run
/// <c>ingest</c> and review the diff. It also watches the § 104(b) horizon:
/// when the recorded next adjustment of <c>code/dollar-amounts</c> is within
/// the warning window and no release covers it, that is the same incident
/// about to happen. The registry is read through the engine's own loaders,
/// so a registry the engine would refuse cannot pass.
/// Exit codes: 0 current · 1 stale · 2 could not verify (treated as an
/// alert too; silence is the only green).
/// </summary>
internal static partial class FreshnessCheck
{
    public const string MeansTestingUrl = "https://www.justice.gov/ust/means-testing";

    // Every spreadsheet a period publishes that some committed release
    // ingests, mapped to the series whose committed digests must include it.
    public static readonly IReadOnlyList<(string Name, string Series)> PeriodFiles =
    [
        ("median_income", UstData.MedianIncomeSeries),
        ("national_expense_standards", UstData.NationalStandardsSeries),
        ("national_oop_healthcare", UstData.NationalStandardsSeries),
        ("housing_util_standards_FIPS", UstData.LocalStandardsSeries),
        ("transportation_standards", UstData.LocalStandardsSeries),
        ("ch13_exp_mult", UstData.Ch13MultipliersSeries),
    ];

    // How far ahead of a recorded § 104 next_adjustment the alarm trips. Two
    // months is enough to ingest a Federal Register notice published ~Feb 1
    // for an Apr 1 effective date.
    public static readonly int Section104WarningDays = 60;

    [GeneratedRegex(@"\b[0-9a-f]{64}\b")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("value=\"/ust/means-testing/(\\d{8})\"")]
    private static partial Regex PeriodOption();

    /// <summary>The newest period in the means-testing page's case-filed dropdown.</summary>
    public static string LatestPeriod(string page)
    {
        var periods = PeriodOption().Matches(page).Select(match => match.Groups[1].Value).ToList();
        if (periods.Count == 0)
        {
            throw new InvalidOperationException(
                "no /ust/means-testing/YYYYMMDD periods found on the page — the page layout changed; update this tool's parser");
        }

        return periods.Max(StringComparer.Ordinal)!;
    }

    /// <summary>
    /// Every SHA-256 a series' committed releases record — the manifests'
    /// source.sha256 plus any digest their notes mention (the multi-file
    /// releases record secondary files' digests in notes).
    /// </summary>
    public static IReadOnlySet<string> CommittedDigests(IReadOnlyDictionary<string, IReadOnlyList<UstRelease>> registry, string series)
    {
        if (!registry.TryGetValue(series, out var releases))
        {
            throw new InvalidOperationException($"registry has no series directory {series}");
        }

        var digests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in releases)
        {
            if (release.SourceSha256 is { } sha)
            {
                digests.Add(sha.ToLowerInvariant());
            }

            foreach (Match found in Sha256Pattern().Matches(release.Notes))
            {
                digests.Add(found.Value);
            }
        }

        if (digests.Count == 0)
        {
            throw new InvalidOperationException($"{series}: no committed digests found");
        }

        return digests;
    }

    public static async Task<List<string>> CheckPeriodFilesAsync(
        string period, IReadOnlyDictionary<string, IReadOnlyList<UstRelease>> registry, Fetcher fetch, TextWriter output)
    {
        var problems = new List<string>();
        var digestsBySeries = PeriodFiles.Select(file => file.Series).Distinct()
            .ToDictionary(series => series, series => CommittedDigests(registry, series), StringComparer.Ordinal);
        foreach (var (name, series) in PeriodFiles)
        {
            var url = Ingest.DocUrl(period, name);
            var digest = Ingest.Sha256(await fetch(url).ConfigureAwait(false));
            if (digestsBySeries[series].Contains(digest))
            {
                await output.WriteLineAsync($"  current: {name}.xlsx ({period}) already ingested by {series}").ConfigureAwait(false);
            }
            else
            {
                problems.Add(
                    $"{series} has never ingested {url} (sha256 {digest}) — the UST's {period} period carries data the "
                    + "registry lacks. Run the UST tool's ingest command and review the diff; the notice on "
                    + "https://www.justice.gov/ust/means-testing states the effective date.");
            }
        }

        return problems;
    }

    /// <summary>Warn when the recorded next § 104 adjustment is near and no release covering it exists yet.</summary>
    public static List<string> CheckSection104Horizon(IReadOnlyList<DollarAmountsRelease> releases, DateOnly today, TextWriter output)
    {
        var adjustments = releases.SelectMany(release => release.Amounts)
            .Select(amount => amount.NextAdjustment)
            .OfType<DateOnly>()
            .Distinct()
            .Order();
        var problems = new List<string>();
        foreach (var adjustment in adjustments)
        {
            if (adjustment <= today.AddDays(Section104WarningDays) && !releases.Any(release => release.EffectiveDate >= adjustment))
            {
                problems.Add(
                    $"code/dollar-amounts records a § 104(b) adjustment effective {adjustment:yyyy-MM-dd} and no release "
                    + "covers it — ingest the new Federal Register notice (published ~February of the adjustment year) "
                    + "as the next release of the series.");
            }
            else
            {
                output.WriteLine($"  current: § 104 horizon {adjustment:yyyy-MM-dd} covered or far");
            }
        }

        return problems;
    }

    /// <summary>The whole check; returns the exit code.</summary>
    public static async Task<int> RunAsync(string registryRoot, DateOnly today, Fetcher fetch, TextWriter output, TextWriter error)
    {
        List<string> problems;
        try
        {
            var tree = DiskTree(registryRoot);
            var period = LatestPeriod(Encoding.UTF8.GetString(await fetch(MeansTestingUrl).ConfigureAwait(false)));
            await output.WriteLineAsync($"latest UST means-testing period: {period}").ConfigureAwait(false);
            problems = await CheckPeriodFilesAsync(period, UstData.LoadRegistry(tree), fetch, output).ConfigureAwait(false);
            problems.AddRange(CheckSection104Horizon(DollarAmounts.LoadRegistry(tree), today, output));
        }
#pragma warning disable CA1031 // Any failure to verify is exit 2, never a crash: the workflow alerts on it.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            await error.WriteLineAsync($"could not verify freshness: {failure.Message}").ConfigureAwait(false);
            return 2;
        }

        if (problems.Count > 0)
        {
            await error.WriteLineAsync($"\n{problems.Count} dataset(s) stale:").ConfigureAwait(false);
            foreach (var problem in problems)
            {
                await error.WriteLineAsync($"  {problem}").ConfigureAwait(false);
            }

            return 1;
        }

        await output.WriteLineAsync("OK — the registry matches the UST's current period").ConfigureAwait(false);
        return 0;
    }

    /// <summary>The registry as it is on disk — the checkout, not this build's embedded copy.</summary>
    public static RegistryTree DiskTree(string registryRoot)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(registryRoot, "*.json", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(registryRoot, path).Replace('\\', '/')] = File.ReadAllText(path);
        }

        return new RegistryTree(files);
    }
}
