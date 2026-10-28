using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Aws.Sealing;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Courts;
using Insolvia.Core.Filings;
using Insolvia.Core.Firms;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;
using Insolvia.Filing.Drivers;
using Insolvia.Filing.Fence;
using Insolvia.Filing.Http;
using Insolvia.Filing.Tests.FakeCourt;
using Insolvia.Forms;
using Insolvia.Forms.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insolvia.Filing.Tests.Support;

/// <summary>A log that keeps every line — the guard tests search it.</summary>
internal sealed class CapturingLog : IFilingLog
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public void Write(string level, string message, params (string Key, object? Value)[] fields)
    {
        lock (_gate)
        {
            _lines.Add($"{level} {message} " + string.Join(" ", fields.Select(f => $"{f.Key}={f.Value}")));
        }
    }
}

/// <summary>The record clock, movable (the lease tests step past it).</summary>
internal sealed class TestClock : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}

/// <summary>
/// The approval world (services/filing tests/conftest.py's <c>FilingWorld</c>):
/// the REAL Insolvia.Api in-process over its memory stores, the reference
/// Chapter 7 case seeded into them in the Middle District of Florida with a
/// real filing-set packet, the attorney's authorization signed and the fake
/// court's login enrolled THROUGH THE ROUTES, the fake CM/ECF on 127.0.0.1,
/// and the worker composed over the same stores. Every identifier is fake;
/// the court's password and seed are minted per court.
/// </summary>
internal sealed class FilingWorld : IAsyncDisposable
{
    public const string Attorney = "00000000-0000-4000-8000-00000000a771";
    public const string Firm = "00000000-0000-4000-8000-00000000f18a";
    private const string Court = "flmb";

    private static readonly Lazy<(byte[] Zip, AssembledPacket Assembled)> Assembly = new(AssembleReference);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    private FilingWorld(FakeCmEcf court, WebApplicationFactory<Program> factory)
    {
        CourtFake = court;
        _factory = factory;
        _client = factory.CreateClient();
        CaseId = Guid.NewGuid().ToString();
    }

    public FakeCmEcf CourtFake { get; }

    public string CaseId { get; }

    public string CredentialId { get; private set; } = string.Empty;

    public MemoryFilingJobSender Queue { get; } = new();

    public MemoryAccessLog AccessLog { get; } = new();

    public MemoryFirmStore Firms { get; } = new();

    public CapturingLog Log { get; } = new();

    public StaticKillSwitch KillSwitch { get; } = new(true);

    public TestClock Clock { get; } = new();

    public string Environment { get; set; } = "local";

    /// <summary>The driver; the fake on this court when null.</summary>
    public Func<ICourtDriver>? Driver { get; set; }

    public bool NoDriver { get; set; }

    public int LeaseSeconds { get; set; } = 600;

    public IServiceProvider Services => _factory.Services;

    public T Service<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public static async Task<FilingWorld> CreateAsync(FakeCmEcf court)
    {
        FilingWorld? world = null;
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("INSOLVIA_ENV", "local");
            host.UseSetting("AUTH_ISSUER_URL", Tokens.Issuer);
            host.UseSetting("AUTH_CLIENT_ID", Tokens.StaffClientId);
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJwksProvider>();
                services.AddSingleton<IJwksProvider>(new StaticJwksProvider(new Dictionary<string, Microsoft.IdentityModel.Tokens.SecurityKey> { [Tokens.Kid] = Tokens.PublicKey }));
                services.RemoveAll<IFirmStore>();
                services.AddSingleton<IFirmStore>(_ => world!.Firms);
                services.RemoveAll<IAccessLog>();
                services.AddSingleton<IAccessLog>(_ => world!.AccessLog);
                services.AddSingleton<IFilingJobSender>(_ => world!.Queue);
            });
        });
        world = new FilingWorld(court, factory);
        await world.SeedAsync();
        return world;
    }

    // ── seeding ──

    private async Task SeedAsync()
    {
        var seeded = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await Firms.CreateFirmAsync(new Core.Firms.Firm(Firm, "Example & Partners", FirmStatus.Active, seeded, seeded));
        var permissions = Permissions.DefaultFor(FirmRole.Attorney).ToDictionary(pair => pair.Key, pair => pair.Value);
        permissions[Feature.ElectronicFiling] = PermissionLevel.AddEdit;
        await Firms.AddUserAsync(new FirmUser(Firm, Attorney, "a771@example.test", "Avery", "Example", FirmRole.Attorney, false, true, permissions.ToValueDictionary(), FirmUserStatus.Active, seeded, seeded));

        var data = Reference(CaseId);
        await Service<ICaseStore>().CreateAsync(data.Case, new CaseAssignment(CaseId, Attorney, data.Case.CreatedAt, data.Case.CreatedAt, Attorney), data.Debtors);
        // The reference tax ids, SEALED the way the debtor route seals them —
        // under the firm and each debtor's ref, with the API's own cipher — so
        // the worker's logged read (not the fixture's digits) is what puts the
        // number in Debtor.txt.
        var cipher = Service<ITaxIdCipher>();
        foreach (var debtor in data.Debtors)
        {
            if (debtor.TaxId is { } taxId)
            {
                var envelope = await cipher.SealAsync(data.TaxIds[Wire.ToWire(debtor.FilingRole)], TaxIdRules.EncryptionContext(Firm, taxId.Ref));
                await Service<ITaxIdStore>().PutAsync(CaseId, new SealedTaxId(taxId.Ref, Firm, taxId.Kind, taxId.LastFour, envelope, seeded, seeded));
            }
        }

        var entities = Service<ICaseEntityStore>();
        foreach (var (_, members) in data.Collections())
        {
            foreach (var entity in members)
            {
                await entities.CreateAsync(entity);
            }
        }

        var (zip, assembled) = Assembly.Value;
        var packet = Packets_New(zip, assembled);
        await StorePacketAsync(packet, zip);

        await SignAsync();
        CredentialId = await EnrolAsync(FakeCmEcf.Login, CourtFake.Password, CourtFake.TotpSeed);
    }

    private Packet Packets_New(byte[] zip, AssembledPacket assembled) => Forms.Packets.NewPacket(
        CaseId,
        7,
        Guid.NewGuid().ToString(),
        zip.Length,
        Convert.ToHexStringLower(SHA256.HashData(zip)),
        assembled.FormRevisions,
        assembled.ConstantsSetId,
        assembled.CreditorCount,
        Attorney,
        new OutputOptions(),
        PdfMeasure.MeasureParts(assembled.Parts));

    /// <summary>Re-assemble: a new packet (same bytes, new id) — the digest moves.</summary>
    public async Task ReassembleAsync()
    {
        var (zip, assembled) = Assembly.Value;
        await StorePacketAsync(Packets_New(zip, assembled), zip);
    }

    /// <summary>The bytes in the bucket and the row in the API's own packet store, the case pinned with it — what the job worker writes.</summary>
    private async Task StorePacketAsync(Packet packet, byte[] zip)
    {
        await Service<IDocumentBlobStore>().PutBytesAsync(packet.StorageRef, zip, Forms.Packets.PacketContentType);
        var stored = (await Service<ICaseStore>().ReadForWorkerAsync(CaseId))!;
        var pinned = stored with { FormRevisions = packet.FormRevisions, ConstantsSetId = packet.ConstantsSetId };
        Assert.True(await Service<IPacketStore>().CreateAsync(Forms.Packets.ToItem(packet), pinned, stored.UpdatedAt));
    }

    /// <summary>The reference case, in flmb, under <paramref name="caseId"/>, owned by the firm.</summary>
    private static CaseData Reference(string caseId)
    {
        var file = CaseFiles.Named("reference-ch7");
        var district = CourtRegistry.Shipped.Latest.District(Court)!;
        var data = CaseDatas.Of(file with { Case = file.Case with { Id = caseId, FirmId = Firm, CreatedBy = Attorney, Court = Court, Division = district.Divisions[0].Code, District = district.Name } });
        return data with { Debtors = [.. data.Debtors.Select(debtor => debtor with { CaseId = caseId, ClientId = null, CaseCreatedAt = null })] };
    }

    private static (byte[] Zip, AssembledPacket Assembled) AssembleReference()
    {
        var data = Reference("11111111-1111-4111-8111-111111111111");
        var (assembled, problems) = PacketAssembly.Assemble(data, DateOnly.FromDateTime(DateTime.UtcNow), DateTimeOffset.UtcNow, new OutputOptions());
        Assert.Empty(problems);
        return (PacketAssembly.PacketZip(assembled!.Parts), assembled);
    }

    // ── the API ──

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, string? token = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Tokens.Staff(Attorney));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async Task<(int Status, JsonObject? Body)> CallAsync(HttpMethod method, string path, string? json = null, string? token = null)
    {
        using var response = await SendAsync(method, path, json, token);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return ((int)response.StatusCode, text.StartsWith('{') ? JsonNode.Parse(text)!.AsObject() : null);
    }

    public string ApprovalPath => $"/v1/cases/{CaseId}/filing-approval";

    private async Task SignAsync()
    {
        var (_, view) = await CallAsync(HttpMethod.Get, "/v1/me/filing-authorization");
        var text = view!["text"]!;
        var (status, body) = await CallAsync(HttpMethod.Post, "/v1/me/filing-authorization", $$"""{"text_version": "{{text["version"]}}", "text_digest": "{{text["digest"]}}"}""");
        Assert.True(status == 201, body?.ToJsonString());
    }

    private async Task<string> EnrolAsync(string login, string password, string seed)
    {
        var (status, body) = await CallAsync(HttpMethod.Post, "/v1/me/filing-credentials", new JsonObject { ["login"] = login, ["password"] = password, ["totp_seed"] = seed, ["courts"] = new JsonArray("flmb") }.ToJsonString());
        Assert.True(status == 201, body?.ToJsonString());
        return body!["id"]!.GetValue<string>();
    }

    /// <summary>What the screen shows: the basis and its digest.</summary>
    public async Task<JsonObject> ReadAsync()
    {
        var (status, body) = await CallAsync(HttpMethod.Get, ApprovalPath);
        Assert.True(status == 200, body?.ToJsonString());
        return body!;
    }

    /// <summary>Approve what the screen shows, freshly signed in — and the exact body the API put on the queue.</summary>
    public async Task<(FilingApproval Approval, string Body)> ApproveAsync()
    {
        var digest = (await ReadAsync())["basis"]!["digest"]!.GetValue<string>();
        var (status, body) = await CallAsync(HttpMethod.Post, ApprovalPath, $$"""{"digest": "{{digest}}"}""");
        Assert.True(status == 201, body?.ToJsonString());
        var approvalId = body!["approval"]!["id"]!.GetValue<string>();
        var approval = (await Service<IFilingApprovalStore>().GetAsync(CaseId, approvalId))!;
        return (approval, Queue.Messages[^1]);
    }

    // ── the worker ──

    public FilingDeps Deps()
    {
        var fence = HostFence.For(Environment);
        var baseUrl = CourtFake.BaseUrl;
        return new FilingDeps
        {
            Environment = Environment,
            Filings = Service<IFilingStore>(),
            Approvals = Service<IFilingApprovalStore>(),
            Cases = Service<ICaseStore>(),
            Debtors = Service<IDebtorStore>(),
            Entities = Service<ICaseEntityStore>(),
            Packets = Service<IPacketStore>(),
            Blobs = Service<IDocumentBlobStore>(),
            Documents = Service<IDocumentStore>(),
            Credentials = Service<IFilingCredentialStore>(),
            Authorizations = Service<IFilingAuthorizationStore>(),

            // THE OPENER — the worker's, composed here and never in the API.
            Opener = new LocalFilingCredentialOpener(),
            AccessLog = AccessLog,
            TaxIds = Service<ITaxIdStore>(),
            TaxIdCipher = Service<ITaxIdCipher>(),
            KillSwitch = KillSwitch,
            Fence = fence,
            Http = () => new FencedHttpClient(fence, TimeSpan.FromSeconds(1)),
            Log = Log,
            FakeDriver = NoDriver ? null : Driver ?? (() => new FakeCmEcfDriver(baseUrl)),
            Clock = Clock,
            LeaseSeconds = LeaseSeconds,
        };
    }

    public Task<FilingResult> RunAsync(string body) => FilingWorker.RunAsync(body, Deps(), TestContext.Current.CancellationToken);

    public async Task<Core.Filings.Filing> FilingAsync(FilingApproval approval) =>
        (await Service<IFilingStore>().GetAsync(CaseId, approval.FilingId))!;

    public Task<Case?> CaseAsync() => Service<ICaseStore>().ReadForWorkerAsync(CaseId);

    public IReadOnlyList<(AccessAction Action, AccessOutcome Outcome, string? Purpose)> Actions() =>
        [.. AccessLog.Events.Select(e => (e.Action, e.Outcome, e.Purpose))];

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }
}
