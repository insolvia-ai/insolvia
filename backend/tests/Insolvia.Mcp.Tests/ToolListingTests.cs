using System.Text.Json.Nodes;
using Insolvia.Mcp.Tests.Support;

namespace Insolvia.Mcp.Tests;

/// <summary>
/// The listing is a contract: a harness that cached the Python server's
/// tools/list must see the same eight tools from this one. Every member of
/// every tool — name, title, description, annotations, input and output
/// schema — is compared with the listing captured from services/mcp
/// (Fixtures/python-tools-list.json), as JSON, over the real wire.
/// </summary>
public sealed class ToolListingTests : IDisposable
{
    private readonly McpHost _host = new();

    public static TheoryData<string> Tools() => ["whoami", "list_cases", "get_case", "list_case_records", "get_case_record", "propose_case_records", "check_proposals", "withdraw_proposal"];

    public void Dispose() => _host.Dispose();

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task Each_tool_is_the_python_servers(string name)
    {
        await _host.SeededAsync();
        var listed = (await _host.RpcAsync("tools/list", headers: McpHost.Auth())).Result["tools"]!.AsArray();
        var ours = listed.Single(tool => (string?)tool!["name"] == name)!.AsObject();
        var theirs = Python().Single(tool => (string?)tool!["name"] == name)!.AsObject();

        foreach (var member in new[] { "name", "title", "description", "annotations", "inputSchema", "outputSchema" })
        {
            Assert.True(JsonNode.DeepEquals(theirs[member], ours[member]), $"{name}.{member}\nexpected: {theirs[member]?.ToJsonString()}\nactual:   {ours[member]?.ToJsonString()}");
        }

        // Nothing extra either (icons, execution, _meta …).
        Assert.Equal(theirs.Select(pair => pair.Key).Order(StringComparer.Ordinal), ours.Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_order_is_the_python_servers()
    {
        await _host.SeededAsync();
        var listed = (await _host.RpcAsync("tools/list", headers: McpHost.Auth())).Result["tools"]!.AsArray();
        Assert.Equal(Python().Select(tool => (string?)tool!["name"]), listed.Select(tool => (string?)tool!["name"]));
    }

    private static JsonArray Python() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-tools-list.json")))!["tools"]!.AsArray();
}
