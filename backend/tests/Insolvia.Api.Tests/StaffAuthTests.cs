using System.Text.Json.Nodes;
using Insolvia.Api.Logging;
using Insolvia.Api.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using static Insolvia.Api.Tests.Support.HttpAssert;

namespace Insolvia.Api.Tests;

// Translated from services/api tests/unit/test_auth.py — the parts that are
// the auth glue rather than /v1/me (that route is 18.14's). The probe route
// /test/staff stands where /v1/me stood.
public sealed class StaffAuthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    public static TheoryData<string> MalformedHeaders() =>
    [
        "Bearer",
        "Bearer ",
        "Basic abcdef",
        "Token abcdef",
        "eyJhbGciOiJSUzI1NiJ9.e30.c2ln", // a token with no scheme at all
        "Bearer a b",
        string.Empty,
        "   ",
    ];

    [Fact]
    public async Task A_valid_access_token_reaches_the_route_with_its_subject()
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();

        var answer = await client.Get("/test/staff", Tokens.Make());

        Assert.Equal(200, answer.Status);
        Assert.Equal(Ids.Alice, JsonNode.Parse(answer.Body)!["subject"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(MalformedHeaders))]
    public async Task A_malformed_authorization_header_is_rejected(string header)
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();

        Unauthorized(await client.SendAsync(HttpMethod.Get, "/test/staff", authorization: header));
    }

    [Fact]
    public async Task A_mutated_payload_is_rejected()
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();
        var parts = Tokens.Make().Split('.');
        var other = Tokens.Make(Ids.Stranger).Split('.');

        Unauthorized(await client.Get("/test/staff", $"{parts[0]}.{other[1]}.{parts[2]}"));
    }

    [Fact]
    public async Task A_token_without_a_subject_or_kid_or_with_a_future_nbf_is_rejected()
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();
        var future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;

        Unauthorized(await client.Get("/test/staff", Tokens.Make(withSubject: false)));
        Unauthorized(await client.Get("/test/staff", Tokens.Make(kid: string.Empty)));
        Unauthorized(await client.Get("/test/staff", Tokens.Make(extra: new JsonObject { ["nbf"] = future })));
    }

    // ── fail closed ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, Tokens.StaffClientId)]
    [InlineData(Tokens.Issuer, null)]
    public async Task Missing_auth_config_fails_closed(string? issuer, string? clientId)
    {
        using var host = new AuthHost(factory, new Dictionary<string, string?> { ["AUTH_ISSUER_URL"] = issuer, ["AUTH_CLIENT_ID"] = clientId });
        using var client = host.CreateClient();

        Unauthorized(await client.Get("/test/staff", Tokens.Make()));
        Assert.Equal("not_configured", host.Logs.In(StructuredLog.Categories.Auth).Single().Fields["reason"]);
    }

    [Fact]
    public async Task A_missing_jwks_provider_fails_closed()
    {
        using var host = new AuthHost(factory, composeJwks: false);
        using var client = host.CreateClient();

        Unauthorized(await client.Get("/test/staff", Tokens.Make()));
    }

    [Fact]
    public async Task Health_still_works_without_auth_config()
    {
        using var host = new AuthHost(factory, new Dictionary<string, string?> { ["AUTH_ISSUER_URL"] = null, ["AUTH_CLIENT_ID"] = null });
        using var client = host.CreateClient();

        Assert.Equal(200, (await client.Get("/health")).Status);
    }

    [Fact]
    public async Task An_uncomposed_firm_store_is_a_500_not_a_403()
    {
        // resolve_accessor raises rather than returning None: a deployment
        // that cannot resolve anyone must not look like nobody is provisioned.
        using var host = new AuthHost(factory, composeFirmStore: false);
        using var client = host.CreateClient();

        Json(await client.Get("/test/staff/firm", Tokens.Make()), 500, ErrorBody("InternalError", "request failed"));
    }

    // ── the public routes must not regress ───────────────────────────

    [Fact]
    public async Task A_bogus_bearer_header_does_not_break_a_public_route()
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();

        Assert.Equal(200, (await client.Get("/health", "nope")).Status);
        Assert.Empty(host.Logs.In(StructuredLog.Categories.Auth));
    }

    // ── order is load-bearing ────────────────────────────────────────

    [Fact]
    public async Task A_permission_filter_before_authentication_is_a_500_never_a_pass()
    {
        // Flask's `@requires` stacked above `@require_auth` reached
        // current_principal() with nothing on `g` and raised. The filter
        // order here is the decorator order, so the same mistake is the
        // same 500 — with a valid token and without one.
        await using var seeded = new SeededHost();
        await seeded.InitializeAsync();
        using var client = seeded.Host.CreateClient();

        Json(await client.Get("/test/order/requires-first", Tokens.Staff(Ids.Alice)), 500, ErrorBody("InternalError", "request failed"));
        Json(await client.Get("/test/order/requires-first"), 500, ErrorBody("InternalError", "request failed"));
    }

    // ── the log line must never carry the token or a claim ───────────

    [Fact]
    public async Task A_rejection_logs_a_category_and_nothing_sensitive()
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();
        var token = Tokens.Make(expiresIn: -120);

        await client.Get("/test/staff", token);

        var entry = Assert.Single(host.Logs.In(StructuredLog.Categories.Auth));
        Assert.Equal("authentication rejected", entry.Message);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("expired", entry.Fields["reason"]);
        foreach (var logged in host.Logs.Entries)
        {
            Assert.DoesNotContain(token, logged.Rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(Ids.Alice, logged.Rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_permission_refusal_logs_the_feature_never_the_subject()
    {
        await using var seeded = new SeededHost();
        await seeded.InitializeAsync();
        using var client = seeded.Host.CreateClient();

        await client.Get("/test/staff/portal-admin", Tokens.Staff(Ids.Bob));

        var entry = Assert.Single(seeded.Host.Logs.In(StructuredLog.Categories.Auth));
        Assert.Equal("permission refused", entry.Message);
        Assert.Equal("client_portal", entry.Fields["feature"]);
        Assert.DoesNotContain(Ids.Bob, entry.Rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Ids.Stranger, "no_active_firm_user")]
    [InlineData(Ids.Dana, "no_active_firm_user")]
    [InlineData(Ids.Sam, "firm_not_active")]
    public async Task An_unresolved_accessor_logs_why(string subject, string reason)
    {
        await using var seeded = new SeededHost();
        await seeded.InitializeAsync();
        using var client = seeded.Host.CreateClient();

        await client.Get("/test/staff/firm", Tokens.Staff(subject));

        var entry = Assert.Single(seeded.Host.Logs.In(StructuredLog.Categories.Auth));
        Assert.Equal(("accessor unresolved", reason), (entry.Message, entry.Fields["reason"]));
    }
}
