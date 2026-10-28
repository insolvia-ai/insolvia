using System.Xml.Linq;

namespace Insolvia.Architecture.Tests;

// The successor of services/api's test_architecture.py (ADR 0028, § Repository
// layout). The rule, in one place:
//
//   Core              references nothing in the solution
//   Engines           references Core only (pure: no I/O, no AWS, no EF)
//   Forms             references Core and Engines (pure) — the form
//                     projections live in Engines, because the calculation
//                     engines and the projections import each other (the
//                     Chapter 13 plan reads Schedules I/J, B113 runs the
//                     plan), and the PDF engine fills what they project;
//                     an amendment to ADR 0028's layout made in 18.8 (#519)
//   Data, Aws         reference Core only
//   hosts             may reference any library; never another host
//
// Only hosts reference Data or Aws, which follows from the lines above: no
// library is allowed them. The filing worker shares the API's approval and
// digest code through Forms and Core, never through Api, which is what the
// last line enforces.
public sealed class LayeringTests
{
    private static readonly string[] Hosts = ["Api", "Worker", "Admin", "Mcp", "Filing", "Migrator"];

    private static readonly Dictionary<string, string[]> LibraryAllowed = new(StringComparer.Ordinal)
    {
        ["Core"] = [],
        ["Engines"] = ["Core"],
        ["Forms"] = ["Core", "Engines"],
        ["Data"] = ["Core"],
        ["Aws"] = ["Core"],
    };

    // Packages that make a project impure. Core, Engines and Forms are the
    // domain: no cloud SDK, no ORM, no database driver.
    private static readonly string[] ImpurePackagePrefixes =
        ["AWSSDK.", "Amazon.", "Microsoft.EntityFrameworkCore", "Npgsql"];

    private static readonly string[] PureProjects = ["Core", "Engines", "Forms"];

    private static readonly string[] ProjectDirectories = ["src", "tests"];

    private static readonly string BackendRoot = FindBackendRoot();

    public static TheoryData<string> SourceProjects()
    {
        var data = new TheoryData<string>();
        foreach (var name in SourceProjectNames())
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void Every_source_project_is_named_by_the_rule()
    {
        var known = LibraryAllowed.Keys.Concat(Hosts).Order(StringComparer.Ordinal);

        Assert.Equal(known, SourceProjectNames().Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void A_project_references_only_what_its_layer_allows(string project)
    {
        var references = ProjectReferences(project);

        if (LibraryAllowed.TryGetValue(project, out var allowed))
        {
            Assert.All(references, reference => Assert.Contains(reference, allowed));
        }
        else
        {
            Assert.Contains(project, Hosts);
            Assert.All(references, reference => Assert.DoesNotContain(reference, Hosts));
        }
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Only_hosts_reference_Data_or_Aws(string project)
    {
        if (Hosts.Contains(project))
        {
            return;
        }

        var references = ProjectReferences(project);

        Assert.DoesNotContain("Data", references);
        Assert.DoesNotContain("Aws", references);
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void The_domain_takes_no_cloud_or_database_package(string project)
    {
        if (!PureProjects.Contains(project))
        {
            return;
        }

        var packages = Elements(SourceProjectFile(project), "PackageReference");

        Assert.All(packages, package => Assert.DoesNotContain(
            ImpurePackagePrefixes,
            prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Only_the_worker_takes_the_model_sdk(string project)
    {
        // ADR 0015/0019: the model SDK is the pipeline worker's weight; the
        // API image (and every other) never grows it.
        if (project == "Worker")
        {
            return;
        }

        Assert.DoesNotContain("Anthropic", Elements(SourceProjectFile(project), "PackageReference"), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Every_source_project_has_its_own_test_project(string project)
    {
        var testProject = Path.Combine(BackendRoot, "tests", $"Insolvia.{project}.Tests", $"Insolvia.{project}.Tests.csproj");

        Assert.True(File.Exists(testProject), $"missing {Path.GetRelativePath(BackendRoot, testProject)}");
        Assert.Contains(
            $"Insolvia.{project}",
            Elements(testProject, "ProjectReference").Select(path => Path.GetFileNameWithoutExtension(path)));
    }

    [Fact]
    public void The_solution_lists_every_project()
    {
        var listed = XDocument.Load(Path.Combine(BackendRoot, "Insolvia.slnx"))
            .Descendants("Project")
            .Select(project => Normalise((string)project.Attribute("Path")!))
            .Order(StringComparer.Ordinal);

        var onDisk = ProjectDirectories
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(BackendRoot, dir), "*.csproj", SearchOption.AllDirectories))
            .Select(path => Normalise(Path.GetRelativePath(BackendRoot, path)))
            .Order(StringComparer.Ordinal);

        Assert.Equal(onDisk, listed);
    }

    private static IEnumerable<string> SourceProjectNames() =>
        Directory.EnumerateDirectories(Path.Combine(BackendRoot, "src"), "Insolvia.*")
            .Select(dir => Path.GetFileName(dir)["Insolvia.".Length..]);

    private static string SourceProjectFile(string project) =>
        Path.Combine(BackendRoot, "src", $"Insolvia.{project}", $"Insolvia.{project}.csproj");

    private static string[] ProjectReferences(string project) =>
        Elements(SourceProjectFile(project), "ProjectReference")
            .Select(path => Path.GetFileNameWithoutExtension(path)["Insolvia.".Length..])
            .ToArray();

    private static IEnumerable<string> Elements(string projectFile, string element) =>
        XDocument.Load(projectFile)
            .Descendants(element)
            .Select(item => (string)item.Attribute("Include")!);

    private static string Normalise(string path) => path.Replace('\\', '/');

    private static string FindBackendRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Insolvia.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No Insolvia.slnx above {AppContext.BaseDirectory}.");
    }
}
