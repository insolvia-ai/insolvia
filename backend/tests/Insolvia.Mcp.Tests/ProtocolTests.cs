using System.Net;
using System.Text.Json.Nodes;
using Insolvia.Core.Debtors;
using Insolvia.Core.Firms;
using Insolvia.Mcp.Tests.Support;
using static Insolvia.Mcp.Tests.Support.World;

namespace Insolvia.Mcp.Tests;

/// <summary>
/// services/mcp/tests/unit/test_protocol.py — the WIRE, pinned: real
/// JSON-RPC POSTs (revision 2026-07-28: <c>_meta</c> envelope,
/// <c>MCP-Protocol-Version</c> / <c>Mcp-Method</c> / <c>Mcp-Name</c>) through
/// the real pipeline with real RS256 tokens. Every Python case is here, the
/// candidate flow and the clients round trip included; the cases below the
/// last Python one are the transport behaviours the Python SDK gave the
/// service for free and this host has to own.
/// </summary>
public sealed class ProtocolTests : IDisposable
{
    private static readonly string[] ExpectedTools =
        ["whoami", "list_cases", "get_case", "list_case_records", "get_case_record", "propose_case_records", "check_proposals", "withdraw_proposal"];

    private static readonly string[] ReadOnlyTools =
        ["whoami", "list_cases", "get_case", "list_case_records", "get_case_record", "check_proposals"];

    private readonly McpHost _host = new();

    public void Dispose() => _host.Dispose();

    private static Dictionary<string, string> Auth(string? token = null) => McpHost.Auth(token);

    private async Task<Core.Cases.Case> SeedCaseAsync() => await World.SeedCaseAsync(_host.Cases);

    // ── transport auth ──────────────────────────────────────────────

    [Fact]
    public async Task An_unauthenticated_request_is_challenged()
    {
        var reply = await _host.RpcAsync("tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
        Assert.StartsWith("Bearer ", reply.WwwAuthenticate, StringComparison.Ordinal);
        Assert.Contains("resource_metadata=", reply.WwwAuthenticate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protected_resource_metadata_names_the_authorization_server()
    {
        using var response = await _host.Http.GetAsync(new Uri("/.well-known/oauth-protected-resource/mcp", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var metadata = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("http://127.0.0.1:8788/mcp", (string?)metadata["resource"]);
        Assert.Equal([Issuer], metadata["authorization_servers"]!.AsArray().Select(node => (string?)node));
    }

    [Fact]
    public async Task A_wrong_host_header_is_rejected()
    {
        // The DNS-rebinding MUST: a deployed environment answers exactly its own hostname.
        using var host = new McpHost(baseAddress: "http://testserver");
        var reply = await host.RpcAsync("tools/list", headers: Auth());
        Assert.Equal(HttpStatusCode.MisdirectedRequest, reply.Status);
    }

    [Fact]
    public async Task An_expired_token_is_a_401()
    {
        var reply = await _host.RpcAsync("tools/list", headers: Auth(Token(expiresIn: -60 - Core.Auth.TokenVerification.ClockSkewSeconds)));
        Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
    }

    // ── the tool listing ────────────────────────────────────────────

    [Fact]
    public async Task The_listing_is_the_static_eight()
    {
        await _host.SeededAsync();
        var reply = await _host.RpcAsync("tools/list", headers: Auth());
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        var tools = reply.Result["tools"]!.AsArray();
        Assert.Equal(ExpectedTools, tools.Select(tool => (string?)tool!["name"]));
        foreach (var tool in tools)
        {
            var name = (string)tool!["name"]!;
            Assert.NotEmpty(tool["outputSchema"]!.AsObject());
            Assert.Equal(ReadOnlyTools.Contains(name), (bool)tool["annotations"]!["readOnlyHint"]!);
        }
    }

    [Fact]
    public async Task The_listing_is_the_same_for_a_permission_stripped_caller()
    {
        // List-visible, call-denied: permissions are revocable mid-hour, so a
        // filtered list would be stale the moment it mattered.
        await _host.SeededAsync();
        Assert.NotNull(await _host.Firms.UpdateUserAsync(MakeUser(isAdmin: false, accessAllCases: false, permissions: new Dictionary<Feature, PermissionLevel>()), Ct));
        var reply = await _host.RpcAsync("tools/list", headers: Auth());
        Assert.Equal(ExpectedTools, reply.Result["tools"]!.AsArray().Select(tool => (string?)tool!["name"]));
    }

    // ── tool calls ──────────────────────────────────────────────────

    [Fact]
    public async Task Whoami_answers_structured_content()
    {
        await _host.SeededAsync();
        var result = (await _host.CallAsync("whoami", [], Auth())).Result;
        Assert.NotEqual(true, (bool?)result["isError"]);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["id"] = FirmId, ["name"] = "Example Firm" }, result["structuredContent"]!["firm"]));

        // The backwards-compatibility text block carries the same JSON.
        Assert.Equal("text", (string?)result["content"]![0]!["type"]);
        Assert.Contains("\"displayName\"", (string?)result["content"]![0]!["text"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_candidate_flow_end_to_end()
    {
        await _host.SeededAsync();
        var found = await SeedCaseAsync();
        var proposed = await _host.CallAsync(
            "propose_case_records",
            new JsonObject
            {
                ["caseId"] = found.Id,
                ["proposals"] = new JsonArray(new JsonObject { ["entityType"] = "creditors", ["payload"] = new JsonObject { ["name"] = "Example Bank" } }),
            },
            Auth());
        var candidate = Assert.Single(proposed.Result["structuredContent"]!["candidates"]!.AsArray())!;
        Assert.Equal("pending", (string?)candidate["status"]);

        var checkedReply = await _host.CallAsync("check_proposals", new JsonObject { ["caseId"] = found.Id }, Auth());
        Assert.Equal((string?)candidate["candidateId"], (string?)checkedReply.Result["structuredContent"]!["candidates"]![0]!["candidateId"]);

        var withdrawn = await _host.CallAsync("withdraw_proposal", new JsonObject { ["caseId"] = found.Id, ["candidateId"] = (string?)candidate["candidateId"] }, Auth());
        Assert.Equal("withdrawn", (string?)withdrawn.Result["structuredContent"]!["status"]);
    }

    [Fact]
    public async Task A_cases_clients_round_trip()
    {
        // ADR 0022 PR 6: a case's clients through the same two record tools,
        // and the wire carries the tax id's last four only.
        await _host.SeededAsync();
        var found = await SeedCaseAsync();
        var client = await MakeFirmClientAsync(_host.Firms);
        await LinkClientAsync(_host.Debtors, client, found, FilingRole.Debtor1);

        var listed = await _host.CallAsync("list_case_records", new JsonObject { ["caseId"] = found.Id, ["entityType"] = "clients" }, Auth());
        Assert.NotEqual(true, (bool?)listed.Result["isError"]);
        var record = Assert.Single(listed.Result["structuredContent"]!["records"]!.AsArray())!;
        Assert.Equal(client.Id, (string?)record["id"]);
        Assert.Equal("6789", (string?)record["tax_id_last_four"]);
        Assert.DoesNotContain(TaxIdRef, listed.Raw, StringComparison.Ordinal);

        var read = await _host.CallAsync("get_case_record", new JsonObject { ["caseId"] = found.Id, ["entityType"] = "clients", ["recordId"] = client.Id }, Auth());
        Assert.Equal(client.Id, (string?)read.Result["structuredContent"]!["record"]!["id"]);
        Assert.DoesNotContain(TaxIdRef, read.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_domain_refusal_carries_the_error_envelope()
    {
        await _host.SeededAsync();
        var result = (await _host.CallAsync("get_case", new JsonObject { ["caseId"] = "no-such-case" }, Auth())).Result;
        Assert.True((bool)result["isError"]!);
        var error = result["structuredContent"]!["error"]!;
        Assert.Equal("not_found", (string?)error["code"]);
        Assert.False(string.IsNullOrEmpty((string?)error["message"]));
    }

    [Fact]
    public async Task A_permission_refusal_is_permission_denied()
    {
        await _host.SeededAsync();
        await _host.Firms.UpdateUserAsync(MakeUser(isAdmin: false, accessAllCases: false, permissions: new Dictionary<Feature, PermissionLevel>()), Ct);
        var result = (await _host.CallAsync("list_cases", [], Auth())).Result;
        Assert.Equal("permission_denied", (string?)result["structuredContent"]!["error"]!["code"]);
    }

    [Fact]
    public async Task A_validation_refusal_names_fields()
    {
        await _host.SeededAsync();
        var found = await SeedCaseAsync();
        var result = (await _host.CallAsync(
            "propose_case_records",
            new JsonObject
            {
                ["caseId"] = found.Id,
                ["proposals"] = new JsonArray(new JsonObject { ["entityType"] = "creditors", ["payload"] = new JsonObject { ["name"] = 42 } }),
            },
            Auth())).Result;
        var error = result["structuredContent"]!["error"]!;
        Assert.Equal("validation_failed", (string?)error["code"]);
        Assert.True(error["fields"]!.AsObject().ContainsKey("proposals[0].payload.name"));
    }

    [Fact]
    public async Task An_unknown_tool_is_a_protocol_error()
    {
        await _host.SeededAsync();
        var body = (await _host.CallAsync("no_such_tool", [], Auth())).Json;

        // Malformed at the protocol layer, not refused by the domain.
        Assert.True(body["error"] is not null || (bool?)body["result"]?["isError"] == true, body.ToJsonString());
    }

    [Fact]
    public async Task A_header_body_mismatch_is_rejected()
    {
        await _host.SeededAsync();
        var reply = await _host.CallAsync("whoami", [], Auth(), nameHeader: "somebody_else");
        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal(-32020, (int)reply.Json["error"]!["code"]!);
    }

    // ── beyond test_protocol.py: what the Python SDK did for free ────

    [Fact]
    public async Task Responses_are_json_never_a_stream()
    {
        await _host.SeededAsync();
        var reply = await _host.RpcAsync("tools/list", headers: Auth());
        Assert.StartsWith("application/json", reply.ContentType, StringComparison.Ordinal);
        Assert.Equal(ExpectedTools.Length, reply.Result["tools"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_json_only_accept_is_served_and_a_stream_only_accept_is_406()
    {
        await _host.SeededAsync();
        var json = await _host.RpcAsync("tools/list", headers: new(Auth()) { ["Accept"] = "application/json" });
        Assert.Equal(HttpStatusCode.OK, json.Status);
        var stream = await _host.RpcAsync("tools/list", headers: new(Auth()) { ["Accept"] = "text/event-stream" });
        Assert.Equal(HttpStatusCode.NotAcceptable, stream.Status);
    }

    [Fact]
    public async Task The_challenge_is_the_python_sdks()
    {
        var reply = await _host.RpcAsync("tools/list", headers: Auth("not-a-jwt"));
        Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
        Assert.Equal(
            "Bearer error=\"invalid_token\", error_description=\"Authentication required\", resource_metadata=\"http://127.0.0.1:8788/.well-known/oauth-protected-resource/mcp\"",
            reply.WwwAuthenticate);
        Assert.Equal("{\"error\":\"invalid_token\",\"error_description\":\"Authentication required\"}", reply.Raw);
    }

    [Fact]
    public async Task The_metadata_document_is_the_python_sdks()
    {
        using var response = await _host.Http.GetAsync(new Uri("/.well-known/oauth-protected-resource/mcp", UriKind.Relative), Ct);
        var expected = new JsonObject
        {
            ["resource"] = "http://127.0.0.1:8788/mcp",
            ["authorization_servers"] = new JsonArray(Issuer),
            ["bearer_methods_supported"] = new JsonArray("header"),
        };
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))));
        using var root = await _host.Http.GetAsync(new Uri("/.well-known/oauth-protected-resource", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
    }

    [Theory]
    [InlineData(OtherClientId)] // the app's own client: valid, but not an MCP client
    [InlineData("someotherclient000000000")]
    public async Task A_token_for_a_client_off_the_allowlist_is_a_401(string clientId)
    {
        var reply = await _host.RpcAsync("tools/list", headers: Auth(Token(clientId: clientId)));
        Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
    }

    [Fact]
    public async Task Every_harness_client_on_the_allowlist_is_admitted()
    {
        await _host.SeededAsync();
        Assert.Equal(HttpStatusCode.OK, (await _host.RpcAsync("tools/list", headers: Auth(Token(clientId: SecondClientId)))).Status);
    }

    [Fact]
    public async Task Authentication_runs_before_the_transport_checks()
    {
        // Python's auth middleware wrapped the transport: unauthenticated is
        // the challenge whatever else is wrong with the request.
        using var host = new McpHost(baseAddress: "http://testserver");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RpcAsync("tools/list")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.RpcAsync("tools/list", headers: new() { ["Origin"] = "https://evil.example" })).Status);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://127.0.0.1:8788")] // the allowlist is empty: even our own origin
    public async Task A_present_origin_is_refused(string origin)
    {
        await _host.SeededAsync();
        var reply = await _host.RpcAsync("tools/list", headers: new(Auth()) { ["Origin"] = origin });
        Assert.Equal(HttpStatusCode.Forbidden, reply.Status);
        Assert.Equal("Invalid Origin header", reply.Raw);
    }

    [Fact]
    public async Task A_non_json_content_type_is_refused()
    {
        var reply = await _host.PostAsync("{}", Auth(), contentType: "text/plain");
        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal("Invalid Content-Type header", reply.Raw);
    }

    [Fact]
    public async Task A_body_over_256_KiB_is_413()
    {
        await _host.SeededAsync();
        var reply = await _host.CallAsync("whoami", new JsonObject { ["blob"] = new string('x', 300 * 1024) }, Auth());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, reply.Status);
        Assert.Equal("Request body too large", reply.Raw);
    }

    [Fact]
    public async Task Unparseable_json_is_a_parse_error()
    {
        await _host.SeededAsync();
        var reply = await _host.PostAsync("{nope", new(Auth()) { ["MCP-Protocol-Version"] = McpHost.ProtocolVersion, ["Mcp-Method"] = "tools/list" });
        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal(-32700, (int)reply.Json["error"]!["code"]!);
    }

    [Fact]
    public async Task Server_discover_names_the_revision_and_the_tools_capability()
    {
        await _host.SeededAsync();
        var result = (await _host.RpcAsync("server/discover", headers: Auth())).Result;
        Assert.Contains(McpHost.ProtocolVersion, result["supportedVersions"]!.AsArray().Select(node => (string?)node));
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Equal("insolvia", (string?)result["_meta"]!["io.modelcontextprotocol/serverInfo"]!["name"]);
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    public async Task The_legacy_initialize_handshake_is_bridged(string version)
    {
        // What an initialize-era harness sends: neither header nor envelope.
        await _host.SeededAsync();
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "x", ["version"] = "0" },
            },
        };
        var reply = await _host.PostAsync(body.ToJsonString(), Auth());
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        Assert.Equal(version, (string?)reply.Result["protocolVersion"]);
        Assert.Equal("insolvia", (string?)reply.Result["serverInfo"]!["name"]);
        Assert.Equal("Insolvia case management", (string?)reply.Result["serverInfo"]!["title"]);
        Assert.NotNull(reply.Result["capabilities"]!["tools"]);
        Assert.StartsWith("Bankruptcy case preparation", (string?)reply.Result["instructions"], StringComparison.Ordinal);

        // ...and its next call, stateless: no session to resume, none needed.
        var listed = await _host.PostAsync(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list", ["params"] = new JsonObject() }.ToJsonString(),
            new(Auth()) { ["MCP-Protocol-Version"] = version });
        Assert.Equal(HttpStatusCode.OK, listed.Status);
        Assert.Equal(ExpectedTools, listed.Result["tools"]!.AsArray().Select(tool => (string?)tool!["name"]));
    }

    [Fact]
    public async Task A_notification_is_accepted_with_no_body()
    {
        var reply = await _host.PostAsync(
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString(),
            new(Auth()) { ["MCP-Protocol-Version"] = McpHost.ProtocolVersion, ["Mcp-Method"] = "notifications/initialized" });
        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
        Assert.Equal(string.Empty, reply.Raw);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task There_is_no_stream_and_no_session_to_delete(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/mcp");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token()}");
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        using var response = await _host.Http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task The_text_block_is_python_json_dumps_of_the_structured_content()
    {
        await _host.SeededAsync();
        var result = (await _host.CallAsync("whoami", [], Auth())).Result;
        var text = (string)result["content"]![0]!["text"]!;
        Assert.StartsWith("{\"firm\": {\"id\": \"" + FirmId + "\", \"name\": \"Example Firm\"}, \"displayName\": \"Dev User\", \"isAdmin\": true", text, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result["structuredContent"]));
    }

    [Fact]
    public async Task The_accessor_is_resolved_on_every_call()
    {
        // Revocation is immediate: disable the user between two calls on the
        // same token and the second call is refused.
        await _host.SeededAsync();
        Assert.NotEqual(true, (bool?)(await _host.CallAsync("list_cases", [], Auth())).Result["isError"]);
        await _host.Firms.UpdateUserAsync(MakeUser(isAdmin: true, status: FirmUserStatus.Disabled), Ct);
        var refused = (await _host.CallAsync("list_cases", [], Auth())).Result;
        Assert.Equal("permission_denied", (string?)refused["structuredContent"]!["error"]!["code"]);
        Assert.Equal("your account is not active in a firm", (string?)refused["structuredContent"]!["error"]!["message"]);

        // whoami answers the fact instead of refusing.
        var who = (await _host.CallAsync("whoami", [], Auth())).Result;
        Assert.Null(who["structuredContent"]!["firm"]);
    }
}
