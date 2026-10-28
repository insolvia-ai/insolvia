using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Insolvia.Admin.Configuration;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Insolvia.Admin.Tests.Support;

/// <summary>
/// The admin service over memory adapters, with REAL staff tokens — the C# of
/// services/admin tests/conftest.py. Tokens are signed here with a keypair
/// made for the run and verified through the production code path (a static
/// JWKS provider serving the public half). Every identifier is obviously
/// fake; this repo is public.
/// </summary>
internal sealed class AdminHost : IDisposable
{
    public const string ClientId = "000000000000-fake.apps.googleusercontent.com";
    public const string Workspace = "example-workspace.test";
    public const string StaffSub = "100000000000000000001";
    public const string StaffEmail = "operator@example-workspace.test";
    public const string Kid = "test-key";

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    private readonly WebApplicationFactory<Program> _factory;

    public AdminHost(
        WebApplicationFactory<Program> factory,
        string? clientId = ClientId,
        bool composeJwks = true,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        _factory = factory.WithWebHostBuilder(host =>
        {
            host.UseSetting("INSOLVIA_ENV", "local");
            host.UseSetting("INSOLVIA_DB_CONNECTION_STRING", null);
            host.UseSetting("FIRM_USER_POOL_ID", null);
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                host.UseSetting(key, value);
            }

            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<AdminConfig>();
                services.AddSingleton(services => AdminConfig.Load(_ => null) with
                {
                    Environment = Insolvia.Admin.InsolviaEnvironment.FromVariable(settings?.GetValueOrDefault("INSOLVIA_ENV") ?? "local"),
                    GoogleClientId = clientId,
                    WorkspaceDomain = Workspace,
                });
                services.RemoveAll<IJwksProvider>();
                if (composeJwks)
                {
                    services.AddSingleton<IJwksProvider>(new StaticJwksProvider(new Dictionary<string, SecurityKey> { [Kid] = PublicKey }));
                }

                services.RemoveAll<IFirmStore>();
                services.AddSingleton<IFirmStore>(Firms);
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAdminAuditLog>();
                services.AddSingleton<IAdminAuditLog>(Audit);
            });
        });
    }

    public static SecurityKey PublicKey { get; } = new RsaSecurityKey(Rsa.ExportParameters(false)) { KeyId = Kid };

    public MemoryFirmStore Firms { get; } = new();

    public MemoryUserDirectory Directory { get; } = new();

    public MemoryAdminAuditLog Audit { get; } = new();

    public static string Sign(JsonObject claims) =>
        Handler.CreateToken(claims.ToJsonString(), new SigningCredentials(new RsaSecurityKey(Rsa) { KeyId = Kid }, SecurityAlgorithms.RsaSha256));

    /// <summary>A valid Workspace ID token, with <paramref name="overrides"/> replacing (null: removing) claims.</summary>
    public static string StaffToken(JsonObject? overrides = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new JsonObject
        {
            ["iss"] = "https://accounts.google.com",
            ["aud"] = ClientId,
            ["sub"] = StaffSub,
            ["hd"] = Workspace,
            ["email"] = StaffEmail,
            ["email_verified"] = true,
            ["iat"] = now,
            ["exp"] = now + 3600,
        };
        foreach (var (name, value) in overrides ?? [])
        {
            if (value is null)
            {
                claims.Remove(name);
            }
            else
            {
                claims[name] = value.DeepClone();
            }
        }

        return Sign(claims);
    }

    public HttpClient CreateClient() => _factory.CreateClient();

    public void Dispose() => _factory.Dispose();
}

internal static class Requests
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Answer> SendAsync(
        this HttpClient client,
        HttpMethod method,
        string path,
        string? bearer = null,
        JsonNode? json = null,
        IReadOnlyDictionary<string, string>? headers = null,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (bearer is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json.ToJsonString(), MediaTypeHeaderValue.Parse("application/json"));
        }
        else if (content is not null)
        {
            request.Content = content;
        }

        var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        return new Answer((int)response.StatusCode, body, response);
    }

    public static Task<Answer> Get(this HttpClient client, string path, string? bearer = null) =>
        client.SendAsync(HttpMethod.Get, path, bearer);

    public static void Unauthorized(Answer answer)
    {
        Assert.Equal(401, answer.Status);
        Assert.Equal("""{"error":"Unauthorized","message":"authentication required"}""", answer.Body);
        Assert.Equal("application/json", answer.Response.Content.Headers.ContentType?.ToString());
    }
}

internal sealed record Answer(int Status, string Body, HttpResponseMessage Response)
{
    public JsonNode Json => JsonNode.Parse(Body)!;
}
