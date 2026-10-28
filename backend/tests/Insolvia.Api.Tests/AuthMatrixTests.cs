using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using static Insolvia.Api.Tests.Support.HttpAssert;

namespace Insolvia.Api.Tests;

/// <summary>
/// The whole 401/403 matrix, through the production filters and pipeline on
/// test-only routes (<see cref="ProbeRoutes"/>). Each row's status AND body is
/// pinned to what services/api answers for the same request: the 401 body is
/// test_auth.py's <c>assert_unauthorized</c>, the 403 bodies are
/// app_factory.py's <c>ForbiddenError</c> handler around auth.py's and
/// client_auth.py's messages.
/// </summary>
public sealed class AuthMatrixTests(SeededHost seeded) : IClassFixture<SeededHost>
{
    private const string NotInAFirm = """{"error":"ForbiddenError","message":"your account is not active in a firm"}""";
    private const string NotGranted = """{"error":"ForbiddenError","message":"your firm has not granted you access to this"}""";
    private const string NoPortalAccess = """{"error":"ForbiddenError","message":"you do not have access to a case through the client portal"}""";

    private AuthHost Host => seeded.Host;

    // label, method, path, 403 body (null = the 401). The credential is
    // minted per run from the label (Credential below): theory data is
    // enumerated at discovery, which may be another process with another
    // keypair.
    public static TheoryData<string, string, string, string?> Rows() => new()
    {
        { "no token", "GET", "/test/staff", null },
        { "malformed header", "GET", "/test/staff", null },
        { "malformed token", "GET", "/test/staff", null },
        { "wrong issuer", "GET", "/test/staff", null },
        { "ID token", "GET", "/test/staff", null },
        { "wrong client id", "GET", "/test/staff", null },
        { "expired", "GET", "/test/staff", null },
        { "unknown kid", "GET", "/test/staff", null },
        { "tampered signature", "GET", "/test/staff", null },
        { "alg none", "GET", "/test/staff", null },
        { "portal token on a staff route", "GET", "/test/staff", null },
        { "portal token on a permission route", "POST", "/test/staff/cases", null },
        { "staff token on a portal route", "GET", "/test/portal", null },
        { "no token on a portal route", "GET", "/test/portal", null },
        { "expired portal token", "GET", "/test/portal", null },
        { "no firm user", "GET", "/test/staff/firm", NotInAFirm },
        { "suspended (disabled) user", "GET", "/test/staff/firm", NotInAFirm },
        { "suspended user, permission route", "POST", "/test/staff/cases", NotInAFirm },
        { "user of a suspended firm", "GET", "/test/staff/firm", NotInAFirm },
        { "missing feature", "GET", "/test/staff/portal-admin", NotGranted },
        { "insufficient level", "POST", "/test/staff/cases", NotGranted },
        { "unbound portal client", "GET", "/test/portal", NoPortalAccess },
        { "revoked portal binding", "GET", "/test/portal", NoPortalAccess },
        { "portal client of a suspended firm", "GET", "/test/portal", NoPortalAccess },
    };

    private static string? Credential(string label) => label switch
    {
        "no token" or "no token on a portal route" => null,
        "malformed header" => "Basic abcdef",
        "malformed token" => "Bearer not.a.jwt",
        "wrong issuer" => "Bearer " + Tokens.Make(issuer: Tokens.OtherIssuer),
        "ID token" => "Bearer " + Tokens.Make(tokenUse: "id"),
        "wrong client id" => "Bearer " + Tokens.Make(clientId: Tokens.OtherClientId),
        "expired" => "Bearer " + Tokens.Make(expiresIn: -120),
        "unknown kid" => "Bearer " + Tokens.Make(kid: Tokens.UnknownKid),
        "tampered signature" => "Bearer " + Tokens.Make(imposter: true),
        "alg none" => "Bearer " + Tokens.Unsigned(
            $$"""{"alg":"none","kid":"{{Tokens.Kid}}"}""",
            $$"""{"iss":"{{Tokens.Issuer}}","sub":"{{Ids.Alice}}","client_id":"{{Tokens.StaffClientId}}","token_use":"access","exp":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60}}}"""),
        "portal token on a staff route" or "portal token on a permission route" => "Bearer " + Tokens.Portal(Ids.Pat),
        "staff token on a portal route" => "Bearer " + Tokens.Staff(Ids.Alice),
        "expired portal token" => "Bearer " + Tokens.Make(Ids.Pat, Tokens.PortalClientId, expiresIn: -120),
        "no firm user" => "Bearer " + Tokens.Staff(Ids.Stranger),
        "suspended (disabled) user" or "suspended user, permission route" => "Bearer " + Tokens.Staff(Ids.Dana),
        "user of a suspended firm" => "Bearer " + Tokens.Staff(Ids.Sam),
        "missing feature" => "Bearer " + Tokens.Staff(Ids.Bob),
        "insufficient level" => "Bearer " + Tokens.Staff(Ids.Erin),
        "unbound portal client" => "Bearer " + Tokens.Portal(Ids.Stranger),
        "revoked portal binding" => "Bearer " + Tokens.Portal(Ids.Quinn),
        "portal client of a suspended firm" => "Bearer " + Tokens.Portal(Ids.Ray),
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "no credential for this row"),
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Every_refusal_answers_its_status_and_body(string label, string method, string path, string? forbiddenBody)
    {
        using var client = Host.CreateClient();

        var answer = await client.SendAsync(new HttpMethod(method), path, authorization: Credential(label));

        if (forbiddenBody is null)
        {
            Unauthorized(answer);
        }
        else
        {
            Json(answer, 403, forbiddenBody);
        }
    }

    [Theory]
    [InlineData("GET", "/test/staff")]
    [InlineData("GET", "/test/staff/me")]
    [InlineData("GET", "/test/staff/firm")]
    [InlineData("POST", "/test/staff/cases")]
    public async Task An_admin_in_an_active_firm_passes_every_staff_gate(string method, string path)
    {
        using var client = Host.CreateClient();

        var answer = await client.SendAsync(new HttpMethod(method), path, Tokens.Staff(Ids.Alice));

        Assert.Equal(200, answer.Status);
    }

    [Fact]
    public async Task A_member_granted_the_level_passes_the_permission_gate()
    {
        using var client = Host.CreateClient();

        Assert.Equal(200, (await client.SendAsync(HttpMethod.Post, "/test/staff/cases", Tokens.Staff(Ids.Bob))).Status);
    }

    [Fact]
    public async Task Signed_in_with_no_firm_is_a_state_on_the_me_shape_not_an_error()
    {
        // test_me_omits_the_firm_for_someone_not_in_one: the reason accessor
        // resolution is a separate step from authentication.
        using var client = Host.CreateClient();

        var answer = await client.Get("/test/staff/me", Tokens.Staff(Ids.Stranger));

        Assert.Equal(200, answer.Status);
        Assert.Null(JsonNode.Parse(answer.Body)!["firm"]);
        Assert.Equal(403, (await client.Get("/test/staff/firm", Tokens.Staff(Ids.Stranger))).Status);
    }

    [Fact]
    public async Task A_portal_client_signed_in_to_the_staff_app_resolves_no_firm()
    {
        // test_a_client_signed_in_to_the_staff_app_resolves_no_firm: a staff
        // token for a debtor's subject is a staff principal with no firm.
        using var client = Host.CreateClient();

        var me = await client.Get("/test/staff/me", Tokens.Staff(Ids.Pat));

        Assert.Equal(200, me.Status);
        Assert.Null(JsonNode.Parse(me.Body)!["firm"]);
        Assert.Equal(403, (await client.Get("/test/staff/firm", Tokens.Staff(Ids.Pat))).Status);
    }
}
