using System.Text.RegularExpressions;
using Insolvia.Api.Auth;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Firms;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// services/api tests/unit/test_portal_routes.py, translated: the portal's
/// own routes through the real filter, the firm's invitation routes, the
/// questionnaire as a client reads it, and — walked over the WHOLE route
/// table — the disjointness that keeps the two principal classes apart.
/// (The resolution half is also in <see cref="ClientAuthTests"/>, through a
/// probe route.)
/// </summary>
public sealed partial class PortalRouteTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] Sections = ["personal_information", "property", "debts", "income", "expenses", "other"];

    // Routes that authenticate nobody by design: they answer a portal bearer
    // exactly as they answer none.
    private static readonly string[] PublicRoutes = ["POST /v1/waitlist", "POST /v1/unsubscribe"];

    private readonly CaseWorld _world = new();

    private AuthHost Host => _world.World.Host;

    public async ValueTask InitializeAsync()
    {
        await _world.SeedAsync();
        await _world.GrantAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── The ADR's done-when ─────────────────────────────────────────

    [Fact]
    public async Task A_portal_token_reaches_portal_me_with_the_firms_name()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);

        var answer = await _world.PortalGet("/v1/portal/me", pat);

        Assert.Equal(200, answer.Status);
        Assert.Equal(
            $$"""{"case":{"chapter":7,"stage":"intake"},"displayName":"Pat Example","firm":{"name":"Example & Partners"},"roles":["debtor_1"],"subject":"{{pat}}"}""",
            answer.Body);
    }

    [Fact]
    public async Task The_case_block_follows_the_case_and_carries_nothing_else()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);
        var moved = await _world.Patch($"/v1/cases/{caseId}", """{"status": "ready_to_file"}""");
        Assert.True(moved.Status == 200, moved.Body);

        var answer = await _world.PortalGet("/v1/portal/me", pat);

        Assert.Equal("""{"chapter":7,"stage":"ready_to_file"}""", answer.Object["case"]!.ToJsonString());
        Assert.DoesNotContain(caseId, answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_binding_whose_case_is_gone_is_refused()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);
        Assert.Equal(204, (await _world.Delete($"/v1/cases/{caseId}")).Status);

        Assert.Equal(403, (await _world.PortalGet("/v1/portal/me", pat)).Status);
    }

    [Fact]
    public async Task A_portal_token_is_refused_by_the_staff_me_route()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());

        Assert.Equal(401, (await _world.PortalGet("/v1/me", pat)).Status);
    }

    [Fact]
    public async Task A_staff_token_is_refused_by_the_portal() =>
        Assert.Equal(401, (await _world.Get("/v1/portal/me")).Status);

    [Fact]
    public async Task A_revoked_binding_is_refused_and_the_refusal_is_logged()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);
        Assert.Equal(200, (await _world.PortalGet("/v1/portal/me", pat)).Status);
        Assert.Equal(200, (await _world.Delete($"/v1/cases/{caseId}/portal/clients/{pat}")).Status);

        var answer = await _world.PortalGet("/v1/portal/me", pat);

        Assert.Equal(403, answer.Status);
        var denied = _world.Events[^1];
        Assert.Equal((caseId, pat, AccessAction.PortalRead, AccessOutcome.Denied), (denied.SubjectId, denied.Principal, denied.Action, denied.Outcome));
    }

    [Fact]
    public async Task Two_bindings_cannot_both_hold_debtor_2()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await _world.InvitedAsync(caseId, roles: """["debtor_2"]""");

        var answer = await _world.Invite(caseId, "sam@example.test", """["debtor_2"]""");

        Assert.Equal(409, answer.Status);
        var listed = (await _world.Get($"/v1/cases/{caseId}/portal/clients")).Object["clients"]!.AsArray();
        Assert.Equal("""[["debtor_2"]]""", new System.Text.Json.Nodes.JsonArray([.. listed.Select(c => c!["roles"]!.DeepClone())]).ToJsonString());
    }

    // ── Resolution ──────────────────────────────────────────────────

    [Fact]
    public async Task The_first_portal_request_activates_the_invitation()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);

        await _world.PortalGet("/v1/portal/me", pat);

        var listed = Assert.Single((await _world.Get($"/v1/cases/{caseId}/portal/clients")).Object["clients"]!.AsArray());
        Assert.Equal("active", listed!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_portal_request_is_on_the_access_log_as_the_client()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());

        await _world.PortalGet("/v1/portal/me", pat);

        Assert.Equal((AccessAction.PortalRead, AccessOutcome.Allowed, pat), Row(_world.Events[^1]));
    }

    [Fact]
    public async Task A_pool_user_with_no_binding_is_refused_without_a_case_to_log()
    {
        var stranger = await Host.Directory.CreateUserAsync("stranger@example.test", HttpAssert.Ct);
        var before = _world.Events.Count;

        Assert.Equal(403, (await _world.PortalGet("/v1/portal/me", stranger)).Status);
        Assert.Equal(before, _world.Events.Count);
    }

    [Fact]
    public async Task A_suspended_firms_clients_are_suspended_with_it()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());
        var firm = (await _world.Firms.GetFirmAsync(Ids.FirmA, HttpAssert.Ct))!;
        await _world.Firms.UpdateFirmAsync(firm with { Status = FirmStatus.Suspended }, HttpAssert.Ct);

        Assert.Equal(403, (await _world.PortalGet("/v1/portal/me", pat)).Status);
        Assert.Equal((AccessAction.PortalRead, AccessOutcome.Denied, pat), Row(_world.Events[^1]));
    }

    [Fact]
    public async Task A_client_signed_in_to_the_staff_app_resolves_no_firm()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());

        var me = await _world.Get("/v1/me", pat);

        Assert.Equal(200, me.Status);
        Assert.False(me.Object.ContainsKey("firm"));
        Assert.Equal(403, (await _world.Get("/v1/cases", pat)).Status);
    }

    // ── Invitations ─────────────────────────────────────────────────

    [Fact]
    public async Task An_invitation_mints_the_account_and_sends_a_secret_free_context_mail()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var answer = await _world.Invite(caseId, "Pat@Example.TEST");

        Assert.Equal(201, answer.Status);
        var body = answer.Object;
        Assert.Equal(Host.Directory.Subjects["pat@example.test"], body.Str("subject"));
        Assert.Equal(("pat@example.test", "invited", """["debtor_1"]"""), (body.Str("email"), body.Str("status"), body["roles"]!.ToJsonString()));
        Assert.Equal(Answer.Keys(body), ["createdAt", "displayName", "email", "invitedBy", "roles", "status", "subject", "updatedAt"]);
        var (email, key) = Assert.Single(Host.Mailer.Sent);
        Assert.Equal(("client_invitation", "pat@example.test"), (email.Category, email.ToAddress));
        Assert.Contains("Example & Partners", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("http://localhost:3000/portal", email.TextBody, StringComparison.Ordinal);
        Assert.Equal($"client-invitation:{caseId}:{body.Str("subject")}:{body.Str("updatedAt")}", key);
        Assert.Equal((AccessAction.ClientInvite, AccessOutcome.Allowed, Alice), Row(_world.Events[^1]));
    }

    [Fact]
    public async Task The_invitation_names_nothing_about_the_client_in_the_request_log()
    {
        await _world.InvitedAsync(await _world.OpenCaseIdAsync());

        var line = Assert.Single(Host.Logs.In("insolvia_api.api.routes.portal_invitations"));
        Assert.Equal("portal client invited", line.Message);
        Assert.DoesNotContain("example.test", line.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Pat", line.Rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_context_mail_does_not_fail_the_invitation()
    {
        using var world = new CaseWorld(services => services.AddSingleton<Api.Mail.IMailer>(new FailingMailer()));
        await world.SeedAsync();

        var answer = await world.Invite(await world.OpenCaseIdAsync());

        Assert.Equal(201, answer.Status);
        var line = Assert.Single(world.World.Host.Logs.In("insolvia_api.api.routes.portal_invitations"), entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Equal("client invitation mail not sent", line.Message);
    }

    [Fact]
    public async Task One_login_for_both_spouses_is_recorded_as_the_firms_choice()
    {
        await _world.InvitedAsync(await _world.OpenCaseIdAsync(), roles: """["debtor_1", "debtor_2"]""");

        Assert.Equal("debtor_1,debtor_2", _world.Events[^1].Roles);
    }

    [Fact]
    public async Task Inviting_the_second_spouse_narrows_the_first()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId, roles: """["debtor_1", "debtor_2"]""");

        var sam = await _world.InvitedAsync(caseId, "sam@example.test", """["debtor_2"]""");

        var listed = (await _world.Get($"/v1/cases/{caseId}/portal/clients")).Object["clients"]!.AsArray()
            .ToDictionary(c => c!["subject"]!.GetValue<string>(), c => c!["roles"]!.ToJsonString());
        Assert.Equal(new Dictionary<string, string> { [pat] = """["debtor_1"]""", [sam] = """["debtor_2"]""" }, listed);
    }

    [Fact]
    public async Task A_role_conflict_is_refused_before_any_account_is_minted()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await _world.InvitedAsync(caseId, roles: """["debtor_1"]""");

        Assert.Equal(409, (await _world.Invite(caseId, "sam@example.test", """["debtor_1"]""")).Status);
        Assert.False(Host.Directory.Subjects.ContainsKey("sam@example.test"));
    }

    [Fact]
    public async Task An_address_with_an_account_and_no_binding_of_ours_is_a_conflict()
    {
        await Host.Directory.CreateUserAsync("colleague@example.test", HttpAssert.Ct);

        Assert.Equal(409, (await _world.Invite(await _world.OpenCaseIdAsync(), "colleague@example.test")).Status);
    }

    [Fact]
    public async Task A_revoked_client_is_rebound_to_a_refiled_case_as_the_same_person()
    {
        var first = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(first);
        await _world.Delete($"/v1/cases/{first}/portal/clients/{pat}");
        var refiled = await _world.OpenCaseIdAsync();

        var answer = await _world.Invite(refiled);

        Assert.Equal(201, answer.Status);
        Assert.Equal(pat, answer.Object.Str("subject"));
    }

    [Fact]
    public async Task A_client_with_live_access_cannot_be_invited_to_a_second_case()
    {
        await _world.InvitedAsync(await _world.OpenCaseIdAsync());

        Assert.Equal(409, (await _world.Invite(await _world.OpenCaseIdAsync())).Status);
    }

    [Theory]
    [InlineData(Bob, 403)]
    [InlineData(PortalCalls.Granted, 201)]
    [InlineData(Alice, 201)]
    public async Task Inviting_needs_the_client_portal_feature(string caller, int status)
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(status, (await _world.Invite(caseId, caller: caller)).Status);
    }

    [Fact]
    public async Task Another_firms_case_is_a_404_and_the_walk_is_on_the_log()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var answer = await _world.Invite(caseId, caller: Carol);

        Assert.Equal(404, answer.Status);
        Assert.Equal((AccessAction.ClientInvite, AccessOutcome.Denied, Carol), Row(_world.Events[^1]));
        Assert.Empty(Host.Directory.Subjects);
    }

    [Fact]
    public async Task An_invitation_refuses_a_field_it_does_not_take()
    {
        var answer = await _world.Post($"/v1/cases/{await _world.OpenCaseIdAsync()}/portal/invitation", """{"email": "pat@example.test", "displayName": "Pat", "caseId": "other"}""");

        Assert.Equal(400, answer.Status);
        Assert.Equal("unsupported fields: caseId", answer.Object.Str("message"));
    }

    // ── Resend and revoke ───────────────────────────────────────────

    [Fact]
    public async Task An_unused_invitation_is_resent_through_the_directory_and_the_mailer()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);

        var answer = await _world.Post($"/v1/cases/{caseId}/portal/clients/{pat}/resend", null);

        Assert.Equal(204, answer.Status);
        Assert.Equal(["pat@example.test"], Host.Directory.Resent);
        Assert.Equal(2, Host.Mailer.Sent.Count);
    }

    [Fact]
    public async Task A_confirmed_account_gets_only_the_context_mail_on_a_resend()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);
        Host.Directory.Confirm("pat@example.test");

        Assert.Equal(204, (await _world.Post($"/v1/cases/{caseId}/portal/clients/{pat}/resend", null)).Status);
        Assert.Empty(Host.Directory.Resent);
        Assert.Equal(2, Host.Mailer.Sent.Count);
    }

    [Fact]
    public async Task A_used_invitation_is_not_resent()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);
        await _world.PortalGet("/v1/portal/me", pat);

        Assert.Equal(409, (await _world.Post($"/v1/cases/{caseId}/portal/clients/{pat}/resend", null)).Status);
    }

    [Fact]
    public async Task Revoking_is_logged_with_the_roles_and_frees_them()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId, roles: """["debtor_2"]""");

        var answer = await _world.Delete($"/v1/cases/{caseId}/portal/clients/{pat}");

        Assert.Equal("revoked", answer.Object.Str("status"));
        Assert.Equal((AccessAction.ClientRevoke, "debtor_2"), (_world.Events[^1].Action, _world.Events[^1].Roles));
        Assert.Equal(201, (await _world.Invite(caseId, "sam@example.test", """["debtor_2"]""")).Status);
    }

    [Fact]
    public async Task Revoking_twice_is_a_conflict()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);
        await _world.Delete($"/v1/cases/{caseId}/portal/clients/{pat}");

        Assert.Equal(409, (await _world.Delete($"/v1/cases/{caseId}/portal/clients/{pat}")).Status);
    }

    [Fact]
    public async Task A_client_on_another_case_is_a_404_on_this_one()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());
        var other = await _world.OpenCaseIdAsync();

        Assert.Equal(404, (await _world.Delete($"/v1/cases/{other}/portal/clients/{pat}")).Status);
        Assert.Equal(404, (await _world.Post($"/v1/cases/{other}/portal/clients/{pat}/resend", null)).Status);
    }

    [Fact]
    public async Task Listing_the_cases_clients_is_a_case_read()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await _world.InvitedAsync(caseId);

        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}/portal/clients")).Status);
        Assert.Equal((AccessAction.CaseRead, AccessOutcome.Allowed, Alice), Row(_world.Events[^1]));
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/portal/clients", Carol)).Status);
    }

    // ── The questionnaire (ADR 0023 PR 3) ───────────────────────────

    [Fact]
    public async Task A_client_of_a_firm_that_configured_nothing_sees_every_section()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());

        var answer = await _world.PortalGet("/v1/portal/questionnaire", pat);

        Assert.Equal(200, answer.Status);
        var sections = answer.Object["sections"]!.AsArray();
        Assert.Equal(Sections, sections.Select(s => s!["id"]!.GetValue<string>()));
        Assert.Equal(["id", "instructions", "questions", "title"], Answer.Keys(sections[0]));
    }

    [Fact]
    public async Task A_section_switched_off_is_gone_for_the_client_and_kept_for_staff()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());
        var saved = await _world.Put("/v1/firm/questionnaire", Save(("expenses", """ "enabled": false """)));
        Assert.True(saved.Status == 200, saved.Body);

        var seen = await ClientSectionsAsync(pat);
        var staff = (await _world.Get("/v1/firm/questionnaire")).Object;

        Assert.DoesNotContain("expenses", seen);
        Assert.Equal(Sections, staff["sections"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()));
        Assert.Equal([true, true, true, true, false, true], staff["sections"]!.AsArray().Select(s => s!["enabled"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task The_firms_instructions_reach_its_client()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());
        await _world.Put("/v1/firm/questionnaire", Save(("debts", """ "instructions": "Bring your statements." """)));

        var debts = (await _world.PortalGet("/v1/portal/questionnaire", pat)).Object["sections"]![2]!.AsObject();

        Assert.Equal(("debts", "Debts", "Bring your statements."), (debts.Str("id"), debts.Str("title"), debts.Str("instructions")));
    }

    [Fact]
    public async Task Reset_to_defaults_shows_the_client_every_section_again()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());
        await _world.Put("/v1/firm/questionnaire", Save(("other", """ "enabled": false """)));

        Assert.Equal(200, (await _world.Delete("/v1/firm/questionnaire")).Status);
        Assert.Equal(Sections, await ClientSectionsAsync(pat));
    }

    [Fact]
    public async Task Another_firms_config_does_not_reach_this_firms_client()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());

        await _world.Put("/v1/firm/questionnaire", Save(("property", """ "enabled": false """)), Carol);

        Assert.Equal(Sections, await ClientSectionsAsync(pat));
    }

    [Fact]
    public async Task Each_section_carries_its_live_questions_as_the_portal_renders_them()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());

        var first = (await _world.PortalGet("/v1/portal/questionnaire", pat)).Object["sections"]![0]!["questions"]![0]!.AsObject();

        Assert.Equal("personal_information.legal_name", first.Str("id"));
        Assert.Equal(["help", "id", "inputs", "perDebtor", "repeats", "text"], Answer.Keys(first));
        Assert.Equal(["key", "label", "required", "type"], Answer.Keys(first["inputs"]![0]));
    }

    [Fact]
    public async Task Reading_the_questionnaire_is_on_the_access_log_as_the_client()
    {
        var pat = await _world.InvitedAsync(await _world.OpenCaseIdAsync());

        await _world.PortalGet("/v1/portal/questionnaire", pat);

        Assert.Equal((AccessAction.PortalRead, AccessOutcome.Allowed, pat), Row(_world.Events[^1]));
    }

    [Fact]
    public async Task A_revoked_client_cannot_read_the_questionnaire()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);
        await _world.Delete($"/v1/cases/{caseId}/portal/clients/{pat}");

        Assert.Equal(403, (await _world.PortalGet("/v1/portal/questionnaire", pat)).Status);
    }

    // ── Disjointness, over the whole route table ────────────────────

    [Fact]
    public void Every_portal_route_admits_clients_and_no_other_route_does()
    {
        var wrong = Endpoints()
            .Where(e => e.Template.StartsWith("/v1/portal/", StringComparison.Ordinal) != (e.Principal == PrincipalClass.Client))
            .Select(e => $"{e.Method} {e.Template} admits {e.Principal}")
            .ToList();

        Assert.Empty(wrong);
        Assert.Contains(Endpoints(), e => e.Template.StartsWith("/v1/portal/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_route_outside_the_portal_accepts_a_portal_token()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var pat = await _world.InvitedAsync(caseId);
        Assert.Equal(200, (await _world.PortalGet("/v1/portal/me", pat)).Status);

        var admitted = new List<string>();
        foreach (var (method, template, _) in Endpoints())
        {
            if (template.StartsWith("/v1/portal/", StringComparison.Ordinal) || PublicRoutes.Contains($"{method} {template}"))
            {
                continue;
            }

            var answer = await _world.World.SendAsync(new HttpMethod(method), Parameter().Replace(template, caseId), json: "{}", headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + Tokens.Portal(pat) });
            if (answer.Status != 401)
            {
                admitted.Add($"{method} {template} -> {answer.Status}");
            }
        }

        Assert.Empty(admitted);
    }

    [Fact]
    public async Task No_portal_route_accepts_a_staff_token()
    {
        var refused = new List<string>();
        foreach (var (method, template, _) in Endpoints().Where(e => e.Template.StartsWith("/v1/portal/", StringComparison.Ordinal)))
        {
            if ((await _world.World.SendAsync(new HttpMethod(method), Parameter().Replace(template, "x"), Alice, "{}")).Status != 401)
            {
                refused.Add($"{method} {template}");
            }
        }

        Assert.Empty(refused);
    }

    [Fact]
    public void The_portal_reads_the_case_only_through_the_binding_projection()
    {
        // ADR 0023 decision 4 at the source: the portal modules never take a
        // staff Accessor and never use the worker's accessor-less read.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Insolvia.Api", "Routes"));
        foreach (var file in new[] { "PortalRoutes.cs", "PortalDocumentRoutes.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, file));
            Assert.DoesNotContain("CurrentAccessorAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ReachableCaseAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ReadForWorkerAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".GetAsync(caseId, accessor", source, StringComparison.Ordinal);
        }

        Assert.Contains("PublicStatusAsync(client", File.ReadAllText(Path.Combine(root, "PortalRoutes.cs")), StringComparison.Ordinal);
    }

    [GeneratedRegex("\\{[A-Za-z]+\\}")]
    private static partial Regex Parameter();

    private static (AccessAction, AccessOutcome, string) Row(AccessEvent row) => (row.Action, row.Outcome, row.Principal);

    private static string Save(params (string Section, string Extra)[] overrides) =>
        "{\"sections\": [" + string.Join(", ", Sections.Select(id =>
        {
            var extra = overrides.FirstOrDefault(o => o.Section == id).Extra;
            return extra is null ? $$"""{"id": "{{id}}", "enabled": true}""" : $$"""{"id": "{{id}}", "enabled": true, {{extra}}}""";
        })) + "]}";

    private async Task<List<string>> ClientSectionsAsync(string subject)
    {
        var answer = await _world.PortalGet("/v1/portal/questionnaire", subject);
        Assert.True(answer.Status == 200, answer.Body);
        return [.. answer.Object["sections"]!.AsArray().Select(s => s!["id"]!.GetValue<string>())];
    }

    private List<(string Method, string Template, PrincipalClass? Principal)> Endpoints() =>
        [.. Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/v1/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (m, e.RoutePattern.RawText!, e.Metadata.GetMetadata<PrincipalClass>())))];

    private sealed class FailingMailer : Api.Mail.IMailer
    {
        public Task SendAsync(Api.Mail.OutboundEmail email, string idempotencyKey, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("the mailer is down");

        public Task SuppressAsync(string address, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
