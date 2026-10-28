using System.Security.Cryptography;
using Insolvia.Engines.Regulatory;

namespace Insolvia.Engines.Tests;

// The regulatory files this assembly embeds are copies of services/api's
// registry (ADR 0014: they ship in the artifact). services/api is frozen
// (ADR 0028) and deleted by 18.23; until then the two copies must not drift,
// so every embedded file is pinned, by sha256, to the file it was copied
// from — and the set of files must be the same set (the template PDFs and
// the filing/ series excepted: those ship with Insolvia.Forms, 18.16).
public sealed class RegulatoryFilesTests
{
    [Fact]
    public void Every_embedded_file_hashes_to_the_python_registrys_copy()
    {
        var python = PythonRegistryRoot();
        if (python is null)
        {
            return;
        }

        var embedded = RegistryTree.EmbeddedBytes();
        var onDisk = Directory.GetFiles(python, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(python, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith("filing/", StringComparison.Ordinal))
            .ToDictionary(path => path, path => Sha256(File.ReadAllBytes(Path.Combine(python, path))), StringComparer.Ordinal);

        Assert.Equal(onDisk.Keys.Order(StringComparer.Ordinal), embedded.Keys.Order(StringComparer.Ordinal));
        Assert.All(onDisk, pair => Assert.Equal(pair.Value, Sha256(embedded[pair.Key])));
    }

    [Fact]
    public void The_embedded_tree_holds_every_series_the_engines_read()
    {
        var series = RegistryTree.Shipped.ChildDirectories("code")
            .Select(name => $"code/{name}")
            .Concat(RegistryTree.Shipped.ChildDirectories("ust").Select(name => $"ust/{name}"))
            .Concat(RegistryTree.Shipped.ChildDirectories("exemptions").Select(name => $"exemptions/{name}"))
            .ToList();

        Assert.Equal(
            ["code/deadline-rules", "code/dollar-amounts", "code/legal-holidays", "ust/census-median-family-income", "ust/ch13-admin-multipliers", "ust/irs-local-standards", "ust/irs-national-standards", "exemptions/federal", "exemptions/fl", "exemptions/ga", "exemptions/tx"],
            series);
        Assert.Equal(22, RegistryTree.Shipped.ChildDirectories("form").Count);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string? PythonRegistryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "services", "api", "src", "insolvia_api", "regulatory");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
