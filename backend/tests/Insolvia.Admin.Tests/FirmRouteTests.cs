using System.Text.Json.Nodes;
using Insolvia.Admin.Tests.Support;
using Insolvia.Core.AdminAudit;
using Insolvia.Core.Firms;
using Microsoft.AspNetCore.Mvc.Testing;
using static Insolvia.Admin.Tests.Support.AdminHost;

namespace Insolvia.Admin.Tests;

// Translated from services/admin tests/unit/test_firm_routes.py: the six admin
// routes through the app over memory adapters. Two invariants get pride of
// place — the CROSS-ISSUER 401 (a firm-pool-shaped token must die in
// verification: the trust boundary the whole service rests on) and the AUDIT
// ROW on every mutation (#178's "record who provisioned what"), asserted per
// route. Every identifier is obviously fake; this repo is public.
public sealed class FirmRouteTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static Task<Answer> Provision(HttpClient client, string name = "Example & Partners", string email = "admin@example.test") =>
        client.SendAsync(HttpMethod.Post, "/v1/firms", StaffToken(), new JsonObject
        {
            ["name"] = name,
            ["admin"] = new JsonObject { ["email"] = email, ["firstName"] = "Alice", ["lastName"] = "Attorney" },
        });

    // ── The trust boundary ──────────────────────────────────────────

    [Fact]
    public async Task No_token_is_401()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        Requests.Unauthorized(await client.Get("/v1/firms"));
    }

    [Fact]
    public async Task A_firm_pool_shaped_token_is_401()
    {
        // THE CROSS-ISSUER INVARIANT. Exactly what the tenant API accepts —
        // token_use, client_id, a Cognito issuer, no aud, no hd — signed by a
        // key this service's provider actually serves, so what refuses it is
        // the Google profile's claim checks and nothing weaker.
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cognitoShaped = Sign(new JsonObject
        {
            ["iss"] = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_fake",
            ["sub"] = "00000000-0000-4000-8000-000000000001",
            ["token_use"] = "access",
            ["client_id"] = "fakepoolclientid",
            ["iat"] = now,
            ["exp"] = now + 3600,
        });

        Requests.Unauthorized(await client.Get("/v1/firms", cognitoShaped));
    }

    [Fact]
    public async Task A_personal_gmail_token_is_401()
    {
        // Internal-client marking is Google's gate; the hd check is ours. A
        // verified personal account carries NO hd claim, and absence reads
        // as refusal here too.
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        Requests.Unauthorized(await client.Get("/v1/firms", StaffToken(new JsonObject
        {
            ["hd"] = null,
            ["sub"] = "200000000000000000002",
            ["email"] = "someone@personal-mail.test",
        })));
    }

    [Fact]
    public async Task Another_workspace_and_another_audience_are_401()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        Requests.Unauthorized(await client.Get("/v1/firms", StaffToken(new JsonObject { ["hd"] = "another-workspace.test" })));
        Requests.Unauthorized(await client.Get("/v1/firms", StaffToken(new JsonObject { ["aud"] = "someone-else.apps.googleusercontent.com" })));
    }

    [Fact]
    public async Task No_google_client_id_fails_closed_and_health_still_answers()
    {
        using var host = new AdminHost(factory, clientId: null);
        using var client = host.CreateClient();

        Requests.Unauthorized(await client.Get("/v1/firms", StaffToken()));
        Assert.Equal(200, (await client.Get("/health")).Status);
    }

    [Fact]
    public async Task No_key_provider_fails_closed()
    {
        using var host = new AdminHost(factory, composeJwks: false);
        using var client = host.CreateClient();

        Requests.Unauthorized(await client.Get("/v1/firms", StaffToken()));
    }

    [Fact]
    public async Task Health_is_public()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var answer = await client.Get("/health");

        Assert.Equal(200, answer.Status);
        Assert.Equal("""{"environment":"local","status":"ok"}""", answer.Body);
        Assert.Equal("application/json", answer.Response.Content.Headers.ContentType?.ToString());
    }

    // ── Provision ───────────────────────────────────────────────────

    [Fact]
    public async Task Provisioning_creates_pool_account_rows_and_audit()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var answer = await Provision(client);

        Assert.Equal(201, answer.Status);
        var body = answer.Json;

        // Provenance is the staff caller, straight off the verified token.
        Assert.Equal(StaffSub, body["firm"]!["createdBy"]!.GetValue<string>());
        Assert.Equal(StaffEmail, body["firm"]!["createdByEmail"]!.GetValue<string>());
        Assert.Equal("active", body["firm"]!["status"]!.GetValue<string>());

        // The pool account exists and the row is keyed on ITS subject.
        Assert.True(host.Directory.Subjects.ContainsKey("admin@example.test"));
        Assert.Equal(host.Directory.Subjects["admin@example.test"], body["admin"]!["subject"]!.GetValue<string>());

        // First administrator BY CONSTRUCTION.
        Assert.True(body["admin"]!["isAdmin"]!.GetValue<bool>());
        var stored = await host.Firms.GetUserAsync(body["firm"]!["id"]!.GetValue<string>(), body["admin"]!["subject"]!.GetValue<string>(), Requests.Ct);
        Assert.NotNull(stored);
        Assert.True(stored.IsAdmin);

        // The row #178 demanded.
        var audit = Assert.Single(host.Audit.Events);
        Assert.Equal(AdminAction.FirmProvision, audit.Action);
        Assert.Equal(StaffSub, audit.Principal);
        Assert.Equal(StaffEmail, audit.PrincipalEmail);
        Assert.Contains("admin@example.test", audit.Detail, StringComparison.Ordinal);
        Assert.Equal(body["firm"]!["id"]!.GetValue<string>(), audit.FirmId);
    }

    [Fact]
    public async Task The_provision_body_is_flask_shaped()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var answer = await Provision(client);

        Assert.Equal("application/json", answer.Response.Content.Headers.ContentType?.ToString());
        var firm = (JsonObject)answer.Json["firm"]!;
        Assert.Equal(
            ["createdAt", "createdBy", "createdByEmail", "defaultChapter", "defaultCourt", "defaultDivision", "id", "letterhead", "name", "status", "updatedAt"],
            firm.Select(pair => pair.Key));
        Assert.Null(firm["defaultCourt"]);
        Assert.Contains("\"name\":\"Example & Partners\"", answer.Body, StringComparison.Ordinal);
        var admin = (JsonObject)answer.Json["admin"]!;
        Assert.Equal("Alice Attorney", admin["displayName"]!.GetValue<string>());
        Assert.Equal("attorney", admin["role"]!.GetValue<string>());
        Assert.Equal("active", admin["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_duplicate_admin_address_is_409_and_writes_nothing()
    {
        // Pool-account-first ordering observed from outside: the refusal
        // arrives before any firm exists, so a retry starts clean.
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        await host.Directory.CreateUserAsync("admin@example.test", Requests.Ct);

        var answer = await Provision(client);

        Assert.Equal(409, answer.Status);
        Assert.Equal("""{"error":"ConflictError","message":"that email address already has an Insolvia account"}""", answer.Body);
        Assert.Empty(host.Firms.Firms);
        Assert.Empty(host.Audit.Events);
    }

    [Fact]
    public async Task Provisioning_without_an_admin_is_a_field_error()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Post, "/v1/firms", StaffToken(), new JsonObject { ["name"] = "Example" });

        Assert.Equal(400, answer.Status);
        Assert.Equal("""{"error":"ValidationError","fields":{"admin":"A firm needs its first administrator: {email, firstName, lastName}."}}""", answer.Body);
    }

    [Fact]
    public async Task A_body_that_is_not_a_json_object_is_a_field_error()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        const string expected = """{"error":"ValidationError","fields":{"body":"A JSON object body is required."}}""";

        Assert.Equal(expected, (await client.SendAsync(HttpMethod.Post, "/v1/firms", StaffToken(), new JsonArray())).Body);
        Assert.Equal(expected, (await client.SendAsync(HttpMethod.Post, "/v1/firms", StaffToken(), content: new StringContent("{\"name\": \"x\"}"))).Body);
        Assert.Equal(expected, (await client.SendAsync(HttpMethod.Patch, "/v1/firms/nope", StaffToken())).Body);
    }

    [Fact]
    public async Task The_body_cannot_demote_the_first_admin()
    {
        // isAdmin: false in the payload is overridden, not honoured.
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Post, "/v1/firms", StaffToken(), new JsonObject
        {
            ["name"] = "Example",
            ["admin"] = new JsonObject { ["email"] = "a@example.test", ["firstName"] = "Ada", ["lastName"] = "Admin", ["isAdmin"] = false },
        });

        Assert.Equal(201, answer.Status);
        Assert.True(answer.Json["admin"]!["isAdmin"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_legacy_display_name_still_provisions()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Post, "/v1/firms", StaffToken(), new JsonObject
        {
            ["name"] = "Example",
            ["admin"] = new JsonObject { ["email"] = "d@example.test", ["displayName"] = "Dana Example", ["role"] = "paralegal" },
        });

        Assert.Equal(201, answer.Status);
        Assert.Equal("Dana", answer.Json["admin"]!["firstName"]!.GetValue<string>());
        Assert.Equal("paralegal", answer.Json["admin"]!["role"]!.GetValue<string>());
    }

    // ── List / get ──────────────────────────────────────────────────

    [Fact]
    public async Task The_firm_list_carries_counts_and_provenance()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        await Provision(client, "Beta LLP", "b@example.test");
        await Provision(client, "Alpha & Co", "a@example.test");

        var firms = (JsonArray)(await client.Get("/v1/firms", StaffToken())).Json["firms"]!;

        Assert.Equal(["Alpha & Co", "Beta LLP"], firms.Select(firm => firm!["name"]!.GetValue<string>()));
        Assert.All(firms, firm => Assert.Equal(1, firm!["userCount"]!.GetValue<int>()));
        Assert.All(firms, firm => Assert.Equal(StaffSub, firm!["createdBy"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_seeded_firm_reads_back_authorless()
    {
        // Pre-portal rows carry no provenance; the list says null, not an author.
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        await host.Firms.CreateFirmAsync(FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(new JsonObject { ["name"] = "Seeded LLP" })), Requests.Ct);

        var answer = await client.Get("/v1/firms", StaffToken());

        Assert.Contains("\"createdBy\":null", answer.Body, StringComparison.Ordinal);
        Assert.Null(answer.Json["firms"]![0]!["createdBy"]);
    }

    [Fact]
    public async Task One_firm_reads_back_with_its_count()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        var firmId = (await Provision(client)).Json["firm"]!["id"]!.GetValue<string>();

        var answer = await client.Get($"/v1/firms/{firmId}", StaffToken());

        Assert.Equal(200, answer.Status);
        Assert.Equal(1, answer.Json["userCount"]!.GetValue<int>());
        Assert.Equal(firmId, answer.Json["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_firm_is_404()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var answer = await client.Get("/v1/firms/nope", StaffToken());

        Assert.Equal(404, answer.Status);
        Assert.Equal("""{"error":"NotFoundError","message":"no such firm"}""", answer.Body);
    }

    // ── Suspend / reactivate ────────────────────────────────────────

    [Fact]
    public async Task Suspend_then_reactivate_audits_each_transition()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        var firmId = (await Provision(client)).Json["firm"]!["id"]!.GetValue<string>();

        var suspended = await client.SendAsync(HttpMethod.Patch, $"/v1/firms/{firmId}", StaffToken(), new JsonObject { ["status"] = "suspended" });
        Assert.Equal(200, suspended.Status);
        Assert.Equal("suspended", suspended.Json["status"]!.GetValue<string>());
        Assert.Null(suspended.Json["userCount"]);

        var reactivated = await client.SendAsync(HttpMethod.Patch, $"/v1/firms/{firmId}", StaffToken(), new JsonObject { ["status"] = "active" });
        Assert.Equal("active", reactivated.Json["status"]!.GetValue<string>());

        Assert.Equal(
            [AdminAction.FirmProvision, AdminAction.FirmSuspend, AdminAction.FirmReactivate],
            host.Audit.Events.Select(audit => audit.Action));
        Assert.All(host.Audit.Events, audit => Assert.Equal(firmId, audit.FirmId));
        Assert.Equal(string.Empty, host.Audit.Events[1].Detail);
    }

    [Fact]
    public async Task An_unknown_status_is_a_field_error()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        var firmId = (await Provision(client)).Json["firm"]!["id"]!.GetValue<string>();

        var answer = await client.SendAsync(HttpMethod.Patch, $"/v1/firms/{firmId}", StaffToken(), new JsonObject { ["status"] = "disabled" });

        Assert.Equal(400, answer.Status);
        Assert.Equal("""{"error":"ValidationError","fields":{"status":"Status must be one of active, suspended."}}""", answer.Body);
    }

    [Fact]
    public async Task Suspending_an_unknown_firm_is_404()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Patch, "/v1/firms/nope", StaffToken(), new JsonObject { ["status"] = "suspended" });

        Assert.Equal(404, answer.Status);
        Assert.Empty(host.Audit.Events);
    }

    // ── A firm's users ──────────────────────────────────────────────

    [Fact]
    public async Task A_firms_users_are_admin_shaped()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        var firmId = (await Provision(client)).Json["firm"]!["id"]!.GetValue<string>();

        var users = (JsonArray)(await client.Get($"/v1/firms/{firmId}/users", StaffToken())).Json["users"]!;

        var user = Assert.Single(users)!;
        Assert.Equal("admin@example.test", user["email"]!.GetValue<string>());
        Assert.NotNull(user["permissions"]);
    }

    [Fact]
    public async Task An_unknown_firms_users_are_404()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        Assert.Equal(404, (await client.Get("/v1/firms/nope/users", StaffToken())).Status);
    }

    // ── Resend invite ───────────────────────────────────────────────

    [Fact]
    public async Task Resend_reinvites_and_audits()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        var body = (await Provision(client)).Json;
        var (firmId, subject) = (body["firm"]!["id"]!.GetValue<string>(), body["admin"]!["subject"]!.GetValue<string>());

        var answer = await client.SendAsync(HttpMethod.Post, $"/v1/firms/{firmId}/users/{subject}/resend-invite", StaffToken());

        Assert.Equal(204, answer.Status);
        Assert.Equal(string.Empty, answer.Body);
        Assert.Equal(["admin@example.test"], host.Directory.Resent);
        Assert.Equal(AdminAction.InviteResend, host.Audit.Events[^1].Action);
        Assert.Equal("admin@example.test", host.Audit.Events[^1].Detail);
    }

    [Fact]
    public async Task Resend_is_firm_scoped()
    {
        // A real subject under the WRONG firm id answers the same 404 an
        // unknown one does — cross-tenant reach never becomes confusion.
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        var body = (await Provision(client)).Json;
        var other = FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(new JsonObject { ["name"] = "Other LLP" }));
        await host.Firms.CreateFirmAsync(other, Requests.Ct);

        var answer = await client.SendAsync(HttpMethod.Post, $"/v1/firms/{other.Id}/users/{body["admin"]!["subject"]!.GetValue<string>()}/resend-invite", StaffToken());

        Assert.Equal(404, answer.Status);
        Assert.Equal("""{"error":"NotFoundError","message":"no such user in that firm"}""", answer.Body);
    }

    [Fact]
    public async Task Resending_to_a_confirmed_user_is_409()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        var body = (await Provision(client)).Json;
        host.Directory.Confirm("admin@example.test");

        var answer = await client.SendAsync(HttpMethod.Post, $"/v1/firms/{body["firm"]!["id"]!.GetValue<string>()}/users/{body["admin"]!["subject"]!.GetValue<string>()}/resend-invite", StaffToken());

        Assert.Equal(409, answer.Status);
        Assert.Equal([AdminAction.FirmProvision], host.Audit.Events.Select(audit => audit.Action));
    }
}
