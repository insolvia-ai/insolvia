using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data.AccessLogging;
using Insolvia.Data.Cases;
using Insolvia.Data.Firms;
using Insolvia.Mcp.Tests.Support;
using Insolvia.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Insolvia.Mcp.Tests;

/// <summary>
/// Which stores each shape of the host composes (mcp_lambda.py and
/// development_server.py): Postgres inside Lambda, always; Postgres locally
/// when this machine names a database; otherwise the memory twins, wholesale.
/// </summary>
public sealed class CompositionTests
{
    // Never connected to: composing a store opens nothing.
    private const string Database = "Host=127.0.0.1;Port=1;Database=insolvia;Username=mcp;Password=unused";

    public static TheoryData<string> PostgresShapes() => ["insolvia-staging-mcp", ""];

    [Theory]
    [MemberData(nameof(PostgresShapes))]
    public void With_a_database_every_port_is_a_postgres_store_scoped_to_the_request(string lambdaFunction)
    {
        using var host = new McpHost(new Dictionary<string, string?>
        {
            ["AWS_LAMBDA_FUNCTION_NAME"] = lambdaFunction,
            ["INSOLVIA_DB_CONNECTION_STRING"] = Database,
        });
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.IsType<PostgresFirmStore>(services.GetRequiredService<IFirmStore>());
        Assert.IsType<PostgresAccessLog>(services.GetRequiredService<IAccessLog>());
        Assert.IsType<PostgresCaseStore>(services.GetRequiredService<ICaseStore>());
        Assert.IsType<PostgresDebtorStore>(services.GetRequiredService<IDebtorStore>());
        Assert.IsType<PostgresCaseEntityStore>(services.GetRequiredService<ICaseEntityStore>());
        Assert.IsType<PostgresDocumentStore>(services.GetRequiredService<IDocumentStore>());
        Assert.IsType<PostgresCandidateStore>(services.GetRequiredService<ICandidateStore>());
        Assert.NotNull(services.GetRequiredService<CaseTools>());

        using var another = host.Services.CreateScope();
        Assert.NotSame(services.GetRequiredService<ICaseStore>(), another.ServiceProvider.GetRequiredService<ICaseStore>());
    }

    [Fact]
    public void Without_one_the_twins_share_one_state_as_the_tables_do()
    {
        using var host = new McpHost();
        var services = host.Services;
        var cases = Assert.IsType<MemoryCaseStore>(services.GetRequiredService<ICaseStore>());
        Assert.Same(cases.Debtors, services.GetRequiredService<IDebtorStore>());
        Assert.IsType<MemoryFirmStore>(services.GetRequiredService<IFirmStore>());
        Assert.IsType<MemoryAccessLog>(services.GetRequiredService<IAccessLog>());
        Assert.IsType<MemoryCaseEntityStore>(services.GetRequiredService<ICaseEntityStore>());
        Assert.IsType<MemoryDocumentStore>(services.GetRequiredService<IDocumentStore>());
        Assert.IsType<MemoryCandidateStore>(services.GetRequiredService<ICandidateStore>());
    }
}
