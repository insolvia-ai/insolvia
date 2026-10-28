using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Logging;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using static Insolvia.Api.Tests.Support.HttpAssert;

namespace Insolvia.Api.Tests;

// Translated from services/api tests/unit/test_portal_routes.py — the
// resolution half (require_client), driven through the probe portal route
// where the Python drove /v1/portal/me.
public sealed class ClientAuthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static (AccessAction Action, AccessOutcome Outcome, string Principal, string? CaseId) Row(AccessEvent row) =>
        (row.Action, row.Outcome, row.Principal, row.CaseId);

    [Fact]
    public async Task The_first_portal_request_activates_the_invitation()
    {
        using var host = await new AuthHost(factory).SeedAsync();
        using var client = host.CreateClient();

        var answer = await client.Get("/test/portal", Tokens.Portal(Ids.Pat));

        Assert.Equal(200, answer.Status);
        Assert.Equal(Ids.Case, JsonNode.Parse(answer.Body)!["caseId"]!.GetValue<string>());
        Assert.Equal("active", JsonNode.Parse(answer.Body)!["status"]!.GetValue<string>());
        Assert.Equal(BindingStatus.Active, (await host.Bindings.GetAsync(Ids.FirmA, Ids.Pat, Ct))!.Status);
    }

    [Fact]
    public async Task A_revoked_binding_is_refused_and_the_refusal_is_logged()
    {
        using var host = await new AuthHost(factory).SeedAsync();
        using var client = host.CreateClient();

        Assert.Equal(403, (await client.Get("/test/portal", Tokens.Portal(Ids.Quinn))).Status);

        var denied = Assert.Single(host.AccessLog.Events);
        Assert.Equal((AccessAction.PortalRead, AccessOutcome.Denied, Ids.Quinn, (string?)Ids.OtherCase), Row(denied));
        Assert.Equal("binding_revoked", host.Logs.In(StructuredLog.Categories.ClientAuth).Single().Fields["reason"]);
    }

    [Fact]
    public async Task A_refused_write_is_logged_as_the_answer_it_would_have_been()
    {
        using var host = await new AuthHost(factory).SeedAsync();
        using var client = host.CreateClient();

        Assert.Equal(403, (await client.SendAsync(HttpMethod.Post, "/test/portal", Tokens.Portal(Ids.Quinn))).Status);

        Assert.Equal(AccessAction.PortalAnswer, Assert.Single(host.AccessLog.Events).Action);
    }

    [Fact]
    public async Task A_revocation_takes_effect_on_the_very_next_request()
    {
        using var host = await new AuthHost(factory).SeedAsync();
        using var client = host.CreateClient();
        Assert.Equal(200, (await client.Get("/test/portal", Tokens.Portal(Ids.Pat))).Status);

        var live = (await host.Bindings.GetAsync(Ids.FirmA, Ids.Pat, Ct))!;
        await host.Bindings.UpdateAsync(ClientBindings.Revoke(live), Ct);

        Assert.Equal(403, (await client.Get("/test/portal", Tokens.Portal(Ids.Pat))).Status);
        Assert.Equal((AccessAction.PortalRead, AccessOutcome.Denied, Ids.Pat, (string?)Ids.Case), Row(host.AccessLog.Events[^1]));
    }

    [Fact]
    public async Task A_pool_user_with_no_binding_is_refused_without_a_case_to_log()
    {
        using var host = await new AuthHost(factory).SeedAsync();
        using var client = host.CreateClient();

        Assert.Equal(403, (await client.Get("/test/portal", Tokens.Portal(Ids.Stranger))).Status);

        Assert.Empty(host.AccessLog.Events);
        Assert.Equal("no_binding", host.Logs.In(StructuredLog.Categories.ClientAuth).Single().Fields["reason"]);
    }

    [Fact]
    public async Task A_suspended_firms_clients_are_suspended_with_it()
    {
        using var host = await new AuthHost(factory).SeedAsync();
        using var client = host.CreateClient();

        Assert.Equal(403, (await client.Get("/test/portal", Tokens.Portal(Ids.Ray))).Status);

        Assert.Equal((AccessAction.PortalRead, AccessOutcome.Denied, Ids.Ray, (string?)Ids.FirmBCase), Row(Assert.Single(host.AccessLog.Events)));
        Assert.Equal("firm_not_active", host.Logs.In(StructuredLog.Categories.ClientAuth).Single().Fields["reason"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Tokens.StaffClientId)]
    public async Task The_portal_fails_closed_unless_its_client_is_disjoint(string portalClientId)
    {
        using var host = await new AuthHost(factory, new Dictionary<string, string?> { ["AUTH_PORTAL_CLIENT_ID"] = portalClientId }).SeedAsync();
        using var client = host.CreateClient();

        Unauthorized(await client.Get("/test/portal", Tokens.Staff(Ids.Pat)));
        Unauthorized(await client.Get("/test/portal", Tokens.Portal(Ids.Pat)));

        // Staff routes are untouched by a missing portal client.
        Assert.Equal(200, (await client.Get("/test/staff/firm", Tokens.Staff(Ids.Alice))).Status);
    }

    [Fact]
    public async Task A_portal_rejection_logs_a_category_and_nothing_sensitive()
    {
        using var host = await new AuthHost(factory).SeedAsync();
        using var client = host.CreateClient();
        var token = Tokens.Staff(Ids.Pat);

        await client.Get("/test/portal", token);

        var entry = Assert.Single(host.Logs.In(StructuredLog.Categories.ClientAuth));
        Assert.Equal(("portal authentication rejected", (object?)"invalid_client"), (entry.Message, entry.Fields["reason"]));
        Assert.All(host.Logs.Entries, logged => Assert.DoesNotContain(token, logged.Rendered, StringComparison.Ordinal));
    }

    [Fact]
    public void Each_filter_stamps_the_principal_class_it_admits()
    {
        // The static half of test_every_portal_rule_admits_clients_and_no_other_rule_does:
        // the stamp is on the endpoint for a route test to walk.
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        PrincipalClass? Admits(string pattern) => endpoints.First(e => e.RoutePattern.RawText == pattern).Metadata.GetMetadata<PrincipalClass>();

        Assert.Equal(PrincipalClass.Client, Admits("/test/portal"));
        Assert.Equal(PrincipalClass.Staff, Admits("/test/staff"));
        Assert.Null(Admits("/health"));
    }
}
