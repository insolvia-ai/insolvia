using System.Text.Json.Nodes;
using Insolvia.UstTool.Tests.Support;

namespace Insolvia.UstTool.Tests;

// The ingester, end to end on the recordings: what it writes for a period
// already in the registry is the committed release, byte for byte — the
// same files the Python wrote.
public sealed class IngestTests
{
    private static IngestArgs Args(string period, string effective, string? msaFile = null) =>
        new(period, effective, 1, Recordings.Accessed, "unused", msaFile);

    [Fact]
    public async Task The_medians_recording_ingests_to_the_committed_release_byte_for_byte()
    {
        var release = await Ingest.MediansAsync(Args("20260401", "2026-04-01"), Recordings.Fetcher());

        AssertCommitted("census-median-family-income/2026-04-01", release);
    }

    [Fact]
    public async Task The_national_standards_recordings_ingest_to_the_committed_release_byte_for_byte()
    {
        var release = await Ingest.NationalAsync(Args("20260715", "2026-07-15"), Recordings.Fetcher());

        AssertCommitted("irs-national-standards/2026-07-15", release);
    }

    [Fact]
    public async Task The_ch13_recording_ingests_to_the_committed_release_byte_for_byte()
    {
        var release = await Ingest.Ch13MultipliersAsync(Args("20260715", "2026-07-15"), Recordings.Fetcher());

        AssertCommitted("ch13-admin-multipliers/2026-07-15", release);
    }

    [Fact]
    public async Task The_local_standards_ingest_to_the_committed_tables_for_every_county_the_trimmed_recording_keeps()
    {
        var release = await Ingest.LocalAsync(
            Args("20260715", "2026-07-15", Path.Combine(Recordings.FixturesDirectory, "20260715", "msa-counties.json")),
            Recordings.Fetcher(new Dictionary<string, string> { ["housing_util_standards_FIPS.xlsx"] = "housing_util_standards_FIPS.trimmed.xlsx" }));
        var committed = JsonNode.Parse(Recordings.Committed("ust/irs-local-standards/2026-07-15/dataset.json"))!.AsObject();

        // Transportation (the whole table), the scope and the prose: identical.
        foreach (var key in new[] { "kind", "verification", "sources", "states", "transportation", "notes" })
        {
            Assert.True(JsonNode.DeepEquals(committed[key], release.Dataset[key]), key);
        }

        // Housing: the kept counties, each identical to its committed row; AL dropped.
        var housing = release.Dataset["housing_utilities"]!.AsObject();
        Assert.Equal(["FL", "GA", "TX"], housing.Select(pair => pair.Key));
        foreach (var (state, rows) in housing)
        {
            var committedRows = committed["housing_utilities"]![state]!.AsArray();
            Assert.Equal(3, rows!.AsArray().Count);
            Assert.All(rows.AsArray(), row => Assert.Contains(committedRows, other => JsonNode.DeepEquals(other, row)));
        }

        // The manifest differs from the committed one only in the housing file's digest.
        var manifest = JsonNode.Parse(Recordings.Committed("ust/irs-local-standards/2026-07-15/manifest.json"))!.AsObject();
        manifest["source"]!["sha256"] = Ingest.Sha256(Recordings.Bytes("20260715", "housing_util_standards_FIPS.trimmed.xlsx"));
        Assert.Equal(PythonJson.Dumps(manifest), PythonJson.Dumps(release.Manifest));
    }

    [Fact]
    public async Task An_msa_the_spreadsheet_does_not_name_is_refused()
    {
        var msaFile = Path.Combine(Path.GetTempPath(), $"msa-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(msaFile, """{"Gotham": {"state": "FL", "counties": ["Wayne"]}}""", TestContext.Current.CancellationToken);
        try
        {
            var error = await Assert.ThrowsAsync<IngestException>(() => Ingest.LocalAsync(
                Args("20260715", "2026-07-15", msaFile),
                Recordings.Fetcher(new Dictionary<string, string> { ["housing_util_standards_FIPS.xlsx"] = "housing_util_standards_FIPS.trimmed.xlsx" })));

            Assert.Equal("msa-file names 'Gotham', not in the spreadsheet", error.Message);
        }
        finally
        {
            File.Delete(msaFile);
        }
    }

    [Fact]
    public async Task An_msa_outside_the_launch_states_is_refused()
    {
        var msaFile = Path.Combine(Path.GetTempPath(), $"msa-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(msaFile, """{"Atlanta": {"state": "AL", "counties": ["Fulton"]}}""", TestContext.Current.CancellationToken);
        try
        {
            var error = await Assert.ThrowsAsync<IngestException>(() => Ingest.LocalAsync(
                Args("20260715", "2026-07-15", msaFile),
                Recordings.Fetcher(new Dictionary<string, string> { ["housing_util_standards_FIPS.xlsx"] = "housing_util_standards_FIPS.trimmed.xlsx" })));

            Assert.Equal("msa-file 'Atlanta' is outside the launch states", error.Message);
        }
        finally
        {
            File.Delete(msaFile);
        }
    }

    [Fact]
    public async Task A_correction_is_written_as_the_next_sequence()
    {
        var release = await Ingest.MediansAsync(Args("20260401", "2026-04-01") with { Sequence = 2 }, Recordings.Fetcher());

        Assert.Equal("2026-04-01+2", release.DirectoryName);
        Assert.Equal(2, (int)release.Manifest["sequence"]!);
    }

    [Fact]
    public async Task Write_puts_a_valid_release_under_the_out_root()
    {
        using var root = new TempDirectory();
        var release = await Ingest.MediansAsync(Args("20260401", "2026-05-01"), Recordings.Fetcher());

        var written = Ingest.Write(root.Path, release);

        Assert.Equal(Path.Combine(root.Path, "ust/census-median-family-income", "2026-05-01"), written);
        Assert.Equal(PythonJson.Dumps(release.Dataset), File.ReadAllText(Path.Combine(written, "dataset.json")));
        Assert.Equal(PythonJson.Dumps(release.Manifest), File.ReadAllText(Path.Combine(written, "manifest.json")));
    }

    [Fact]
    public async Task Write_refuses_an_existing_release_directory()
    {
        using var root = new TempDirectory();
        var release = await Ingest.MediansAsync(Args("20260401", "2026-04-01"), Recordings.Fetcher());
        Directory.CreateDirectory(Path.Combine(root.Path, "ust/census-median-family-income/2026-04-01"));

        var error = Assert.Throws<IngestException>(() => Ingest.Write(root.Path, release));

        Assert.Contains("already exists — releases are append-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_refuses_what_the_engine_would_refuse_and_writes_nothing()
    {
        using var root = new TempDirectory();
        var release = await Ingest.MediansAsync(Args("20260401", "2026-05-01"), Recordings.Fetcher());
        release.Dataset["excess_person_annual_addition"] = "a lot";
        var error = Assert.Throws<IngestException>(() => Ingest.Write(root.Path, release));

        Assert.StartsWith("the engine refuses this release, so it was not written:", error.Message, StringComparison.Ordinal);
        Assert.Contains("excess_person_annual_addition", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "ust")));
    }

    [Fact]
    public async Task Write_validates_against_the_releases_already_on_disk()
    {
        using var root = new TempDirectory();
        var broken = Path.Combine(root.Path, "ust/irs-national-standards/2026-07-15");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, "manifest.json"), Recordings.Committed("ust/irs-national-standards/2026-07-15/manifest.json"), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(broken, "dataset.json"), "{}", TestContext.Current.CancellationToken);
        var release = await Ingest.MediansAsync(Args("20260401", "2026-05-01"), Recordings.Fetcher());

        var error = Assert.Throws<IngestException>(() => Ingest.Write(root.Path, release));

        Assert.Contains("ust/irs-national-standards", error.Message, StringComparison.Ordinal);
    }

    private static void AssertCommitted(string releasePath, UstReleaseFiles release)
    {
        Assert.Equal(Recordings.Committed($"ust/{releasePath}/manifest.json"), PythonJson.Dumps(release.Manifest));
        Assert.Equal(Recordings.Committed($"ust/{releasePath}/dataset.json"), PythonJson.Dumps(release.Dataset));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("ust-tool-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
