using System.Reflection;
using System.Text.Json.Nodes;
using Insolvia.Core.Debtors;
using Insolvia.Core.Ports;
using Insolvia.Mcp.Tests.Support;
using Insolvia.Mcp.Tools;
using static Insolvia.Mcp.Tests.Support.World;

namespace Insolvia.Mcp.Tests;

/// <summary>
/// services/mcp/tests/unit/test_architecture.py's rule — the tools' meaning
/// depends on nothing protocol- or infrastructure-shaped — plus the invariant
/// mcp-surface.md makes structural: the ONLY writes this surface can make are
/// candidates and access-log rows. Never a case record.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] Forbidden = ["ModelContextProtocol", "Insolvia.Data", "Insolvia.Aws", "Microsoft.AspNetCore", "Amazon."];

    private static readonly string[] WriteVerbs =
        ["Create", "Put", "Update", "Delete", "Add", "Remove", "Link", "Assign", "Unassign", "Record", "Set", "Mark", "Claim", "Finish", "Release"];

    [Theory]
    [InlineData("Tools/CaseTools.cs")]
    [InlineData("Views/WireViews.cs")]
    [InlineData("Views/PyJson.cs")]
    public void The_tools_meaning_imports_no_protocol_and_no_adapter(string file)
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), file));
        var usings = source.Split('\n').Where(line => line.StartsWith("using ", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(usings, line => Forbidden.Any(prefix => line.Contains(prefix, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Every_tool_writes_only_candidates_and_access_log_rows()
    {
        var stores = new Stores();
        await stores.SeededAsync();
        var found = await SeedCaseAsync(stores.Cases);
        var entity = await CreditorAsync(stores.Entities, found.Id);
        var client = await MakeFirmClientAsync(stores.Firms);
        await LinkClientAsync(stores.Debtors, client, found, FilingRole.Debtor1);

        var writes = new List<string>();
        var tools = new CaseTools(
            Guarded<ICaseStore>(stores.Cases, writes),
            Guarded<ICaseEntityStore>(stores.Entities, writes),
            Guarded<IDebtorStore>(stores.Debtors, writes),
            Guarded<IDocumentStore>(stores.Documents, writes),
            stores.Candidates,
            stores.AccessLog,
            Guarded<IFirmStore>(stores.Firms, writes));
        var admin = MakeAccessor();

        _ = CaseTools.Whoami(admin);
        await tools.ListCasesAsync(admin, cancellationToken: Ct);
        await tools.GetCaseAsync(admin, found.Id, Ct);
        foreach (var entityType in CaseTools.EntityTypes)
        {
            await tools.ListCaseRecordsAsync(admin, found.Id, entityType, cancellationToken: Ct);
        }

        await tools.GetCaseRecordAsync(admin, found.Id, "creditors", entity.Id, Ct);
        await tools.GetCaseRecordAsync(admin, found.Id, "clients", client.Id, Ct);
        var proposed = await tools.ProposeCaseRecordsAsync(
            admin,
            found.Id,
            new JsonArray(
                new JsonObject { ["entityType"] = "creditors", ["payload"] = new JsonObject { ["name"] = "Example Bank" } },
                new JsonObject { ["entityType"] = "debtors", ["payload"] = new JsonObject { ["name"] = new JsonObject { ["given"] = "Pat" } } }),
            ClientId,
            Ct);
        await tools.CheckProposalsAsync(admin, found.Id, cancellationToken: Ct);
        await tools.WithdrawProposalAsync(admin, found.Id, (string)proposed["candidates"]![0]!["candidateId"]!, Ct);

        Assert.Empty(writes);
        Assert.Equal(2, (await stores.Candidates.ListForCaseAsync(found.Id, Ct)).Count);
        Assert.Single(await stores.Entities.ListForCaseAsync(found.Id, Core.CaseEntities.CaseCollections.ByCollection["creditors"], Ct));
    }

    private static T Guarded<T>(T inner, List<string> writes)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, WriteGuard>();
        var guard = (WriteGuard)(object)proxy;
        guard.Inner = inner;
        guard.Writes = writes;
        guard.Verbs = WriteVerbs;
        return proxy;
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Insolvia.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "src", "Insolvia.Mcp");
    }

    /// <summary>Forwards reads; records (and refuses) any method named like a write.</summary>
    public class WriteGuard : DispatchProxy
    {
        internal object Inner { get; set; } = null!;

        internal List<string> Writes { get; set; } = null!;

        internal string[] Verbs { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (Verbs.Any(verb => targetMethod.Name.StartsWith(verb, StringComparison.Ordinal)))
            {
                Writes.Add($"{targetMethod.DeclaringType?.Name}.{targetMethod.Name}");
                throw new InvalidOperationException($"the MCP surface wrote through {targetMethod.Name}");
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                throw error.InnerException;
            }
        }
    }
}
