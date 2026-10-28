using System.Security.Cryptography;
using Insolvia.Engines.Projections;

namespace Insolvia.Forms.Tests;

// The template-PDF half of test_form_templates.py: every release ships its
// template, it hashes to the manifest's source.sha256, and every embedded
// copy is byte-identical to the Python original (frozen, ADR 0028) until
// 18.23 deletes it.
public sealed class TemplateFileTests
{
    private static readonly string PythonRegistry = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "services", "api", "src", "insolvia_api", "regulatory"));

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

    [Theory]
    [MemberData(nameof(Releases))]
    public void The_embedded_template_is_the_python_copy_byte_for_byte(string releaseId)
    {
        var path = TemplatePdfs.PathOf(Release(releaseId));

        Assert.Equal(File.ReadAllBytes(Path.Combine(PythonRegistry, path)), TemplatePdfs.Read(path));
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
