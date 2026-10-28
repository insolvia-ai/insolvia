using System.Collections.Concurrent;
using System.Security.Cryptography;
using Insolvia.Engines;
using Insolvia.Engines.Projections;

namespace Insolvia.Forms;

/// <summary>
/// The template-PDF half of the form registry (from <c>core/form_templates.py</c>):
/// each release's <c>template.pdf</c>, vendored verbatim from uscourts.gov
/// and embedded in this assembly (ADR 0014). A template whose sha256 is not
/// the manifest's <c>source.sha256</c> is refused — the committed file is
/// then not the official PDF.
/// </summary>
public static class TemplatePdfs
{
    private const string ResourcePrefix = "Insolvia.Forms.Templates/";
    private static readonly ConcurrentDictionary<string, byte[]> Loaded = new(StringComparer.Ordinal);

    /// <summary>The release's official template bytes (a copy), hash-checked against its manifest.</summary>
    public static byte[] For(FormRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var bytes = Loaded.GetOrAdd(release.ReleaseId, _ => Load(release));
        return (byte[])bytes.Clone();
    }

    /// <summary>The embedded path of a release's template.</summary>
    public static string PathOf(FormRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return $"{release.SeriesId}/{release.Pin}/template.pdf";
    }

    /// <summary>Every embedded template, by path (<c>form/b101/2024-06-22/template.pdf</c>).</summary>
    public static IReadOnlyList<string> EmbeddedPaths() =>
        [.. typeof(TemplatePdfs).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..].Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>One embedded template's bytes, by path.</summary>
    public static byte[] Read(string path)
    {
        using var stream = typeof(TemplatePdfs).Assembly.GetManifestResourceStream(ResourcePrefix + path)
            ?? throw new MalformedReleaseException($"malformed form release {path}: template.pdf missing");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] Load(FormRelease release)
    {
        var where = $"{release.SeriesId}/{release.Pin}";
        var bytes = Read(PathOf(release));
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (actual != release.SourceSha256)
        {
            throw new MalformedReleaseException(
                $"malformed form release {where}: template.pdf sha256 {actual} != manifest source.sha256 {release.SourceSha256} — the committed template is not the official PDF");
        }

        return bytes;
    }
}
