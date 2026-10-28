using System.Text.Json.Nodes;
using Insolvia.Mcp.Tests.Support;

namespace Insolvia.Mcp.Tests;

/// <summary>
/// The SDK-level argument validation the Python server got from pydantic
/// (lax mode, over each tool's signature), pinned against what services/mcp
/// answered for the same calls: a wrong type is a TEXT-ONLY <c>isError</c>
/// (<c>Error executing tool …</c>, no <c>structuredContent</c>); lax coercions
/// are served; an empty string passes through to the domain's own refusal.
/// </summary>
public sealed class ArgumentTests : IDisposable
{
    private readonly McpHost _host = new();

    public void Dispose() => _host.Dispose();

    public static TheoryData<string, string, string> Refused() => new()
    {
        // tool, arguments, the pydantic location + type the message names
        { "list_cases", """{"limit": "x"}""", "limit\n  Input should be a valid integer, unable to parse string as an integer [type=int_parsing, input_value='x', input_type=str]" },
        { "list_cases", """{"limit": 2.5}""", "limit\n  Input should be a valid integer, got a number with a fractional part [type=int_from_float, input_value=2.5, input_type=float]" },
        { "list_cases", """{"cursor": 7}""", "cursor\n  Input should be a valid string [type=string_type, input_value=7, input_type=int]" },
        { "list_cases", """{"status": 5}""", "status\n  Input should be a valid string [type=string_type, input_value=5, input_type=int]" },
        { "get_case", "{}", "caseId\n  Field required [type=missing, input_value={}, input_type=dict]" },
        { "get_case", """{"caseId": 5}""", "caseId\n  Input should be a valid string [type=string_type, input_value=5, input_type=int]" },
        { "propose_case_records", """{"caseId": "c", "proposals": "x"}""", "proposals\n  Input should be a valid list [type=list_type, input_value='x', input_type=str]" },
        { "propose_case_records", """{"caseId": "c", "proposals": ["x"]}""", "proposals.0\n  Input should be a valid dictionary [type=dict_type, input_value='x', input_type=str]" },
        { "check_proposals", """{"caseId": "c", "candidateIds": "x"}""", "candidateIds\n  Input should be a valid list [type=list_type, input_value='x', input_type=str]" },
        { "check_proposals", """{"caseId": "c", "candidateIds": [1]}""", "candidateIds.0\n  Input should be a valid string [type=string_type, input_value=1, input_type=int]" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_wrong_type_is_a_text_only_tool_error(string tool, string arguments, string detail)
    {
        await _host.SeededAsync();
        var result = (await _host.CallAsync(tool, JsonNode.Parse(arguments)!.AsObject(), McpHost.Auth())).Result;
        Assert.True((bool)result["isError"]!);
        Assert.Null(result["structuredContent"]);
        var text = (string)result["content"]![0]!["text"]!;
        Assert.StartsWith($"Error executing tool {tool}: 1 validation error for {tool}_toolArguments\n", text, StringComparison.Ordinal);
        Assert.Contains(detail, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"limit": "5"}""")]
    [InlineData("""{"limit": 2.0}""")]
    [InlineData("""{"limit": true}""")]
    [InlineData("""{"limit": null, "status": null, "cursor": null}""")]
    [InlineData("""{"unexpected": 1}""")]
    public async Task Lax_inputs_are_served(string arguments)
    {
        await _host.SeededAsync();
        await World.SeedCaseAsync(_host.Cases);
        var result = (await _host.CallAsync("list_cases", JsonNode.Parse(arguments)!.AsObject(), McpHost.Auth())).Result;
        Assert.False((bool)result["isError"]!);
        Assert.Single(result["structuredContent"]!["cases"]!.AsArray());
    }

    [Theory]
    [InlineData("get_case", """{"caseId": ""}""", "caseId is required")]
    [InlineData("list_cases", """{"limit": false}""", "limit must be between 1 and 100")]
    [InlineData("list_cases", """{"limit": 0}""", "limit must be between 1 and 100")]
    [InlineData("list_cases", """{"cursor": "not-a-cursor"}""", "cursor is not valid")]
    [InlineData("list_cases", """{"status": "open"}""", "status must be one of: intake, ready_to_file, filed, discharged, dismissed, closed")]
    [InlineData("propose_case_records", """{"caseId": "c", "proposals": []}""", "proposals must contain between 1 and 25 items")]
    [InlineData("check_proposals", """{"caseId": "c", "status": "nope"}""", "status must be one of: pending, accepted, corrected, rejected, withdrawn")]
    [InlineData("withdraw_proposal", """{"caseId": "c", "candidateId": ""}""", "candidateId is required")]
    public async Task What_pydantic_admits_the_domain_refuses_in_its_envelope(string tool, string arguments, string message)
    {
        await _host.SeededAsync();
        var result = (await _host.CallAsync(tool, JsonNode.Parse(arguments)!.AsObject(), McpHost.Auth())).Result;
        Assert.True((bool)result["isError"]!);
        Assert.Equal("validation_failed", (string?)result["structuredContent"]!["error"]!["code"]);
        Assert.Equal(message, (string?)result["structuredContent"]!["error"]!["message"]);
        Assert.Equal(
            $"{{\"error\": {{\"code\": \"validation_failed\", \"message\": \"{message}\"}}}}",
            (string?)result["content"]![0]!["text"]);
    }

    [Fact]
    public async Task A_withdrawn_candidate_conflicts_with_pythons_message()
    {
        await _host.SeededAsync();
        var found = await World.SeedCaseAsync(_host.Cases);
        var proposed = await _host.CallAsync(
            "propose_case_records",
            new JsonObject { ["caseId"] = found.Id, ["proposals"] = new JsonArray(new JsonObject { ["entityType"] = "creditors", ["payload"] = new JsonObject { ["name"] = "Example Bank" } }) },
            McpHost.Auth());
        var candidateId = (string)proposed.Result["structuredContent"]!["candidates"]![0]!["candidateId"]!;
        var args = new JsonObject { ["caseId"] = found.Id, ["candidateId"] = candidateId };
        await _host.CallAsync("withdraw_proposal", args, McpHost.Auth());
        var again = (await _host.CallAsync("withdraw_proposal", (JsonObject)args.DeepClone(), McpHost.Auth())).Result;
        Assert.Equal("conflict", (string?)again["structuredContent"]!["error"]!["code"]);
        Assert.Equal("candidate has already been reviewed (status: withdrawn)", (string?)again["structuredContent"]!["error"]!["message"]);
    }

    [Fact]
    public async Task A_field_refusal_reads_as_pythons()
    {
        await _host.SeededAsync();
        var found = await World.SeedCaseAsync(_host.Cases);
        var result = (await _host.CallAsync(
            "propose_case_records",
            new JsonObject { ["caseId"] = found.Id, ["proposals"] = new JsonArray(new JsonObject { ["entityType"] = "creditors", ["payload"] = new JsonObject { ["name"] = 42 } }) },
            McpHost.Auth())).Result;
        Assert.Equal(
            "{\"error\": {\"code\": \"validation_failed\", \"message\": \"validation failed: proposals[0].payload.name\", \"fields\": {\"proposals[0].payload.name\": \"Must be text.\"}}}",
            (string?)result["content"]![0]!["text"]);
    }
}
