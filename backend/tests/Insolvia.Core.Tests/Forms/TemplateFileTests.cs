using System.Security.Cryptography;
using Insolvia.Engines.Projections;

namespace Insolvia.Forms.Tests;

// The template-PDF half of test_form_templates.py: every release ships its
// template, and it hashes to the manifest's source.sha256 — the court's own
// PDF, byte for byte. Templates/ is the canonical copy (ADR 0014).
public sealed class TemplateFileTests
{
    public static TheoryData<string> Releases() =>
        [.. FormTemplates.Registry.Values.SelectMany(releases => releases).Select(release => release.ReleaseId)];

    private static FormRelease Release(string releaseId) =>
        FormTemplates.Registry.Values.SelectMany(releases => releases).Single(release => release.ReleaseId == releaseId);

    [Theory]
    [MemberData(nameof(Releases))]
    public void Every_release_ships_its_official_template(string releaseId)
    {
        var release = Release(releaseId);

        Assert.Equal(release.SourceSha256, Convert.ToHexStringLower(SHA256.HashData(TemplatePdfs.For(release))));
    }

    [Fact]
    public void Every_embedded_template_belongs_to_a_release()
    {
        var expected = FormTemplates.Registry.Values.SelectMany(releases => releases).Select(TemplatePdfs.PathOf).Order(StringComparer.Ordinal);

        Assert.Equal(expected, TemplatePdfs.EmbeddedPaths());
    }

    [Fact]
    public void A_template_whose_hash_disagrees_with_its_manifest_is_refused()
    {
        var release = FormTemplates.Latest("form/b101") with { SourceSha256 = new string('0', 64) };

        var error = Assert.Throws<Engines.MalformedReleaseException>(() => TemplatePdfs.For(release));

        Assert.Contains("is not the official PDF", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_is_handed_out_so_a_caller_cannot_corrupt_the_cache()
    {
        var release = FormTemplates.Latest("form/b2010");
        var first = TemplatePdfs.For(release);
        first[0] = 0;

        Assert.Equal((byte)'%', TemplatePdfs.For(release)[0]);
    }
}
