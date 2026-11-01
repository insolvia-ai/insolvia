using System.Text.Json.Nodes;

namespace Insolvia.Forms.Tests.Specs;

// The `Form specs` check (forms-pr.yml, forms/scripts/dev-test.sh): every
// committed forms/specs/*.json against its forms/acroform/*.json dump, read
// from the checkout itself rather than copied into bin/, so a deleted or
// renamed file is seen as deleted and never as a stale copy.
public sealed class FormSpecTests(ITestOutputHelper output)
{
    private static readonly string FormsDirectory = FindFormsDirectory();
    private static readonly string SpecsDirectory = Path.Combine(FormsDirectory, "specs");
    private static readonly string DumpsDirectory = Path.Combine(FormsDirectory, "acroform");

    public static TheoryData<string> Forms { get; } = [.. Stems(SpecsDirectory)];

    [Theory]
    [MemberData(nameof(Forms))]
    public void The_committed_spec_passes_every_check(string form)
    {
        var dumpPath = Path.Combine(DumpsDirectory, $"{form}.json");
        var result = FormSpecChecker.Check(
            form,
            JsonNode.Parse(File.ReadAllText(Path.Combine(SpecsDirectory, $"{form}.json"))),
            File.Exists(dumpPath) ? JsonNode.Parse(File.ReadAllText(dumpPath)) : null);

        output.WriteLine($"{form}: {result.SpecFields} spec fields covering {result.Covered} PDF fields ({result.EntityMapped} entity-mapped)");
        Assert.True(result.Errors.Count == 0, $"{result.Errors.Count} error(s):\n  " + string.Join("\n  ", result.Errors));
    }

    [Fact]
    public void Every_AcroForm_dump_has_a_spec()
    {
        Assert.Empty(FormSpecChecker.DumpsWithoutSpec(Stems(SpecsDirectory), Stems(DumpsDirectory)));
    }

    [Fact]
    public void The_specs_directory_is_found_and_not_empty()
    {
        // A theory with no rows passes vacuously; this is what stops a moved
        // directory from turning the whole check green.
        Assert.NotEmpty(Forms);
    }

    private static IEnumerable<string> Stems(string directory) =>
        Directory.EnumerateFiles(directory, "*.json").Select(path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.Ordinal);

    private static string FindFormsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "forms", "specs");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "forms");
            }
        }
        throw new DirectoryNotFoundException($"no forms/specs above {AppContext.BaseDirectory}");
    }
}
