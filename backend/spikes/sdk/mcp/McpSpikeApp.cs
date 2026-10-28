using System.ComponentModel;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpSpike;

public sealed record SpikeSettings(
    string ResourceUrl,
    string Issuer,
    string[] ClientIds,
    SecurityKey SigningKey,
    string[] AllowedHosts);

/// <summary>
/// The seam the real service needs: our own per-request token verification
/// (insolvia_core.auth's job in Python). Here: RS256 + issuer + token_use +
/// client_id allow-list, exactly the checks the Python verifier makes.
/// </summary>
public interface ITokenVerifier
{
    Task<ClaimsPrincipal?> VerifyAsync(string token);
}

public sealed class SpikeTokenVerifier(SpikeSettings settings) : ITokenVerifier
{
    public async Task<ClaimsPrincipal?> VerifyAsync(string token)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = false, // Cognito access tokens carry no aud: client_id IS the audience check.
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKey = settings.SigningKey,
        });
        if (!result.IsValid) return null;
        var jwt = (JsonWebToken)result.SecurityToken;
        if (jwt.GetClaim("token_use")?.Value != "access") return null;
        var clientId = jwt.GetClaim("client_id")?.Value;
        if (clientId is null || !settings.ClientIds.Contains(clientId)) return null;
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", jwt.Subject), new Claim("client_id", clientId)], "InsolviaBearer"));
    }
}

/// <summary>Authenticate = our verifier. Challenge is delegated to the MCP scheme (RFC 9728).</summary>
public sealed class InsolviaBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    ITokenVerifier verifier)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var principal = await verifier.VerifyAsync(header["Bearer ".Length..].Trim());
        return principal is null
            ? AuthenticateResult.Fail("rejected") // category only; never the token
            : AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

public static class McpSpikeApp
{
    public static WebApplication Build(SpikeSettings settings, string[]? args = null, string? url = null,
        bool jsonResponseShim = true)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        if (url is not null) builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        // The 2026-07-28 request ceiling in mcp-surface.md (256 KiB).
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 256 * 1024);

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<ITokenVerifier, SpikeTokenVerifier>();
        builder.Services.AddHttpContextAccessor();
        builder.Services
            .AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = "InsolviaBearer";
                o.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, InsolviaBearerHandler>("InsolviaBearer", null)
            .AddMcp(o =>
            {
                o.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = settings.ResourceUrl,
                    AuthorizationServers = [settings.Issuer],
                };
            });
        builder.Services.AddAuthorization();
        builder.Services
            .AddMcpServer(o => o.ServerInstructions = "Spike: whoami and list_cases over canned data.")
            .WithHttpTransport(o => o.SessionMode = ModelContextProtocol.AspNetCore.HttpServerSessionMode.Stateless)
            .WithTools<SpikeTools>();

        var app = builder.Build();

        // The spec's DNS-rebinding MUST: pin Host to this environment's name -> 421
        // (Python SDK behaviour pinned by test_a_wrong_host_header_is_rejected).
        app.Use(async (ctx, next) =>
        {
            var host = ctx.Request.Host.ToString();
            if (!settings.AllowedHosts.Any(p => HostMatches(p, host)))
            {
                ctx.Response.StatusCode = 421;
                return;
            }
            await next();
        });
        if (jsonResponseShim) app.UseMiddleware<JsonResponseShim>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp("/mcp").RequireAuthorization();
        return app;
    }

    private static bool HostMatches(string pattern, string host) =>
        pattern.EndsWith(":*") ? host.StartsWith(pattern[..^1]) : host == pattern;
}

public sealed record WhoamiResult(Firm? Firm, string? DisplayName);
public sealed record Firm(string Id, string Name);
public sealed record CaseSummary(string Id, string Status, string Court);
public sealed record ListCasesResult(IReadOnlyList<CaseSummary> Cases, string? NextCursor);

[McpServerToolType]
public sealed class SpikeTools(IHttpContextAccessor http)
{
    private string Subject => http.HttpContext!.User.FindFirst("sub")!.Value;

    [McpServerTool(Name = "whoami", Title = "Who am I", ReadOnly = true, UseStructuredContent = true)]
    [Description("The caller's firm, display name, and per-feature permissions.")]
    public WhoamiResult Whoami() =>
        new(new Firm("firm-1", "Example Firm"), $"User {Subject}");

    [McpServerTool(Name = "list_cases", Title = "List cases", ReadOnly = true, UseStructuredContent = true,
        OutputSchemaType = typeof(ListCasesResult))]
    [Description("The cases the caller may see, newest first. Optional status filter.")]
    public CallToolResult ListCases(string? status = null, int? limit = null, string? cursor = null)
    {
        string[] valid = ["intake", "ready_to_file", "filed", "discharged", "dismissed", "closed"];
        if (Subject == "no-firm-subject")
            return Refusal("permission_denied", "your account is not active in a firm");
        if (status is not null && !valid.Contains(status))
            return Refusal("validation_failed", "invalid status", ["status"]);
        var all = new[]
        {
            new CaseSummary("case-2", "intake", "flmb"),
            new CaseSummary("case-1", "filed", "flmb"),
        };
        var cases = all.Where(c => status is null || c.Status == status).ToList();
        return new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new ListCasesResult(cases, null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new ListCasesResult(cases, null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) }],
        };
    }

    // The {error: {code, message, fields?}} envelope of api/results.py.
    private static CallToolResult Refusal(string code, string message, string[]? fields = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (fields is not null) error["fields"] = new JsonArray(fields.Select(f => (JsonNode)f).ToArray());
        var body = new JsonObject { ["error"] = error };
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(body),
            Content = [new TextContentBlock { Text = body.ToJsonString() }],
        };
    }
}

/// <summary>
/// The C# SDK 2.3.0 answers every POST as a one-event SSE stream; it has no
/// "JSON response" switch (the Python server runs json_response=True). In stateless
/// mode with no server-to-client requests, that stream always carries exactly one
/// JSON-RPC message, so this shim buffers the body and re-emits it as application/json.
/// Anything that is not exactly one event passes through untouched.
/// </summary>
public sealed class JsonResponseShim(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method)) { await next(ctx); return; }
        var original = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()!;
        var buffer = new MemoryStream();
        var feature = new BufferingBodyFeature(original, buffer);
        ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(feature);
        try { await next(ctx); }
        finally { ctx.Features.Set(original); }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var isSse = ctx.Response.ContentType?.StartsWith("text/event-stream") == true;
        if (isSse && ctx.Response.StatusCode == 200)
        {
            var data = text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].TrimStart()).ToList();
            if (data.Count == 1)
            {
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength = Encoding.UTF8.GetByteCount(data[0]);
                await original.Stream.WriteAsync(Encoding.UTF8.GetBytes(data[0]));
                return;
            }
        }
        if (buffer.Length > 0)
        {
            ctx.Response.ContentLength = buffer.Length;
            await original.Stream.WriteAsync(buffer.ToArray());
        }
    }

    private sealed class BufferingBodyFeature(
        Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature prior, Stream buffer)
        : Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature
    {
        public Stream Stream => buffer;
        public System.IO.Pipelines.PipeWriter Writer { get; } = System.IO.Pipelines.PipeWriter.Create(buffer);
        public void DisableBuffering() { }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken ct = default) =>
            prior.SendFileAsync(path, offset, count, ct);
        public async Task CompleteAsync() { await Writer.FlushAsync(); }
    }
}
