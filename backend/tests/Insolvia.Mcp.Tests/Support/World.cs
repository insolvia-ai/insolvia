using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Creditors;
using Insolvia.Core.Debtors;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Mcp.Tools;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Insolvia.Mcp.Tests.Support;

/// <summary>
/// services/mcp/tests/conftest.py, translated: a real RSA keypair,
/// Cognito-shaped tokens, and the in-memory surface. Every token is signed for
/// real — no mock verifier. Every id is obviously fake; this repo is public.
/// </summary>
internal static class World
{
    public const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00";

    // Two allowlisted MCP clients (one pre-registered client per harness),
    // plus the app's, which must never verify here.
    public const string ClientId = "examplemcpclaudeclient00";
    public const string SecondClientId = "examplemcpinspector00000";
    public const string OtherClientId = "exampleappclientid000000";
    public const string Subject = "00000000-0000-4000-8000-000000000001";
    public const string Colleague = "00000000-0000-4000-8000-000000000002";
    public const string FirmId = "00000000-0000-4000-8000-00000000f1a1";
    public const string OtherFirmId = "00000000-0000-4000-8000-00000000f1a2";
    public const string Kid = "test-key-1";

    // A tax-id POINTER, as the sealed item would be addressed — fake, and
    // asserted never to appear on this surface's wire.
    public const string TaxIdRef = "taxid-ref-example-0000000000000001";

    public static readonly DateTimeOffset Seeded = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The public half, as the JWKS provider serves it.</summary>
    public static SecurityKey PublicKey { get; } = new RsaSecurityKey(Rsa.ExportParameters(false)) { KeyId = Kid };

    public static StaticJwksProvider Jwks => new(new Dictionary<string, SecurityKey> { [Kid] = PublicKey });

    /// <summary>conftest's <c>make_token</c>: shaped exactly like a Cognito access token.</summary>
    public static string Token(
        string issuer = Issuer,
        string clientId = ClientId,
        string tokenUse = "access",
        string? subject = Subject,
        int expiresIn = 3600,
        string kid = Kid,
        RSA? key = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new JsonObject
        {
            ["iss"] = issuer,
            ["client_id"] = clientId,
            ["token_use"] = tokenUse,
            ["iat"] = now,
            ["exp"] = now + expiresIn,
            ["scope"] = "insolvia/mcp",
        };
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        return Handler.CreateToken(claims.ToJsonString(), new SigningCredentials(new RsaSecurityKey(key ?? Rsa) { KeyId = kid }, SecurityAlgorithms.RsaSha256));
    }

    public static Firm MakeFirm(string firmId = FirmId, FirmStatus status = FirmStatus.Active) =>
        new(firmId, "Example Firm", status, Seeded, Seeded);

    public static FirmUser MakeUser(
        string subject = Subject,
        string firmId = FirmId,
        bool isAdmin = false,
        bool accessAllCases = false,
        IReadOnlyDictionary<Feature, PermissionLevel>? permissions = null,
        FirmUserStatus status = FirmUserStatus.Active) =>
        new(
            firmId,
            subject,
            "dev@example.com",
            "Dev",
            "User",
            FirmRole.Attorney,
            isAdmin,
            accessAllCases,
            (permissions ?? new Dictionary<Feature, PermissionLevel>()).ToValueDictionary(),
            status,
            Seeded,
            Seeded);

    public static Accessor MakeAccessor(
        bool isAdmin = true,
        bool accessAllCases = false,
        IReadOnlyDictionary<Feature, PermissionLevel>? permissions = null,
        string subject = Subject,
        string firmId = FirmId) =>
        new(MakeFirm(firmId), MakeUser(subject, firmId, isAdmin, accessAllCases, permissions));

    /// <summary>A client in the firm's directory, carrying a tax-id POINTER, so a test can prove it never leaves.</summary>
    public static async Task<FirmClient> MakeFirmClientAsync(IFirmStore firms, string surname = "Example", string firmId = FirmId, bool withTaxId = true)
    {
        var client = FirmClientRules.Create(
            FirmClientRules.Parse(new JsonObject { ["name"] = new JsonObject { ["given"] = "Pat", ["surname"] = surname } }),
            firmId,
            Subject);
        if (withTaxId)
        {
            client = client with { TaxIdRef = TaxIdRef, TaxIdLastFour = "6789" };
        }

        await firms.CreateClientAsync(client, Ct);
        return client;
    }

    /// <summary>Copy the client onto the case as <paramref name="role"/> — the debtor POST /v1/cases writes.</summary>
    public static async Task LinkClientAsync(IDebtorStore debtors, FirmClient client, Case target, FilingRole role) =>
        Assert.True(await debtors.CreateAsync(ClientCopies.DebtorFromClient(client, target, role), Ct));

    public static async Task<Case> SeedCaseAsync(ICaseStore cases, Accessor? accessor = null)
    {
        accessor ??= MakeAccessor();
        var (created, assignment) = CaseRules.Create(
            CaseRules.ParseCreation(new JsonObject { ["chapter"] = 7, ["court"] = "flmb", ["division"] = "tampa" }, requireClients: false),
            accessor.FirmId,
            accessor.Subject);
        await cases.CreateAsync(created, assignment, cancellationToken: Ct);
        return created;
    }

    public static async Task<ICaseEntity> CreditorAsync(ICaseEntityStore entities, string caseId, string name = "Example Bank")
    {
        var draft = CaseEntityRules.ParseEntity(CreditorRules.Kind, new JsonObject
        {
            ["name"] = name,
            ["provenance"] = new JsonObject { ["name"] = new JsonObject { ["source"] = "staff_typed" } },
        });
        var entity = CaseEntityRules.Create(CreditorRules.Kind, draft, caseId);
        await entities.CreateAsync(entity, Ct);
        return entity;
    }
}

/// <summary>conftest's <c>stores</c> + <c>tools</c> + <c>firm_store</c> fixtures: the memory twins, composed as the host composes them.</summary>
internal sealed class Stores
{
    public Stores()
    {
        Firms = new MemoryFirmStore();
        Debtors = new MemoryDebtorStore(Firms);
        Cases = new MemoryCaseStore(Debtors);
        Tools = new CaseTools(Cases, Entities, Debtors, Documents, Candidates, AccessLog, Firms);
    }

    public MemoryFirmStore Firms { get; }

    public MemoryDebtorStore Debtors { get; }

    public MemoryCaseStore Cases { get; }

    public MemoryCaseEntityStore Entities { get; } = new();

    public MemoryDocumentStore Documents { get; } = new();

    public MemoryCandidateStore Candidates { get; } = new();

    public MemoryAccessLog AccessLog { get; } = new();

    public CaseTools Tools { get; }

    /// <summary>conftest's firm_store: the firm, and the subject as its admin.</summary>
    public async Task<Stores> SeededAsync()
    {
        await Firms.CreateFirmAsync(World.MakeFirm(), World.Ct);
        await Firms.AddUserAsync(World.MakeUser(isAdmin: true), World.Ct);
        return this;
    }
}
