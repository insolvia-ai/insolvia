using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Debtors;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Insolvia.Api.Tests.Support;

/// <summary>
/// The two-firm fixture the Python route suites build per test
/// (test_firm_routes.py's <c>firms</c> + <c>client</c> fixtures): one fresh
/// API, memory stores, tokens signed for real. Each test owns one, so a
/// write in one test is never seen by another.
/// </summary>
internal sealed class FirmWorld : IDisposable
{
    public static readonly DateTimeOffset Seeded = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public FirmWorld(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        Host = new AuthHost(_factory, settings, services: services);
        _client = Host.CreateClient();
    }

    public AuthHost Host { get; }

    public Core.Memory.MemoryFirmStore Firms => Host.Firms;

    public static FirmUser Member(
        string subject,
        string firmId = Ids.FirmA,
        FirmRole role = FirmRole.Attorney,
        bool isAdmin = false,
        bool accessAllCases = false,
        FirmUserStatus status = FirmUserStatus.Active,
        string firstName = "Person",
        string? lastName = null,
        IReadOnlyDictionary<Feature, PermissionLevel>? permissions = null) =>
        new(
            firmId,
            subject,
            subject[^4..] + "@example.test",
            firstName,
            lastName ?? subject[^4..],
            role,
            isAdmin,
            accessAllCases,
            permissions is null ? Permissions.DefaultFor(role) : permissions.ToValueDictionary(),
            status,
            Seeded,
            Seeded);

    public static ValueDictionary<Feature, PermissionLevel> DefaultsWith(FirmRole role, Feature feature, PermissionLevel level)
    {
        var map = Permissions.DefaultFor(role).ToDictionary(pair => pair.Key, pair => pair.Value);
        map[feature] = level;
        return map.ToValueDictionary();
    }

    public async Task<FirmWorld> WithFirmsAsync(string nameA = "Example & Partners", string nameB = "Other Firm LLP")
    {
        await Firms.CreateFirmAsync(new Firm(Ids.FirmA, nameA, FirmStatus.Active, Seeded, Seeded), HttpAssert.Ct);
        await Firms.CreateFirmAsync(new Firm(Ids.FirmB, nameB, FirmStatus.Active, Seeded, Seeded), HttpAssert.Ct);
        return this;
    }

    public async Task<FirmWorld> WithAsync(params FirmUser[] users)
    {
        foreach (var user in users)
        {
            await Firms.AddUserAsync(user, HttpAssert.Ct);
        }

        return this;
    }

    public Task<FirmUser?> UserAsync(string subject, string firmId = Ids.FirmA) => Firms.GetUserAsync(firmId, subject, HttpAssert.Ct);

    public Task<Firm?> FirmAsync(string firmId = Ids.FirmA) => Firms.GetFirmAsync(firmId, HttpAssert.Ct);

    public Task<IReadOnlyList<FirmUser>> UsersAsync(string firmId = Ids.FirmA) => Firms.ListUsersAsync(firmId, HttpAssert.Ct);

    /// <summary>Overwrite a stored member in place — the Python tests' <c>firms.users[(firm, subject)] = …</c>.</summary>
    public async Task ReplaceAsync(FirmUser user) => Assert.NotNull(await Firms.UpdateUserAsync(user, HttpAssert.Ct));

    public Task<Answer> Get(string path, string? subject = null) => SendAsync(HttpMethod.Get, path, subject);

    public Task<Answer> Delete(string path, string? subject = null) => SendAsync(HttpMethod.Delete, path, subject);

    public Task<Answer> Post(string path, string? json, string? subject = null) => SendAsync(HttpMethod.Post, path, subject, json);

    public Task<Answer> Put(string path, string? json, string? subject = null) => SendAsync(HttpMethod.Put, path, subject, json);

    public Task<Answer> Patch(string path, string? json, string? subject = null) => SendAsync(HttpMethod.Patch, path, subject, json);

    public async Task<Answer> SendAsync(
        HttpMethod method,
        string path,
        string? subject = null,
        string? json = null,
        string contentType = "application/json",
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (subject is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Tokens.Staff(subject));
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (json is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        var response = await _client.SendAsync(request, HttpAssert.Ct);
        var body = await response.Content.ReadAsStringAsync(HttpAssert.Ct);
        return new Answer((int)response.StatusCode, body, response);
    }

    public void Dispose()
    {
        _client.Dispose();
        Host.Dispose();
        _factory.Dispose();
    }
}

/// <summary>One response: status, raw body, and the body parsed (null when it is not JSON).</summary>
internal sealed record Answer(int Status, string Body, HttpResponseMessage Response)
{
    public JsonNode? Json => Body.Length > 0 && (Body[0] == '{' || Body[0] == '[') ? JsonNode.Parse(Body) : null;

    public JsonObject Object => Json?.AsObject() ?? throw new InvalidOperationException($"not a JSON object: {Status} {Body}");

    public string? ContentType => Response.Content.Headers.ContentType?.ToString();

    /// <summary>The key set of the body (or of <paramref name="node"/>).</summary>
    public static IReadOnlyList<string> Keys(JsonNode? node) => [.. node!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    public IReadOnlyList<string> FieldErrors => Status == 400 && Object["fields"] is JsonObject fields
        ? [.. fields.Select(pair => pair.Key)]
        : throw new InvalidOperationException($"not a field-validation answer: {Status} {Body}");

    public string? Str(string key) => Object[key]?.GetValue<string>();
}

/// <summary>
/// The debtor store's client links, in memory (the Core merge tests' fake):
/// what the Python merge route tests reached by opening real cases, which
/// this host cannot do until the case routes land (18.15). A case is a set
/// of (role → client) links.
/// </summary>
internal sealed class FakeDebtorClientLinks : IDebtorClientLinks
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, FilingRole Role), string?> _links = [];
    private int _cases;

    /// <summary>A case whose debtor 1 (and debtor 2) are the given clients.</summary>
    public string CaseFor(params string[] clientIds)
    {
        lock (_gate)
        {
            var caseId = $"case-{++_cases}";
            FilingRole[] roles = [FilingRole.Debtor1, FilingRole.Debtor2];
            for (var index = 0; index < clientIds.Length; index++)
            {
                _links[(caseId, roles[index])] = clientIds[index];
            }

            return caseId;
        }
    }

    /// <summary>The cases a client is on, in case order — what <c>GET …/cases</c> listed.</summary>
    public IReadOnlyList<string> CasesOf(string clientId)
    {
        lock (_gate)
        {
            return [.. _links.Where(pair => pair.Value == clientId).Select(pair => pair.Key.CaseId).Distinct().Order(StringComparer.Ordinal)];
        }
    }

    public Task<IReadOnlyList<(string CaseId, FilingRole FilingRole)>> RolesForClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<(string, FilingRole)> roles = [.. _links
                .Where(pair => pair.Value == clientId)
                .Select(pair => pair.Key)
                .OrderBy(key => key.CaseId, StringComparer.Ordinal)
                .ThenBy(key => key.Role)];
            return Task.FromResult(roles);
        }
    }

    public Task<RepointOutcome> RepointClientAsync(string caseId, FilingRole filingRole, string fromClientId, string toClientId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_links.GetValueOrDefault((caseId, filingRole)) != fromClientId)
            {
                return Task.FromResult(RepointOutcome.Absent);
            }

            if (_links.Any(pair => pair.Key.CaseId == caseId && pair.Key.Role != filingRole && pair.Value == toClientId))
            {
                return Task.FromResult(RepointOutcome.ClientTaken);
            }

            _links[(caseId, filingRole)] = toClientId;
            return Task.FromResult(RepointOutcome.Written);
        }
    }

    public Task<IReadOnlyList<DebtorClientLink>> ClientLinksForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<DebtorClientLink> links = [.. _links
                .Where(pair => pair.Key.CaseId == caseId)
                .Select(pair => new DebtorClientLink(caseId, pair.Key.Role, pair.Value))];
            return Task.FromResult(links);
        }
    }
}
