using System.Text;
using Insolvia.Engines.Regulatory;
using Insolvia.UstTool.Tests.Support;

namespace Insolvia.UstTool.Tests;

// The weekly tripwire, offline: a fake justice.gov serving the recordings,
// against a copy of the committed registry. Its live run is
// regulatory-refresh.yml's (and anyone's: the tool's check command).
public sealed class FreshnessCheckTests : IDisposable
{
    private const string Page = """
        <select><option value="/ust/means-testing/20260401">April 1, 2026</option>
        <option value="/ust/means-testing/20260715">July 15, 2026</option>
        <option value="/ust/means-testing/20251101">November 1, 2025</option></select>
        """;

    private static readonly DateOnly Today = new(2026, 11, 2);

    // The committed registry's ust/ and code/ trees, with one edit: the local
    // standards record the TRIMMED housing recording's digest, since that is
    // the file this fake site serves (the published one is 570 KB).
    private readonly string _registry = CopyRegistry();

    public void Dispose() => Directory.Delete(_registry, recursive: true);

    // justice.gov as recorded: the 20260715 period republishes the 20260401
    // medians file unchanged (the digest the registry holds).
    private static Fetcher Site(string? replacedName = null, byte[]? replacement = null) => url =>
    {
        if (url == FreshnessCheck.MeansTestingUrl)
        {
            return Task.FromResult(Encoding.UTF8.GetBytes(Page));
        }

        var name = url[(url.LastIndexOf('/') + 1)..];
        return Task.FromResult(name switch
        {
            _ when name == replacedName => replacement!,
            "median_income.xlsx" => Recordings.Bytes("20260401", name),
            "housing_util_standards_FIPS.xlsx" => Recordings.Bytes("20260715", "housing_util_standards_FIPS.trimmed.xlsx"),
            _ => Recordings.Bytes("20260715", name),
        });
    };

    [Fact]
    public void The_latest_period_is_the_greatest_in_the_dropdown()
    {
        Assert.Equal("20260715", FreshnessCheck.LatestPeriod(Page));
    }

    [Fact]
    public void A_page_with_no_periods_is_a_failure_to_verify()
    {
        var error = Assert.Throws<InvalidOperationException>(() => FreshnessCheck.LatestPeriod("<html>moved</html>"));

        Assert.StartsWith("no /ust/means-testing/YYYYMMDD periods found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_committed_digests_include_the_secondary_files_named_in_notes()
    {
        var digests = FreshnessCheck.CommittedDigests(UstData.Registry, UstData.NationalStandardsSeries);

        Assert.Contains(Ingest.Sha256(Recordings.Bytes("20260715", "national_expense_standards.xlsx")), digests);
        Assert.Contains(Ingest.Sha256(Recordings.Bytes("20260715", "national_oop_healthcare.xlsx")), digests);
    }

    [Fact]
    public async Task A_registry_holding_every_file_of_the_current_period_is_current_and_exits_0()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await FreshnessCheck.RunAsync(_registry, Today, Site(), output, error);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        Assert.Equal(
            """
            latest UST means-testing period: 20260715
              current: median_income.xlsx (20260715) already ingested by ust/census-median-family-income
              current: national_expense_standards.xlsx (20260715) already ingested by ust/irs-national-standards
              current: national_oop_healthcare.xlsx (20260715) already ingested by ust/irs-national-standards
              current: housing_util_standards_FIPS.xlsx (20260715) already ingested by ust/irs-local-standards
              current: transportation_standards.xlsx (20260715) already ingested by ust/irs-local-standards
              current: ch13_exp_mult.xlsx (20260715) already ingested by ust/ch13-admin-multipliers
              current: § 104 horizon 2028-04-01 covered or far
            OK — the registry matches the UST's current period

            """.ReplaceLineEndings("\n"),
            output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_file_the_registry_never_ingested_is_stale_and_exits_1()
    {
        var error = new StringWriter();
        var newBytes = Encoding.UTF8.GetBytes("a new period's spreadsheet");

        var code = await FreshnessCheck.RunAsync(_registry, Today, Site("ch13_exp_mult.xlsx", newBytes), new StringWriter(), error);

        Assert.Equal(1, code);
        Assert.Contains("1 dataset(s) stale:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            $"ust/ch13-admin-multipliers has never ingested https://www.justice.gov/ust/eo/bapcpa/20260715/bci_data/docs/ch13_exp_mult.xlsx (sha256 {Ingest.Sha256(newBytes)})",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_fetch_is_a_failure_to_verify_and_exits_2()
    {
        var error = new StringWriter();

        var code = await FreshnessCheck.RunAsync(_registry, Today, _ => throw new HttpRequestException("403 Forbidden"), new StringWriter(), error);

        Assert.Equal(2, code);
        Assert.Equal("could not verify freshness: 403 Forbidden", error.ToString().Trim());
    }

    [Fact]
    public async Task A_registry_the_engine_refuses_is_a_failure_to_verify()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_registry, "ust/census-median-family-income/2026-04-01/dataset.json"), "{}", TestContext.Current.CancellationToken);
        var error = new StringWriter();

        var code = await FreshnessCheck.RunAsync(_registry, Today, Site(), new StringWriter(), error);

        Assert.Equal(2, code);
        Assert.StartsWith("could not verify freshness:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_104_adjustment_inside_the_window_with_no_release_is_stale()
    {
        var releases = new[] { DollarRelease(new DateOnly(2025, 4, 1), next: new DateOnly(2028, 4, 1)) };

        Assert.Single(FreshnessCheck.CheckSection104Horizon(releases, new DateOnly(2028, 2, 1), TextWriter.Null));
        Assert.Empty(FreshnessCheck.CheckSection104Horizon(releases, new DateOnly(2028, 1, 31), TextWriter.Null));
    }

    [Fact]
    public void A_section_104_adjustment_a_release_covers_is_current()
    {
        var releases = new[]
        {
            DollarRelease(new DateOnly(2025, 4, 1), next: new DateOnly(2028, 4, 1)),
            DollarRelease(new DateOnly(2028, 4, 1), next: new DateOnly(2031, 4, 1)),
        };
        var output = new StringWriter();

        Assert.Empty(FreshnessCheck.CheckSection104Horizon(releases, new DateOnly(2028, 3, 1), output));
        Assert.Contains("  current: § 104 horizon 2028-04-01 covered or far", output.ToString(), StringComparison.Ordinal);
    }

    private static DollarAmountsRelease DollarRelease(DateOnly effective, DateOnly next) => new(
        "code/dollar-amounts", effective, 1, "https://example.invalid", null, null, "",
        [new DollarAmount("a", "11 U.S.C. 104", "a figure", "100.00", Verification.Primary, [], null, next)]);

    private static string CopyRegistry()
    {
        var root = Directory.CreateTempSubdirectory("ust-check-").FullName;
        foreach (var tree in new[] { "ust", "code" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(Recordings.RegistryRoot, tree), "*.json", SearchOption.AllDirectories))
            {
                var target = Path.Combine(root, Path.GetRelativePath(Recordings.RegistryRoot, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }
        }

        var manifest = Path.Combine(root, "ust/irs-local-standards/2026-07-15/manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(
            "8b6954b45b1036da3ef6f98459c9775e53c69991f1aede155e73ad0a30942dfd",
            Ingest.Sha256(Recordings.Bytes("20260715", "housing_util_standards_FIPS.trimmed.xlsx")),
            StringComparison.Ordinal));
        return root;
    }
}
