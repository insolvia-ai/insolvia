using System.Net;
using System.Security.Cryptography;
using Insolvia.Core.Firms;
using Insolvia.Core.Memory;
using Insolvia.Mcp.Auth;
using Insolvia.Mcp.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using static Insolvia.Mcp.Tests.Support.World;

namespace Insolvia.Mcp.Tests;

/// <summary>
/// services/mcp/tests/unit/test_auth.py: mostly what verification and
/// accessor resolution REFUSE. The JWT checks are Core's, pinned by its own
/// suite; this pins the composition — every refusal is the same 401, a good
/// token carries subject and client, resolution fails closed.
/// </summary>
public sealed class AuthTests
{
    private static async Task<HttpStatusCode> StatusAsync(string token, IReadOnlyDictionary<string, string?>? settings = null, bool composeJwks = true)
    {
        using var host = new McpHost(settings, composeJwks);
        await host.SeededAsync();
        return (await host.RpcAsync("tools/list", headers: McpHost.Auth(token))).Status;
    }

    [Fact]
    public async Task A_valid_token_yields_the_principal()
    {
        using var host = new McpHost();
        await host.SeededAsync();
        var result = (await host.CallAsync("whoami", [], McpHost.Auth())).Result;
        Assert.Equal(FirmId, (string?)result["structuredContent"]!["firm"]!["id"]);

        // The client id rides into a candidate's origin, from the token.
        var found = await SeedCaseAsync(host.Cases);
        await host.CallAsync(
            "propose_case_records",
            new System.Text.Json.Nodes.JsonObject
            {
                ["caseId"] = found.Id,
                ["proposals"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["entityType"] = "creditors", ["payload"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "Example Bank" } }),
            },
            McpHost.Auth(Token(clientId: SecondClientId)));
        var stored = await ((MemoryCandidateStore)host.Services.GetService(typeof(Core.Ports.ICandidateStore))!).ListForCaseAsync(found.Id, Ct);
        var candidate = Assert.Single(stored);
        Assert.Equal(SecondClientId, candidate.Origin.ClientId);
        Assert.Equal(Subject, candidate.Origin.Subject);
        Assert.Equal(Core.Candidates.OriginChannel.Mcp, candidate.Origin.Channel);
    }

    [Fact]
    public async Task Every_harness_client_on_the_allowlist_verifies() =>
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(Token(clientId: SecondClientId)));

    [Fact]
    public async Task The_app_clients_token_is_not_an_mcp_token() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(Token(clientId: OtherClientId)));

    [Fact]
    public async Task Refusals_answer_401()
    {
        using var other = RSA.Create(2048);
        foreach (var token in new[]
        {
            "not-a-jwt",
            Token(expiresIn: -60 - Core.Auth.TokenVerification.ClockSkewSeconds),
            Token(issuer: Issuer + "x"),
            Token(tokenUse: "id"),
            Token(kid: "unknown-key"),
            Token(key: other),
            Token(subject: null),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(token));
        }
    }

    [Fact]
    public async Task Missing_configuration_fails_closed()
    {
        // No issuer, an empty allowlist, or no provider composed: every one
        // is a rejection, never a bypass.
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(Token(), new Dictionary<string, string?> { ["AUTH_ISSUER_URL"] = null }));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(Token(), new Dictionary<string, string?> { ["AUTH_CLIENT_IDS"] = " , " }));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(Token(), composeJwks: false));
    }

    [Fact]
    public async Task Resolve_accessor_answers_the_active_pair()
    {
        var store = new MemoryFirmStore();
        await store.CreateFirmAsync(MakeFirm(), Ct);
        await store.AddUserAsync(MakeUser(), Ct);
        var accessor = await AccessorResolution.ResolveAsync(store, Subject, NullLogger.Instance, Ct);
        Assert.Equal(Subject, accessor?.Subject);
    }

    [Fact]
    public async Task Resolution_fails_closed()
    {
        Assert.Null(await AccessorResolution.ResolveAsync(new MemoryFirmStore(), Subject, NullLogger.Instance, Ct));

        var disabled = new MemoryFirmStore();
        await disabled.CreateFirmAsync(MakeFirm(), Ct);
        await disabled.AddUserAsync(MakeUser(status: FirmUserStatus.Disabled), Ct);
        Assert.Null(await AccessorResolution.ResolveAsync(disabled, Subject, NullLogger.Instance, Ct));

        var suspended = new MemoryFirmStore();
        await suspended.CreateFirmAsync(MakeFirm(status: FirmStatus.Suspended), Ct);
        await suspended.AddUserAsync(MakeUser(), Ct);
        Assert.Null(await AccessorResolution.ResolveAsync(suspended, Subject, NullLogger.Instance, Ct));
    }
}
