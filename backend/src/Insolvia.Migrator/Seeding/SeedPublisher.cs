using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Insolvia.Migrator.Seeding;

/// <summary>
/// <c>seed publish</c>: one committed fixture version into the shared fixture
/// bucket, byte for byte (seed.py's <c>publish</c>). The direction of each
/// command:
/// <code>
/// seed            git fixture + fixture bucket  →  an environment  (local, staging)
/// seed-publish    git fixture                   →  the fixture bucket
/// </code>
/// Idempotent by digest; a local file that disagrees with its manifest is
/// refused before anything uploads, because the manifest is the contract the
/// loader verifies against.
/// </summary>
public static partial class SeedPublisher
{
    /// <summary>Refuses anything that is not the shared fixture bucket (insolvia-services' <c>dev_fixtures</c>).</summary>
    /// <param name="bucket">The bucket's name.</param>
    public static void RequireFixtureBucket(string? bucket)
    {
        if (!FixtureBucket().IsMatch(bucket ?? string.Empty))
        {
            throw new SeedRefusedException($"'{bucket}' is not the shared fixture bucket (expected insolvia-shared-dev-fixtures-<region>)");
        }
    }

    /// <summary>Publish <paramref name="folder"/>. Returns how many objects were (or, under check, would be) uploaded.</summary>
    /// <param name="folder">The version's folder, e.g. <c>seeds/fixtures/v4</c>.</param>
    /// <param name="objects">The fixture bucket.</param>
    /// <param name="check">Report only.</param>
    /// <param name="output">The report.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The number missing from the bucket.</returns>
    public static async Task<int> PublishAsync(string folder, IFixtureObjects objects, bool check, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(output);
        var version = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var (_, manifest) = SeedFixtures.Folder(folder);

        var missing = 0;
        foreach (var (key, node) in manifest["objects"] as JsonObject ?? [])
        {
            var local = Path.Combine(folder, key);
            if (!File.Exists(local))
            {
                throw new SeedRefusedException($"manifest lists {key} but {local} is absent");
            }

            var content = await File.ReadAllBytesAsync(local, cancellationToken).ConfigureAwait(false);
            var digest = Convert.ToHexStringLower(SHA256.HashData(content));
            if (digest != SeedJson.Text(node?["sha256"]) || content.Length != (node?["size"]?.GetValue<long>() ?? -1))
            {
                throw new SeedRefusedException($"{key} does not match manifest.json — regenerate the manifest rather than publishing bytes the loader will refuse");
            }

            if (await objects.DigestAsync($"{version}/{key}", cancellationToken).ConfigureAwait(false) == digest)
            {
                await output.WriteLineAsync($"{version}/{key}: up to date").ConfigureAwait(false);
                continue;
            }

            missing++;
            if (check)
            {
                await output.WriteLineAsync($"{version}/{key}: would upload").ConfigureAwait(false);
                continue;
            }

            await objects.PutAsync($"{version}/{key}", content, "application/octet-stream", cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"{version}/{key}: uploaded").ConfigureAwait(false);
        }

        if (!check)
        {
            foreach (var name in new[] { "cases.json", "manifest.json" })
            {
                await objects.PutAsync($"{version}/{name}", await File.ReadAllBytesAsync(Path.Combine(folder, name), cancellationToken).ConfigureAwait(false), "application/json", cancellationToken).ConfigureAwait(false);
            }
        }

        return missing;
    }

    [GeneratedRegex(@"^insolvia-shared-dev-fixtures-[a-z0-9-]+\z")]
    private static partial Regex FixtureBucket();
}
