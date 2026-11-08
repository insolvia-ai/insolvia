using System.Xml.Linq;

namespace Insolvia.Domain.Tests.Architecture;

// The successor of services/api's test_architecture.py (ADR 0028, § Repository
// layout, amended by ADR 0030). Three layers, each project named for its role,
// never for a vendor. The rule, in one place:
//
//   Domain            the business logic layer: entities, rules, ports, the
//                     calculation engines (Engines/) and the PDF engine
//                     (Forms/). References nothing in the solution; pure: no
//                     I/O, no cloud SDK, no EF
//   Data              the data access layer: EF Core, the migrations and the
//                     Postgres stores. References Domain only
//   Platform          the platform services the app runs on — document
//                     storage, sealing, queues, the user directory, mail.
//                     References Domain only
//   hosts             the API layer (and the workers): may reference any
//                     library; never another host
//   tools             reference the pure library only (Domain); run by a
//                     developer or CI, never deployed, so never Data,
//                     Platform or a host — UstTool writes the regulatory
//                     registry and validates it through the engines (#585)
//
// Only hosts reference Data or Platform, which follows from the lines above:
// no library is allowed them. The filing worker shares the API's approval and
// digest code through Domain, never through Api, which is what the hosts line
// enforces.
public sealed class LayeringTests
{
    private static readonly string[] Hosts = ["Api", "Worker", "Admin", "Mcp", "Filing", "Migrator"];

    private static readonly Dictionary<string, string[]> ToolAllowed = new(StringComparer.Ordinal)
    {
        ["UstTool"] = ["Domain"],
    };

    private static readonly Dictionary<string, string[]> LibraryAllowed = new(StringComparer.Ordinal)
    {
        ["Domain"] = [],
        ["Data"] = ["Domain"],
        ["Platform"] = ["Domain"],
    };

    // Where each source project's tests live: its own test project, except
    // the migrator's, which share the data access layer's Postgres container.
    private static readonly Dictionary<string, string> TestProjectOf = new(StringComparer.Ordinal)
    {
        ["Domain"] = "Domain",
        ["Data"] = "Data",
        ["Platform"] = "Platform",
        ["Api"] = "Api",
        ["Worker"] = "Worker",
        ["Admin"] = "Admin",
        ["Mcp"] = "Mcp",
        ["Filing"] = "Filing",
        ["Migrator"] = "Data",
        ["UstTool"] = "UstTool",
    };

    // Packages that make a project impure. Domain is the business logic: no
    // cloud SDK, no ORM, no database driver.
    private static readonly string[] ImpurePackagePrefixes =
        ["AWSSDK.", "Amazon.", "Microsoft.EntityFrameworkCore", "Npgsql"];

    private static readonly string[] PureProjects = ["Domain"];

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
        var known = LibraryAllowed.Keys.Concat(Hosts).Concat(ToolAllowed.Keys).Order(StringComparer.Ordinal);

        Assert.Equal(known, SourceProjectNames().Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void A_project_references_only_what_its_layer_allows(string project)
    {
        var references = ProjectReferences(project);

        if (LibraryAllowed.TryGetValue(project, out var allowed) || ToolAllowed.TryGetValue(project, out allowed))
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
    public void Only_hosts_reference_Data_or_Platform(string project)
    {
        if (Hosts.Contains(project))
        {
            return;
        }

        var references = ProjectReferences(project);

        Assert.DoesNotContain("Data", references);
        Assert.DoesNotContain("Platform", references);
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
    public void Every_source_project_has_a_test_project(string project)
    {
        Assert.True(TestProjectOf.TryGetValue(project, out var tests), $"{project} is not in TestProjectOf");
        var testProject = Path.Combine(BackendRoot, "tests", $"Insolvia.{tests}.Tests", $"Insolvia.{tests}.Tests.csproj");

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
