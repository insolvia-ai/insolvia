using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpSpike;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace McpSpike.Tests;

/// <summary>
/// The protocol-level cases of services/mcp/tests/unit/test_protocol.py, ported:
/// real JSON-RPC POSTs (revision 2026-07-28 shape: _meta protocol version,
/// MCP-Protocol-Version / Mcp-Method / Mcp-Name headers), real RS256 tokens,
/// a real Kestrel socket. The domain cases (candidate flow, clients round
/// trip) are not ported: they test insolvia_core, not the protocol seam.
/// </summary>
public class ServerFixture : IAsyncLifetime
{
    public const string Version = "2026-07-28";
    public const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00";
    public const string ResourceUrl = "http://127.0.0.1:8788/mcp";

    public readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "k1" };
    public WebApplication App = null!;
    public HttpClient Http = null!;

    protected virtual bool Shim => true;

    public async Task InitializeAsync()
    {
        var settings = new SpikeSettings(ResourceUrl, Issuer, ["harness-client"], Key,
            ["127.0.0.1:*", "localhost:*"]);
        App = McpSpikeApp.Build(settings, url: "http://127.0.0.1:0", jsonResponseShim: Shim);
        await App.StartAsync();
        var addr = App.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Http = new HttpClient { BaseAddress = new Uri(addr) };
    }

    public async Task DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }

    public string Token(string sub = "user-1", string clientId = "harness-client",
        string tokenUse = "access", int expiresIn = 3600, RsaSecurityKey? key = null)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = now.AddSeconds(-Math.Max(0, -expiresIn) - 120),
            NotBefore = now.AddSeconds(-Math.Max(0, -expiresIn) - 120),
            Expires = now.AddSeconds(expiresIn),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = sub, ["client_id"] = clientId, ["token_use"] = tokenUse, ["scope"] = "openid",
            },
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

public sealed class RawSdkFixture : ServerFixture { protected override bool Shim => false; }

public class ProtocolTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private static readonly JsonObject Meta = new()
    {
        ["io.modelcontextprotocol/protocolVersion"] = ServerFixture.Version,
        ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "insolvia-tests", ["version"] = "0" },
        ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
    };

    private sealed record Reply(HttpStatusCode Status, string ContentType, HttpResponseHeaders Headers, string Raw)
    {
        /// <summary>The JSON-RPC body whether the server answered as JSON or as a single SSE event.</summary>
        public JsonNode Json
        {
            get
            {
                if (ContentType.StartsWith("text/event-stream"))
                {
                    var data = string.Join("\n", Raw.Split('\n')
                        .Where(l => l.StartsWith("data:")).Select(l => l[5..].TrimStart()));
                    return JsonNode.Parse(data)!;
                }
                return JsonNode.Parse(Raw)!;
            }
        }
    }

    private async Task<Reply> Rpc(string method, JsonObject? @params = null,
        Dictionary<string, string>? headers = null, string? host = null)
    {
        var p = @params is null ? new JsonObject() : (JsonObject)@params.DeepClone();
        p["_meta"] = Meta.DeepClone();
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = p };
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        req.Headers.Add("MCP-Protocol-Version", ServerFixture.Version);
        req.Headers.Add("Mcp-Method", method);
        if (host is not null) req.Headers.Host = host;
        foreach (var (k, v) in headers ?? []) req.Headers.TryAddWithoutValidation(k, v);
        var res = await fx.Http.SendAsync(req);
        return new Reply(res.StatusCode, res.Content.Headers.ContentType?.ToString() ?? "", res.Headers,
            await res.Content.ReadAsStringAsync());
    }

    private Task<Reply> Call(string tool, JsonObject args, Dictionary<string, string>? headers = null,
        string? nameHeader = null) =>
        Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args },
            new Dictionary<string, string> { ["Mcp-Name"] = nameHeader ?? tool }
                .Concat(headers ?? []).ToDictionary());

    private Dictionary<string, string> Auth(string? token = null) =>
        new() { ["Authorization"] = $"Bearer {token ?? fx.Token()}" };

    // ── transport auth ─────────────────────────────────────────────

    [Fact]
    public async Task An_unauthenticated_request_is_challenged()
    {
        var r = await Rpc("tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, r.Status);
        var challenge = r.Headers.WwwAuthenticate.ToString();
        Assert.StartsWith("Bearer ", challenge);
        Assert.Contains("resource_metadata=", challenge);
    }

    [Fact]
    public async Task Protected_resource_metadata_names_the_authorization_server()
    {
        var res = await fx.Http.GetAsync("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var meta = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal(ServerFixture.ResourceUrl, (string?)meta["resource"]);
        Assert.Equal([ServerFixture.Issuer], meta["authorization_servers"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public async Task A_wrong_host_header_is_rejected()
    {
        var r = await Rpc("tools/list", headers: Auth(), host: "testserver");
        Assert.Equal((HttpStatusCode)421, r.Status);
    }

    [Fact]
    public async Task An_expired_token_is_a_401()
    {
        var r = await Rpc("tools/list", headers: Auth(fx.Token(expiresIn: -60)));
        Assert.Equal(HttpStatusCode.Unauthorized, r.Status);
    }

    [Fact]
    public async Task A_token_for_another_client_id_is_a_401()
    {
        // Disjoint client ids ARE the audience check (an app token must fail closed here).
        var r = await Rpc("tools/list", headers: Auth(fx.Token(clientId: "app-client")));
        Assert.Equal(HttpStatusCode.Unauthorized, r.Status);
    }

    [Fact]
    public async Task A_token_signed_by_another_key_or_wrong_token_use_is_a_401()
    {
        var other = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k1" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc("tools/list", headers: Auth(fx.Token(key: other)))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc("tools/list", headers: Auth(fx.Token(tokenUse: "id")))).Status);
    }

    // ── the tool listing ───────────────────────────────────────────

    [Fact]
    public async Task The_listing_is_static_with_output_schemas_and_read_only_annotations()
    {
        var r = await Rpc("tools/list", headers: Auth());
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var tools = r.Json["result"]!["tools"]!.AsArray();
        Assert.Equal(["whoami", "list_cases"], tools.Select(t => (string?)t!["name"]));
        foreach (var t in tools)
        {
            Assert.NotNull(t!["outputSchema"]);
            // A real, typed schema (not the schema of CallToolResult itself).
            Assert.NotNull(t["outputSchema"]!["properties"]![(string)t["name"]! == "whoami" ? "firm" : "cases"]);
            Assert.True((bool)t["annotations"]!["readOnlyHint"]!);
        }
    }

    [Fact]
    public async Task The_listing_is_the_same_for_a_firmless_caller()
    {
        var r = await Rpc("tools/list", headers: Auth(fx.Token(sub: "no-firm-subject")));
        Assert.Equal(["whoami", "list_cases"],
            r.Json["result"]!["tools"]!.AsArray().Select(t => (string?)t!["name"]));
    }

    // ── tool calls ─────────────────────────────────────────────────

    [Fact]
    public async Task Whoami_answers_structured_content()
    {
        var r = await Call("whoami", new JsonObject(), Auth());
        var result = r.Json["result"]!;
        Assert.NotEqual(true, (bool?)result["isError"]);
        Assert.Equal("firm-1", (string?)result["structuredContent"]!["firm"]!["id"]);
        Assert.Equal("Example Firm", (string?)result["structuredContent"]!["firm"]!["name"]);
        Assert.Equal("text", (string?)result["content"]![0]!["type"]);
        Assert.Contains("\"displayName\"", (string?)result["content"]![0]!["text"]);
    }

    [Fact]
    public async Task A_domain_success_carries_structured_content_and_text()
    {
        var r = await Call("list_cases", new JsonObject { ["status"] = "intake" }, Auth());
        var result = r.Json["result"]!;
        Assert.NotEqual(true, (bool?)result["isError"]);
        var cases = result["structuredContent"]!["cases"]!.AsArray();
        Assert.Equal("case-2", (string?)Assert.Single(cases)!["id"]);
    }

    [Fact]
    public async Task A_validation_refusal_names_fields()
    {
        var r = await Call("list_cases", new JsonObject { ["status"] = "bogus" }, Auth());
        var result = r.Json["result"]!;
        Assert.True((bool)result["isError"]!);
        var error = result["structuredContent"]!["error"]!;
        Assert.Equal("validation_failed", (string?)error["code"]);
        Assert.Contains("status", error["fields"]!.AsArray().Select(f => (string?)f));
    }

    [Fact]
    public async Task A_permission_refusal_is_permission_denied()
    {
        var r = await Call("list_cases", new JsonObject(), Auth(fx.Token(sub: "no-firm-subject")));
        var error = r.Json["result"]!["structuredContent"]!["error"]!;
        Assert.Equal("permission_denied", (string?)error["code"]);
        Assert.False(string.IsNullOrEmpty((string?)error["message"]));
    }

    [Fact]
    public async Task An_unknown_tool_is_a_protocol_error()
    {
        var r = await Call("no_such_tool", new JsonObject(), Auth());
        var body = r.Json;
        Assert.True(body["error"] is not null || (bool?)body["result"]?["isError"] == true, r.Raw);
    }

    [Fact]
    public async Task A_header_body_mismatch_is_rejected_with_32020()
    {
        var r = await Call("whoami", new JsonObject(), Auth(), nameHeader: "somebody_else");
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal(-32020, (int)r.Json["error"]!["code"]!);
    }

    // ── beyond the Python cases: the posture the service depends on ──

    [Fact]
    public async Task Post_responses_are_reported_with_their_content_type()
    {
        // The Python server is configured json_response=True. Record what the C# SDK does;
        // this test passes on either and the research doc states which it is.
        var r = await Rpc("tools/list", headers: Auth());
        Console.WriteLine($"CONTENT-TYPE: {r.ContentType}\nRAW: {r.Raw.Replace('\n', '|')}");
        Assert.True(r.ContentType.StartsWith("application/json") || r.ContentType.StartsWith("text/event-stream"));
    }

    [Fact]
    public async Task Json_response_not_sse_with_the_shim()
    {
        var r = await Rpc("tools/list", headers: Auth());
        Assert.StartsWith("application/json", r.ContentType);
    }

    [Fact]
    public async Task GET_and_DELETE_are_not_supported_statelessly()
    {
        var get = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        get.Headers.Add("Authorization", $"Bearer {fx.Token()}");
        get.Headers.Accept.ParseAdd("text/event-stream");
        get.Headers.Add("MCP-Protocol-Version", ServerFixture.Version);
        var res = await fx.Http.SendAsync(get);
        Assert.Contains((int)res.StatusCode, new[] { 405, 400 });
    }

    [Fact]
    public async Task A_request_over_256_KiB_is_refused()
    {
        var big = new JsonObject { ["blob"] = new string('x', 300 * 1024) };
        var r = await Call("whoami", big, Auth());
        Assert.True((int)r.Status >= 400, $"status {(int)r.Status}");
    }
}

/// <summary>What the SDK does on its own, without our JSON shim.</summary>
public class RawSdkTests(RawSdkFixture fx) : IClassFixture<RawSdkFixture>
{
    [Fact]
    public async Task The_sdk_alone_answers_post_as_one_event_sse_stream()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"_meta\":{"
                + "\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\","
                + "\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"0\"},"
                + "\"io.modelcontextprotocol/clientCapabilities\":{}}}}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        req.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        req.Headers.Add("Mcp-Method", "tools/list");
        req.Headers.Add("Authorization", $"Bearer {fx.Token()}");
        var res = await fx.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        var raw = await res.Content.ReadAsStringAsync();
        Assert.Equal(1, raw.Split('\n').Count(l => l.StartsWith("data:")));
    }
}
