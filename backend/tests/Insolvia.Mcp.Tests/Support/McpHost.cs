using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insolvia.Mcp.Tests.Support;

/// <summary>
/// The real Program pipeline in-process (test_protocol.py's <c>client</c>
/// fixture): the composed memory twins, the static JWKS provider, and a
/// client whose base address is the dev-server host the local transport
/// security allows.
/// </summary>
internal sealed class McpHost : IDisposable
{
    public const string ProtocolVersion = "2026-07-28";
    public const string LocalBase = "http://127.0.0.1:8788";

    private static readonly JsonObject Meta = new()
    {
        ["io.modelcontextprotocol/protocolVersion"] = ProtocolVersion,
        ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "insolvia-tests", ["version"] = "0" },
        ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
    };

    private readonly WebApplicationFactory<Program> _factory;
    private int _rpcId;

    public McpHost(IReadOnlyDictionary<string, string?>? settings = null, bool composeJwks = true, string baseAddress = LocalBase)
    {
        var effective = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["INSOLVIA_ENV"] = "local",
            ["AUTH_ISSUER_URL"] = World.Issuer,
            ["AUTH_CLIENT_IDS"] = $"{World.ClientId},{World.SecondClientId}",
            ["INSOLVIA_DB_CONNECTION_STRING"] = null,
            ["AWS_LAMBDA_FUNCTION_NAME"] = null,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            effective[key] = value;
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            foreach (var (key, value) in effective)
            {
                host.UseSetting(key, value);
            }

            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJwksProvider>();
                if (composeJwks)
                {
                    services.AddSingleton<IJwksProvider>(World.Jwks);
                }
            });
        });
        Http = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress), AllowAutoRedirect = false });
    }

    public HttpClient Http { get; }

    public IServiceProvider Services => _factory.Services;

    public MemoryFirmStore Firms => (MemoryFirmStore)Services.GetRequiredService<IFirmStore>();

    public MemoryCaseStore Cases => (MemoryCaseStore)Services.GetRequiredService<ICaseStore>();

    public MemoryDebtorStore Debtors => (MemoryDebtorStore)Services.GetRequiredService<IDebtorStore>();

    public MemoryAccessLog AccessLog => (MemoryAccessLog)Services.GetRequiredService<IAccessLog>();

    public static Dictionary<string, string> Auth(string? token = null) => new() { ["Authorization"] = $"Bearer {token ?? World.Token()}" };

    /// <summary>conftest's firm_store: the firm, and the subject as its admin.</summary>
    public async Task<McpHost> SeededAsync()
    {
        await Firms.CreateFirmAsync(World.MakeFirm(), World.Ct);
        await Firms.AddUserAsync(World.MakeUser(isAdmin: true), World.Ct);
        return this;
    }

    /// <summary>A 2026-07-28 request: the <c>_meta</c> envelope and the two headers.</summary>
    public Task<Reply> RpcAsync(string method, JsonObject? @params = null, Dictionary<string, string>? headers = null)
    {
        var p = @params is null ? new JsonObject() : (JsonObject)@params.DeepClone();
        p["_meta"] = Meta.DeepClone();
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_rpcId, ["method"] = method, ["params"] = p };
        var all = new Dictionary<string, string> { ["MCP-Protocol-Version"] = ProtocolVersion, ["Mcp-Method"] = method };
        foreach (var (key, value) in headers ?? [])
        {
            all[key] = value;
        }

        return PostAsync(body.ToJsonString(), all);
    }

    /// <summary>A tools/call with the matching <c>Mcp-Name</c> header (or a deliberately wrong one).</summary>
    public Task<Reply> CallAsync(string tool, JsonObject arguments, Dictionary<string, string>? headers = null, string? nameHeader = null)
    {
        var all = new Dictionary<string, string> { ["Mcp-Name"] = nameHeader ?? tool };
        foreach (var (key, value) in headers ?? [])
        {
            all[key] = value;
        }

        return RpcAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }, all);
    }

    /// <summary>A raw POST to /mcp.</summary>
    public async Task<Reply> PostAsync(string body, Dictionary<string, string>? headers = null, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        foreach (var (key, value) in headers ?? [])
        {
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Host = value;
            }
            else if (key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Accept.Clear();
                request.Headers.TryAddWithoutValidation("Accept", value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var response = await Http.SendAsync(request, World.Ct);
        return new Reply(
            response.StatusCode,
            response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            response.Headers.WwwAuthenticate.ToString(),
            await response.Content.ReadAsStringAsync(World.Ct));
    }

    public void Dispose()
    {
        Http.Dispose();
        _factory.Dispose();
    }
}

/// <summary>One HTTP answer.</summary>
internal sealed record Reply(HttpStatusCode Status, string ContentType, string WwwAuthenticate, string Raw)
{
    public JsonNode Json => JsonNode.Parse(Raw)!;

    /// <summary>A tools/call's <c>result</c>.</summary>
    public JsonNode Result => Json["result"]!;
}
