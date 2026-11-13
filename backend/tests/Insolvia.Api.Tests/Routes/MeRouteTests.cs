using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Domain;
using Insolvia.Domain.Firms;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// <c>GET</c>/<c>PATCH /v1/me</c> — test_auth.py's /v1/me half (its token
/// rejections are <see cref="StaffAuthTests"/>' and <see cref="AuthMatrixTests"/>'),
/// against the real route instead of a probe.
/// </summary>
public sealed class MeRouteTests : IDisposable
{
    private readonly FirmWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ── GET: the happy path ──────────────────────────────────────────

    [Fact]
    public async Task A_valid_access_token_returns_the_claims_derived_identity()
    {
        var answer = await _world.Get("/v1/me", Ids.Alice);

        Assert.Equal(200, answer.Status);
        Assert.Equal("application/json", answer.Response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Ids.Alice, answer.Str("subject"));
        Assert.Equal(Ids.Alice, answer.Str("username"));
        Assert.Equal(Tokens.StaffClientId, answer.Str("clientId"));
        Assert.Equal("""["aws.cognito.signin.user.admin"]""", answer.Object["scopes"]!.ToJsonString());
        Assert.True(answer.Object["expiresAt"]!.GetValueKind() == System.Text.Json.JsonValueKind.Number);
    }

    [Fact]
    public async Task The_me_body_has_exactly_the_contract_keys_and_no_firm_for_someone_in_none()
    {
        // `firm` is ABSENT, not null — "signed in, not provisioned" is a state.
        var answer = await _world.Get("/v1/me", Ids.Alice);

        Assert.Equal(["clientId", "expiresAt", "scopes", "subject", "username"], Answer.Keys(answer.Json));
    }

    [Fact]
    public async Task The_body_is_compact_and_reads_the_token_claims()
    {
        var answer = await _world.Get("/v1/me", Ids.Alice);

        var body = answer.Object;
        Assert.Equal(body.ToJsonString(), answer.Body);
        Assert.True(body.Remove("expiresAt"));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse($$"""{"clientId":"exampleappclientid000000","scopes":["aws.cognito.signin.user.admin"],"subject":"{{Ids.Alice}}","username":"{{Ids.Alice}}"}"""),
            body));
    }

    [Fact]
    public async Task Me_reports_the_firm_and_the_effective_permissions()
    {
        await (await _world.WithFirmsAsync()).WithAsync(FirmWorld.Member(Ids.Alice, isAdmin: true, firstName: "Example", lastName: "Admin"));

        var firm = (await _world.Get("/v1/me", Ids.Alice)).Object["firm"]!.AsObject();

        Assert.Equal(Ids.FirmA, firm["id"]!.GetValue<string>());
        Assert.Equal("Example & Partners", firm["name"]!.GetValue<string>());
        Assert.True(firm["isAdmin"]!.GetValue<bool>());

        // EFFECTIVE, not stored: the row says hidden, the admin flag says add_edit.
        Assert.NotEqual(PermissionLevel.AddEdit, Permissions.DefaultFor(FirmRole.Attorney)[Feature.FirmAdministration]);
        Assert.Equal("add_edit", firm["permissions"]!["firm_administration"]!.GetValue<string>());
        Assert.Equal(Wire.Names<Feature>().Order(StringComparer.Ordinal), Answer.Keys(firm["permissions"]));
    }

    [Fact]
    public async Task The_firm_block_is_the_whole_contract_with_explicit_null_defaults()
    {
        await (await _world.WithFirmsAsync()).WithAsync(FirmWorld.Member(Ids.Alice, isAdmin: true, firstName: "Example", lastName: "Admin"));

        var firm = (await _world.Get("/v1/me", Ids.Alice)).Object["firm"]!.AsObject();

        Assert.Equal(
            ["accessAllCases", "defaultChapter", "defaultCourt", "defaultDivision", "displayName", "firstName", "id", "isAdmin", "lastName", "letterhead", "name", "permissions", "role", "signatureBlock"],
            Answer.Keys(firm));
        Assert.Equal("Example Admin", firm["displayName"]!.GetValue<string>());
        Assert.Null(firm["defaultCourt"]);
        Assert.Null(firm["signatureBlock"]);
    }

    [Fact]
    public async Task Me_never_returns_an_email()
    {
        await (await _world.WithFirmsAsync()).WithAsync(FirmWorld.Member(Ids.Alice, isAdmin: true));

        var body = (await _world.Get("/v1/me", Ids.Alice)).Body;

        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Multiple_scopes_split_on_whitespace_and_a_token_without_one_has_none()
    {
        var several = await _world.SendAsync(HttpMethod.Get, "/v1/me", headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + Tokens.Make(extra: new JsonObject { ["scope"] = "openid profile email" }) });
        Assert.Equal("""["openid","profile","email"]""", several.Object["scopes"]!.ToJsonString());

        var none = await _world.SendAsync(HttpMethod.Get, "/v1/me", headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + Tokens.Make(extra: new JsonObject { ["scope"] = null }) });
        Assert.Equal("[]", none.Object["scopes"]!.ToJsonString());
    }

    [Fact]
    public async Task No_token_is_the_one_401_body()
    {
        var answer = await _world.Get("/v1/me");

        Assert.Equal(401, answer.Status);
        Assert.Equal(ErrorBodies.Unauthorized, answer.Body);
    }

    // ── PATCH: the self-service rename (#216 / 11.16) ─────────────────

    [Fact]
    public async Task A_member_renames_themselves_and_the_row_agrees()
    {
        await ProvisionedAsync();

        var answer = await _world.Patch("/v1/me", """{"firstName": "Corrected", "lastName": "Name"}""", Ids.Alice);

        Assert.Equal(200, answer.Status);
        var firm = answer.Object["firm"]!;
        Assert.Equal("Corrected", firm["firstName"]!.GetValue<string>());
        Assert.Equal("Name", firm["lastName"]!.GetValue<string>());
        Assert.Equal("Corrected Name", firm["displayName"]!.GetValue<string>());
        var row = await _world.UserAsync(Ids.Alice);
        Assert.Equal(("Corrected", "Name"), (row!.FirstName, row.LastName));
    }

    [Fact]
    public async Task Either_half_of_a_name_may_be_corrected_alone()
    {
        await ProvisionedAsync();

        Assert.Equal(200, (await _world.Patch("/v1/me", """{"lastName": "Renamed"}""", Ids.Alice)).Status);

        var row = await _world.UserAsync(Ids.Alice);
        Assert.Equal(("Original", "Renamed"), (row!.FirstName, row.LastName));
    }

    [Fact]
    public async Task A_display_name_is_not_a_field_and_changes_nothing()
    {
        await ProvisionedAsync();

        var answer = await _world.Patch("/v1/me", """{"displayName": "Corrected Name"}""", Ids.Alice);

        Assert.Equal(HttpAssert.ErrorBody("ValidationError", "no supported fields to update"), answer.Body);
        var row = await _world.UserAsync(Ids.Alice);
        Assert.Equal(("Original", "Name"), (row!.FirstName, row.LastName));
    }

    [Fact]
    public async Task The_patch_answers_with_the_get_body()
    {
        await ProvisionedAsync();

        var patched = await _world.Patch("/v1/me", """{"firstName": "Corrected", "lastName": "Name"}""", Ids.Alice);
        var fetched = await _world.Get("/v1/me", Ids.Alice);

        // A fresh token per call: compare all but its expiry.
        Assert.Equal(200, patched.Status);
        Assert.True(JsonNode.DeepEquals(Without(patched, "expiresAt"), Without(fetched, "expiresAt")));
    }

    [Fact]
    public async Task No_permission_is_needed_to_rename_yourself()
    {
        await ProvisionedAsync(permissions: Wire.Members<Feature>().ToDictionary(feature => feature, _ => PermissionLevel.Hidden));

        Assert.Equal(200, (await _world.Patch("/v1/me", """{"firstName": "Still", "lastName": "Mine"}""", Ids.Alice)).Status);

        var row = await _world.UserAsync(Ids.Alice);
        Assert.Equal(("Still", "Mine"), (row!.FirstName, row.LastName));
    }

    [Fact]
    public async Task A_rename_cannot_smuggle_a_promotion()
    {
        await ProvisionedAsync();

        var answer = await _world.Patch("/v1/me", """{"firstName": "Corrected", "lastName": "Name", "isAdmin": true, "role": "attorney"}""", Ids.Alice);

        Assert.Equal(200, answer.Status);
        var row = await _world.UserAsync(Ids.Alice);
        Assert.False(row!.IsAdmin);
        Assert.Equal(FirmRole.Staff, row.Role);
    }

    [Fact]
    public async Task A_privilege_only_payload_is_a_validation_error()
    {
        await ProvisionedAsync();

        Assert.Equal(400, (await _world.Patch("/v1/me", """{"isAdmin": true}""", Ids.Alice)).Status);
        Assert.False((await _world.UserAsync(Ids.Alice))!.IsAdmin);
    }

    [Fact]
    public async Task A_bad_name_reports_the_field()
    {
        await ProvisionedAsync();

        var answer = await _world.Patch("/v1/me", """{"firstName": " ", "lastName": "Name"}""", Ids.Alice);

        Assert.Equal(400, answer.Status);
        Assert.Contains("firstName", answer.FieldErrors);
    }

    [Fact]
    public async Task A_caller_in_no_firm_cannot_rename()
    {
        var answer = await _world.Patch("/v1/me", """{"firstName": "No", "lastName": "Body"}""", Ids.Alice);

        Assert.Equal(403, answer.Status);
        Assert.Equal(HttpAssert.ErrorBody("ForbiddenError", "your account is not active in a firm"), answer.Body);
    }

    [Fact]
    public async Task A_disabled_member_cannot_rename()
    {
        await ProvisionedAsync(status: FirmUserStatus.Disabled);

        Assert.Equal(403, (await _world.Patch("/v1/me", """{"firstName": "Not", "lastName": "Anymore"}""", Ids.Alice)).Status);

        var row = await _world.UserAsync(Ids.Alice);
        Assert.Equal(("Original", "Name"), (row!.FirstName, row.LastName));
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_400_never_a_415()
    {
        await ProvisionedAsync();

        var answer = await _world.SendAsync(HttpMethod.Patch, "/v1/me", Ids.Alice, "name=Ada", "text/plain");

        Assert.Equal(HttpAssert.ErrorBody("ValidationError", "request body must be a JSON object"), answer.Body);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("{not json")]
    public async Task A_json_body_that_is_not_an_object_is_a_400_never_a_422(string body)
    {
        await ProvisionedAsync();

        var answer = await _world.SendAsync(HttpMethod.Patch, "/v1/me", Ids.Alice, body, "application/json");

        Assert.Equal(400, answer.Status);
        Assert.Equal("ValidationError", answer.Str("error"));
    }

    [Fact]
    public async Task The_route_declares_a_four_kib_body_cap() =>
        Assert.Equal(4 * 1024, BodyCap(_world.Host.Services, "/v1/me", "PATCH"));

    /// <summary>The byte cap a route declares; the server that hosts it enforces it.</summary>
    internal static long? BodyCap(IServiceProvider services, string pattern, string method) =>
        services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == pattern && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()!.HttpMethods.Contains(method))
            .Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()!.MaxRequestBodySize;

    [Fact]
    public async Task A_rename_logs_that_it_happened_and_nothing_about_whom()
    {
        await ProvisionedAsync();

        await _world.Patch("/v1/me", """{"firstName": "Corrected", "lastName": "Secretname"}""", Ids.Alice);

        var entry = Assert.Single(_world.Host.Logs.In("insolvia_api.api.routes.me"));
        Assert.Equal("self name updated", entry.Message);
        Assert.DoesNotContain(_world.Host.Logs.Entries, line => line.Rendered.Contains("Secretname", StringComparison.Ordinal) || line.Rendered.Contains(Ids.Alice, StringComparison.Ordinal));
    }

    private static JsonObject Without(Answer answer, string key)
    {
        var copy = answer.Object.DeepClone().AsObject();
        copy.Remove(key);
        return copy;
    }

    // test_auth.py's `provisioned`: one firm, the caller a staff member of it.
    private async Task ProvisionedAsync(IReadOnlyDictionary<Feature, PermissionLevel>? permissions = null, FirmUserStatus status = FirmUserStatus.Active)
    {
        await (await _world.WithFirmsAsync()).WithAsync(
            FirmWorld.Member(Ids.Alice, role: FirmRole.Staff, status: status, firstName: "Original", lastName: "Name", permissions: permissions));
    }
}

/// <summary>The fixed error bodies the route suites compare against.</summary>
internal static class ErrorBodies
{
    public const string Unauthorized = """{"error":"Unauthorized","message":"authentication required"}""";
    public const string FeatureRefused = """{"error":"ForbiddenError","message":"your firm has not granted you access to this"}""";
}
