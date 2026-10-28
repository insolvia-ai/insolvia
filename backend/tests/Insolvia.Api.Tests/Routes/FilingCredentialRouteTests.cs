using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Aws.Sealing;
using Insolvia.Core.AccessLog;
using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Firms;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Tests.Routes;

// test_filing_credential_routes.py, translated: the vault and the written
// authorization under /v1/me — owned by the token's subject, gated by
// electronic_filing, sealed by the API and opened only by the worker's
// opener (composed here by the TEST, never by the API), no credential
// without a signature, and every success no-store.
public sealed class FilingCredentialRouteTests : IDisposable
{
    private const string Attorney = Ids.Alice;
    private const string Colleague = Ids.Dana;
    private const string Admin = Ids.Erin;
    private const string ViewOnly = Ids.Sam;
    private const string Plain = Ids.Pat;
    private const string Seed = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "FAKE-ECF-PASSWORD-not-a-real-one";

    private readonly FirmWorld _world = new();

    private static string Enrolment(string login = "FAKE-ECF-USER") =>
        $$"""{"login": "{{login}}", "password": "{{Password}}", "totp_seed": "{{Seed}}", "courts": ["flmb"]}""";

    private async Task<FirmWorld> SeedAsync()
    {
        await _world.WithFirmsAsync();
        var granted = FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.ElectronicFiling, PermissionLevel.AddEdit);
        var every = Enum.GetValues<Feature>().ToDictionary(feature => feature, _ => PermissionLevel.AddEdit);
        await _world.WithAsync(
            FirmWorld.Member(Attorney, permissions: granted),
            FirmWorld.Member(Colleague, permissions: granted),
            FirmWorld.Member(Admin, isAdmin: true, permissions: every),
            FirmWorld.Member(ViewOnly, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.ElectronicFiling, PermissionLevel.ViewOnly)),
            FirmWorld.Member(Plain));
        return _world;
    }

    private async Task SignAsync(string subject = Attorney)
    {
        var text = (await _world.Get("/v1/me/filing-authorization", subject)).Object["text"]!;
        var answer = await _world.Post(
            "/v1/me/filing-authorization",
            $$"""{"text_version": "{{text["version"]}}", "text_digest": "{{text["digest"]}}"}""",
            subject);
        Assert.True(answer.Status == 201, answer.Body);
    }

    private async Task<string> EnrolAsync(string subject = Attorney, string login = "FAKE-ECF-USER")
    {
        var answer = await _world.Post("/v1/me/filing-credentials", Enrolment(login), subject);
        Assert.True(answer.Status == 201, answer.Body);
        return answer.Str("id")!;
    }

    private Task<Answer> Stale(HttpMethod method, string path, string? json, long? authTime) =>
        _world.SendAsync(method, path, json: json, headers: new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + Tokens.Make(Attorney, extra: authTime is null ? new JsonObject { ["auth_time"] = null } : new JsonObject { ["auth_time"] = authTime }),
        });

    private IFilingCredentialStore Store => _world.Host.Services.GetRequiredService<IFilingCredentialStore>();

    private IFilingAuthorizationStore Authorizations => _world.Host.Services.GetRequiredService<IFilingAuthorizationStore>();

    [Fact]
    public async Task Enrol_answers_the_status_view_and_never_the_secret()
    {
        await SeedAsync();
        await SignAsync();

        var answer = await _world.Post("/v1/me/filing-credentials", Enrolment(), Attorney);

        Assert.Equal(201, answer.Status);
        Assert.Equal(["courts", "created_at", "id", "login", "status", "updated_at"], Answer.Keys(answer.Json));
        Assert.Equal("no-store", answer.Response.Headers.CacheControl!.ToString());
        Assert.DoesNotContain(Password, answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Seed, answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_lists_the_callers_own_and_a_colleague_sees_none_of_it_nor_an_admin()
    {
        await SeedAsync();
        await SignAsync();
        var id = await EnrolAsync();

        var mine = await _world.Get("/v1/me/filing-credentials", Attorney);
        Assert.Equal(id, mine.Object["credentials"]![0]!["id"]!.GetValue<string>());
        Assert.DoesNotContain(Password, mine.Body, StringComparison.Ordinal);
        Assert.Empty((await _world.Get("/v1/me/filing-credentials", Colleague)).Object["credentials"]!.AsArray());
        Assert.Empty((await _world.Get("/v1/me/filing-credentials", Admin)).Object["credentials"]!.AsArray());
        Assert.Equal(404, (await _world.Delete($"/v1/me/filing-credentials/{id}", Colleague)).Status);
        Assert.Equal(404, (await _world.Delete($"/v1/me/filing-credentials/{id}", Admin)).Status);
        Assert.Single(await Store.ListForAttorneyAsync(Ids.FirmA, Attorney, HttpAssert.Ct));
    }

    [Fact]
    public async Task A_bad_enrolment_names_the_field_and_the_same_login_twice_is_a_409()
    {
        await SeedAsync();
        await SignAsync();

        var bad = await _world.Post("/v1/me/filing-credentials", """{"login": "FAKE", "password": "x", "totp_seed": "short"}""", Attorney);
        Assert.Equal(["totp_seed"], bad.FieldErrors);
        await EnrolAsync();
        Assert.Equal(409, (await _world.Post("/v1/me/filing-credentials", Enrolment(), Attorney)).Status);
    }

    [Fact]
    public async Task What_the_api_sealed_only_the_workers_opener_opens_and_revoke_destroys_it()
    {
        await SeedAsync();
        await SignAsync();
        var id = await EnrolAsync();
        var log = new MemoryAccessLog();

        var secret = await FilingCredentialRules.OpenAsync(Ids.FirmA, Attorney, id, "filing-1", "sign_in", new LocalFilingCredentialOpener(), Store, Authorizations, log, cancellationToken: HttpAssert.Ct);
        Assert.Equal((Password, Seed), (secret.Password, secret.TotpSeed));
        Assert.Null(_world.Host.Services.GetService<IFilingCredentialOpener>());

        Assert.Equal(204, (await _world.Delete($"/v1/me/filing-credentials/{id}", Attorney)).Status);
        await Assert.ThrowsAsync<CredentialUnavailableException>(() =>
            FilingCredentialRules.OpenAsync(Ids.FirmA, Attorney, id, "filing-1", "sign_in", new LocalFilingCredentialOpener(), Store, Authorizations, log, cancellationToken: HttpAssert.Ct));
        Assert.Equal(404, (await _world.Delete($"/v1/me/filing-credentials/{id}", Attorney)).Status);
        Assert.Equal(404, (await _world.Delete("/v1/me/filing-credentials/not-a-uuid", Attorney)).Status);
    }

    [Fact]
    public async Task Every_act_is_in_the_access_log()
    {
        await SeedAsync();
        await SignAsync();
        var id = await EnrolAsync();
        await _world.Delete($"/v1/me/filing-credentials/{id}", Attorney);

        var actions = _world.Host.AccessLog.Events.Select(e => e.Action).ToList();
        Assert.Equal([AccessAction.AuthorizationSign, AccessAction.CredentialEnrol, AccessAction.CredentialRevoke], actions);
    }

    [Fact]
    public async Task Without_the_feature_every_route_is_403_and_signed_out_is_401()
    {
        await SeedAsync();

        foreach (var (method, path) in new[] { ("GET", "/v1/me/filing-credentials"), ("POST", "/v1/me/filing-credentials"), ("GET", "/v1/me/filing-authorization"), ("POST", "/v1/me/filing-authorization"), ("DELETE", "/v1/me/filing-authorization") })
        {
            var answer = await _world.SendAsync(new HttpMethod(method), path, Plain, method == "POST" ? "{}" : null);
            Assert.True(answer.Status == 403, $"{method} {path}: {answer.Status}");
            Assert.Equal(401, (await _world.SendAsync(new HttpMethod(method), path)).Status);
        }

        Assert.Equal(PermissionLevel.Hidden, Permissions.DefaultFor(FirmRole.Attorney)[Feature.ElectronicFiling]);
    }

    [Fact]
    public async Task View_only_reads_and_withdraws_but_cannot_enrol_or_sign()
    {
        await SeedAsync();

        Assert.Equal(200, (await _world.Get("/v1/me/filing-credentials", ViewOnly)).Status);
        Assert.Equal(403, (await _world.Post("/v1/me/filing-credentials", Enrolment(), ViewOnly)).Status);
        Assert.Equal(403, (await _world.Post("/v1/me/filing-authorization", "{}", ViewOnly)).Status);
        Assert.Equal(404, (await _world.Delete("/v1/me/filing-authorization", ViewOnly)).Status);
    }

    [Fact]
    public async Task The_text_is_served_with_its_version_and_digest()
    {
        await SeedAsync();

        var answer = await _world.Get("/v1/me/filing-authorization", Attorney);

        var text = answer.Object["text"]!;
        Assert.Equal(FilingAuthorizationRules.TextVersion, text["version"]!.GetValue<string>());
        Assert.Equal(FilingAuthorizationRules.TextDigest(text["text"]!.GetValue<string>()), text["digest"]!.GetValue<string>());
        Assert.False(answer.Object["current"]!.GetValue<bool>());
        Assert.Null(answer.Object["signature"]);
    }

    [Fact]
    public async Task Enrolment_without_a_signature_is_a_403_and_stores_nothing()
    {
        await SeedAsync();

        Assert.Equal(403, (await _world.Post("/v1/me/filing-credentials", Enrolment(), Attorney)).Status);
        Assert.Empty(await Store.ListForAttorneyAsync(Ids.FirmA, Attorney, HttpAssert.Ct));
    }

    public static TheoryData<int?> StaleBy => new() { 400, 3600, (int?)null };

    [Theory]
    [MemberData(nameof(StaleBy))]
    public async Task A_stale_sign_in_cannot_sign(int? signedInAgo)
    {
        await SeedAsync();
        var text = (await _world.Get("/v1/me/filing-authorization", Attorney)).Object["text"]!;
        var body = $$"""{"text_version": "{{text["version"]}}", "text_digest": "{{text["digest"]}}"}""";
        long? authTime = signedInAgo is { } ago ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ago : null;

        var answer = await Stale(HttpMethod.Post, "/v1/me/filing-authorization", body, authTime);

        Assert.Equal(403, answer.Status);
        Assert.Equal("ReauthenticationRequired", answer.Str("error"));
        Assert.Null(await Authorizations.GetCurrentAsync(Ids.FirmA, Attorney, HttpAssert.Ct));
    }

    [Fact]
    public async Task Signing_another_text_or_twice_is_a_409()
    {
        await SeedAsync();

        Assert.Equal(409, (await _world.Post("/v1/me/filing-authorization", $$"""{"text_version": "{{FilingAuthorizationRules.TextVersion}}", "text_digest": "{{new string('0', 64)}}"}""", Attorney)).Status);
        await SignAsync();
        var text = (await _world.Get("/v1/me/filing-authorization", Attorney)).Object["text"]!;
        Assert.Equal(409, (await _world.Post("/v1/me/filing-authorization", $$"""{"text_version": "{{text["version"]}}", "text_digest": "{{text["digest"]}}"}""", Attorney)).Status);
    }

    [Fact]
    public async Task Withdrawing_revokes_every_credential_needs_no_fresh_sign_in_and_spares_a_colleague()
    {
        await SeedAsync();
        await SignAsync();
        await SignAsync(Colleague);
        await EnrolAsync(login: "FAKE-ONE");
        await EnrolAsync(login: "FAKE-TWO");
        var theirs = await EnrolAsync(Colleague, "FAKE-THREE");

        var answer = await Stale(HttpMethod.Delete, "/v1/me/filing-authorization", null, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200);

        Assert.Equal(200, answer.Status);
        Assert.Equal(2, answer.Object["credentials_revoked"]!.GetValue<int>());
        Assert.Empty(await Store.ListForAttorneyAsync(Ids.FirmA, Attorney, HttpAssert.Ct));
        Assert.Equal(theirs, Assert.Single(await Store.ListForAttorneyAsync(Ids.FirmA, Colleague, HttpAssert.Ct)).CredentialId);
        Assert.Equal(404, (await _world.Delete("/v1/me/filing-authorization", Attorney)).Status);
        Assert.Equal(2, _world.Host.AccessLog.Events.Count(e => e.Action == AccessAction.CredentialRevoke && e.Principal == Attorney));
    }

    public void Dispose() => _world.Dispose();
}
