using System.Text.Json.Nodes;
using Insolvia.Api.Http;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_firm_routes.py: a firm's own record and staff list. The two things it
/// holds down — every route is scoped to the CALLER'S firm, and a firm cannot
/// lock itself out. Alice administers firm A, Dana sees every case without
/// administering, Bob is a paralegal, and Carol (<see cref="Ids.Sam"/>)
/// administers firm B.
/// </summary>
public sealed class FirmRouteTests : IAsyncLifetime, IDisposable
{
    private const string Carol = Ids.Sam;

    private readonly FirmWorld _world = new();

    public static TheoryData<string, string> AdministrationRoutes => new()
    {
        { "GET", "/v1/firm" },
        { "PATCH", "/v1/firm" },
        { "GET", "/v1/firm/users" },
        { "POST", "/v1/firm/users" },
        { "PATCH", $"/v1/firm/users/{Ids.Bob}" },
        { "DELETE", $"/v1/firm/users/{Ids.Bob}" },
    };

    public static TheoryData<string, string> TokenRoutes => new()
    {
        { "GET", "/v1/firm" },
        { "PATCH", "/v1/firm" },
        { "GET", "/v1/firm/users" },
        { "GET", "/v1/firm/directory" },
        { "POST", "/v1/firm/users" },
    };

    public static TheoryData<string, string> InvalidAdditions => new()
    {
        { """{"firstName": "A", "lastName": "B", "role": "staff"}""", "email" },
        { """{"email": "not-an-email", "firstName": "A", "lastName": "B", "role": "staff"}""", "email" },
        { """{"email": "a@b.test", "lastName": "B", "role": "staff"}""", "firstName" },
        { """{"email": "a@b.test", "firstName": "A", "role": "staff"}""", "lastName" },
        { """{"email": "a@b.test", "role": "staff"}""", "displayName" },
        { """{"email": "a@b.test", "displayName": "Cher", "role": "staff"}""", "displayName" },
        { """{"email": "a@b.test", "firstName": "A", "lastName": "B"}""", "role" },
        { """{"email": "a@b.test", "firstName": "A", "lastName": "B", "role": "partner"}""", "role" },
        { """{"email": "a@b.test", "firstName": "A", "lastName": "B", "role": "staff", "isAdmin": "true"}""", "isAdmin" },
    };

    public async ValueTask InitializeAsync()
    {
        await (await _world.WithFirmsAsync()).WithAsync(
            FirmWorld.Member(Ids.Alice, isAdmin: true, firstName: "Alice", lastName: "Admin"),
            FirmWorld.Member(Ids.Dana, accessAllCases: true, firstName: "Dana", lastName: "Attorney"),
            FirmWorld.Member(Ids.Bob, role: FirmRole.Paralegal, firstName: "Bob", lastName: "Paralegal"),
            FirmWorld.Member(Carol, Ids.FirmB, isAdmin: true, firstName: "Carol", lastName: "Other"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── Permissions on the surface itself ────────────────────────────

    [Theory]
    [MemberData(nameof(AdministrationRoutes))]
    public async Task Administration_needs_the_admin_flag(string method, string path)
    {
        // Dana sees every matter and is not an admin: separate axes.
        var answer = await _world.SendAsync(new HttpMethod(method), path, Ids.Dana, """{"role": "staff"}""");

        Assert.Equal(403, answer.Status);
        Assert.Equal(ErrorBodies.FeatureRefused, answer.Body);
    }

    [Theory]
    [MemberData(nameof(TokenRoutes))]
    public async Task Administration_requires_a_token(string method, string path)
    {
        var answer = await _world.SendAsync(new HttpMethod(method), path, json: "{}");

        Assert.Equal(401, answer.Status);
        Assert.Equal(ErrorBodies.Unauthorized, answer.Body);
    }

    [Fact]
    public async Task Permission_levels_on_the_administration_surface()
    {
        // view_only reads the staff list and changes nothing; add_edit changes it.
        await _world.ReplaceAsync(FirmWorld.Member(Ids.Dana, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.FirmAdministration, PermissionLevel.ViewOnly)));
        Assert.Equal(200, (await _world.Get("/v1/firm/users", Ids.Dana)).Status);
        Assert.Equal(403, (await _world.Patch($"/v1/firm/users/{Ids.Bob}", """{"role": "staff"}""", Ids.Dana)).Status);

        await _world.ReplaceAsync(FirmWorld.Member(Ids.Dana, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.FirmAdministration, PermissionLevel.AddEdit)));
        Assert.Equal(200, (await _world.Patch($"/v1/firm/users/{Ids.Bob}", """{"role": "staff"}""", Ids.Dana)).Status);
    }

    // ── The firm's own record (#217) ──────────────────────────────────

    [Fact]
    public async Task An_admin_reads_the_firms_record_without_provenance()
    {
        // The exact bytes: no createdBy/createdByEmail, explicit-null defaults.
        var answer = await _world.Get("/v1/firm", Ids.Alice);

        HttpAssert.Json(
            (answer.Status, answer.Body, answer.Response),
            200,
            $$"""{"createdAt":"2026-01-01T00:00:00.000Z","defaultChapter":null,"defaultCourt":null,"defaultDivision":null,"id":"{{Ids.FirmA}}","letterhead":null,"name":"Example & Partners","status":"active","updatedAt":"2026-01-01T00:00:00.000Z"}""");
    }

    [Fact]
    public async Task The_record_is_the_callers_firm()
    {
        Assert.Equal(Ids.FirmB, (await _world.Get("/v1/firm", Carol)).Str("id"));
    }

    [Fact]
    public async Task Firm_defaults_are_written_and_reach_every_member()
    {
        var body = (await _world.Patch(
            "/v1/firm",
            """
            {"defaultCourt": "flmb", "defaultDivision": "tampa", "defaultChapter": 7,
             "letterhead": {"name": "Example & Partners, P.A.", "address": {"line1": "1 Main St", "city": "Tampa", "state": "FL"}, "phone": "813-555-0100"}}
            """,
            Ids.Alice)).Object;

        Assert.Equal("flmb", body["defaultCourt"]!.GetValue<string>());
        Assert.Equal("tampa", body["defaultDivision"]!.GetValue<string>());
        Assert.Equal(7, body["defaultChapter"]!.GetValue<int>());
        Assert.Equal("Tampa", body["letterhead"]!["address"]!["city"]!.GetValue<string>());

        // Bob cannot read /v1/firm, but the create form he uses needs the defaults.
        var me = (await _world.Get("/v1/me", Ids.Bob)).Object["firm"]!;
        Assert.Equal("flmb", me["defaultCourt"]!.GetValue<string>());
        Assert.Equal("tampa", me["defaultDivision"]!.GetValue<string>());
        Assert.Equal(7, me["defaultChapter"]!.GetValue<int>());
        Assert.Equal("Example & Partners, P.A.", me["letterhead"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_default_court_the_registry_does_not_know_is_refused()
    {
        var answer = await _world.Patch("/v1/firm", """{"defaultCourt": "nyeb", "defaultDivision": "brooklyn"}""", Ids.Alice);

        Assert.Equal(400, answer.Status);
        Assert.Contains("defaultCourt", answer.FieldErrors);
    }

    [Fact]
    public async Task A_signature_block_is_self_service_and_visible_to_the_firm()
    {
        const string block = """{"address":{"city":"Tampa","line1":"1 Main St","state":"FL"},"bar_number":"0123456","bar_state":"FL","email":"alice@example.test","firm_name":"Example & Partners","phone":"813-555-0100"}""";
        var me = await _world.Patch("/v1/me", "{\"signatureBlock\": " + block + "}", Ids.Alice);
        Assert.Equal(200, me.Status);
        Assert.Equal(block, FlaskJson.Serialize(me.Object["firm"]!["signatureBlock"]));

        // Every colleague sees it in the directory — a bar number is printed on every filing.
        var people = (await _world.Get("/v1/firm/directory", Ids.Bob)).Object["people"]!.AsArray();
        Assert.Equal(block, FlaskJson.Serialize(people.Single(p => p!["subject"]!.GetValue<string>() == Ids.Alice)!["signatureBlock"]));
        Assert.Null(people.Single(p => p!["subject"]!.GetValue<string>() == Ids.Bob)!["signatureBlock"]);

        // An administrator may set a colleague's too.
        var adminSet = await _world.Patch($"/v1/firm/users/{Ids.Bob}", """{"signatureBlock": {"bar_number": "7654321", "bar_state": "FL"}}""", Ids.Alice);
        Assert.Equal(200, adminSet.Status);
        Assert.Equal("7654321", adminSet.Object["signatureBlock"]!["bar_number"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_rename_lands_on_the_row_and_reaches_every_member()
    {
        var answer = await _world.Patch("/v1/firm", """{"name": "Example, LLP"}""", Ids.Alice);

        Assert.Equal(200, answer.Status);
        Assert.Equal("Example, LLP", answer.Str("name"));
        Assert.Equal("Example, LLP", (await _world.FirmAsync())!.Name);
        Assert.Equal("Example, LLP", (await _world.Get("/v1/me", Ids.Bob)).Object["firm"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_firm_cannot_suspend_itself()
    {
        Assert.Equal(400, (await _world.Patch("/v1/firm", """{"status": "suspended"}""", Ids.Alice)).Status);
        Assert.Equal(FirmStatus.Active, (await _world.FirmAsync())!.Status);
    }

    [Fact]
    public async Task A_rename_cannot_smuggle_a_status()
    {
        Assert.Equal(200, (await _world.Patch("/v1/firm", """{"name": "Example, LLP", "status": "suspended"}""", Ids.Alice)).Status);

        var firm = await _world.FirmAsync();
        Assert.Equal(FirmStatus.Active, firm!.Status);
        Assert.Equal("Example, LLP", firm.Name);
    }

    [Fact]
    public async Task A_bad_firm_name_reports_the_field()
    {
        var answer = await _world.Patch("/v1/firm", """{"name": " "}""", Ids.Alice);

        Assert.Equal(400, answer.Status);
        Assert.Contains("name", answer.FieldErrors);
        Assert.Equal("Example & Partners", (await _world.FirmAsync())!.Name);
    }

    // ── The directory ────────────────────────────────────────────────

    [Fact]
    public async Task Anyone_in_the_firm_can_resolve_a_colleagues_name()
    {
        var people = (await _world.Get("/v1/firm/directory", Ids.Bob)).Object["people"]!.AsArray();

        Assert.Equal(["Alice Admin", "Bob Paralegal", "Dana Attorney"], people.Select(p => p!["displayName"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_directory_withholds_everything_administrative()
    {
        var person = (await _world.Get("/v1/firm/directory", Ids.Bob)).Object["people"]![0];

        Assert.Equal(["displayName", "firstName", "lastName", "role", "signatureBlock", "subject"], Answer.Keys(person));
    }

    [Fact]
    public async Task The_directory_is_firm_scoped()
    {
        var people = (await _world.Get("/v1/firm/directory", Ids.Bob)).Object["people"]!.AsArray();

        Assert.DoesNotContain(Carol, people.Select(p => p!["subject"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_directory_keeps_disabled_colleagues()
    {
        await _world.ReplaceAsync(FirmWorld.Member(Ids.Bob, role: FirmRole.Paralegal, firstName: "Bob", lastName: "Paralegal", status: FirmUserStatus.Disabled));

        var people = (await _world.Get("/v1/firm/directory", Ids.Alice)).Object["people"]!.AsArray();

        Assert.Contains(Ids.Bob, people.Select(p => p!["subject"]!.GetValue<string>()));
    }

    // ── The admin list ───────────────────────────────────────────────

    [Fact]
    public async Task The_admin_list_is_the_whole_record()
    {
        var user = (await _world.Get("/v1/firm/users", Ids.Alice)).Object["users"]![0];

        Assert.Equal(
            ["accessAllCases", "createdAt", "displayName", "email", "firstName", "isAdmin", "lastName", "permissions", "role", "signatureBlock", "status", "subject", "updatedAt"],
            Answer.Keys(user));
    }

    [Fact]
    public async Task The_admin_list_is_firm_scoped()
    {
        var users = (await _world.Get("/v1/firm/users", Ids.Alice)).Object["users"]!.AsArray();

        Assert.Equal([Ids.Alice, Ids.Bob, Ids.Dana], users.Select(u => u!["subject"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    // ── Adding a colleague ───────────────────────────────────────────

    [Fact]
    public async Task Adding_a_colleague_creates_the_account_then_the_row()
    {
        var answer = await _world.Post("/v1/firm/users", """{"email": "New.Person@example.test", "firstName": "New", "lastName": "Person", "role": "paralegal"}""", Ids.Alice);

        Assert.Equal(201, answer.Status);
        var subject = answer.Str("subject")!;

        // The subject the directory minted — and the row is in the CALLER'S firm.
        Assert.Equal(_world.Host.Directory.Subjects["new.person@example.test"], subject);
        Assert.Equal(Ids.FirmA, (await _world.UserAsync(subject))!.FirmId);
    }

    [Fact]
    public async Task A_new_colleague_gets_the_role_defaults()
    {
        var body = (await _world.Post("/v1/firm/users", """{"email": "clerk@example.test", "firstName": "Clerk", "lastName": "Ofcourt", "role": "staff"}""", Ids.Alice)).Object;

        var expected = new JsonObject();
        foreach (var (feature, level) in Permissions.DefaultFor(FirmRole.Staff))
        {
            expected[Core.Wire.ToWire(feature)] = Core.Wire.ToWire(level);
        }

        Assert.True(JsonNode.DeepEquals(expected, body["permissions"]));
        Assert.False(body["isAdmin"]!.GetValue<bool>());
        Assert.False(body["accessAllCases"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Supplied_permissions_are_merged_over_the_defaults()
    {
        var body = (await _world.Post("/v1/firm/users", """{"email": "clerk@example.test", "firstName": "Clerk", "lastName": "Ofcourt", "role": "staff", "permissions": {"documents": "hidden"}}""", Ids.Alice)).Object;

        Assert.Equal("hidden", body["permissions"]!["documents"]!.GetValue<string>());
        Assert.Equal("view_only", body["permissions"]!["cases"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_address_that_already_has_an_account_is_409()
    {
        await _world.Host.Directory.CreateUserAsync("taken@example.test", HttpAssert.Ct);

        var answer = await _world.Post("/v1/firm/users", """{"email": "taken@example.test", "firstName": "Al", "lastName": "Ready", "role": "staff"}""", Ids.Alice);

        Assert.Equal(409, answer.Status);
        Assert.Equal("ConflictError", answer.Str("error"));
    }

    [Fact]
    public async Task A_failed_account_creation_writes_no_row()
    {
        await _world.Host.Directory.CreateUserAsync("taken@example.test", HttpAssert.Ct);
        var before = (await _world.UsersAsync()).Count;

        await _world.Post("/v1/firm/users", """{"email": "taken@example.test", "firstName": "Al", "lastName": "Ready", "role": "staff"}""", Ids.Alice);

        Assert.Equal(before, (await _world.UsersAsync()).Count);
    }

    [Theory]
    [MemberData(nameof(InvalidAdditions))]
    public async Task Adding_a_colleague_validates(string payload, string field)
    {
        var answer = await _world.Post("/v1/firm/users", payload, Ids.Alice);

        Assert.Equal(400, answer.Status);
        Assert.Contains(field, answer.FieldErrors);
    }

    [Fact]
    public async Task Adding_a_colleague_without_a_user_directory_is_a_500_that_names_nothing()
    {
        // The Lambda composes no directory until the Cognito adapter is
        // ported; the read routes must keep working without it.
        using var world = new FirmWorld(services: services => services.RemoveAll<IUserDirectory>());
        await (await world.WithFirmsAsync()).WithAsync(FirmWorld.Member(Ids.Alice, isAdmin: true));

        Assert.Equal(200, (await world.Get("/v1/firm/users", Ids.Alice)).Status);
        var added = await world.Post("/v1/firm/users", """{"email": "a@example.test", "firstName": "A", "lastName": "B", "role": "staff"}""", Ids.Alice);
        Assert.Equal(500, added.Status);
        Assert.Equal("""{"error":"InternalError","message":"request failed"}""", added.Body);
        Assert.Single(await world.UsersAsync());
    }

    // ── Changing a colleague ─────────────────────────────────────────

    [Fact]
    public async Task A_role_change_leaves_the_permission_map_alone()
    {
        await _world.ReplaceAsync(FirmWorld.Member(Ids.Bob, role: FirmRole.Paralegal, permissions: FirmWorld.DefaultsWith(FirmRole.Paralegal, Feature.Documents, PermissionLevel.Hidden)));

        var body = (await _world.Patch($"/v1/firm/users/{Ids.Bob}", """{"role": "attorney"}""", Ids.Alice)).Object;

        Assert.Equal("attorney", body["role"]!.GetValue<string>());
        Assert.Equal("hidden", body["permissions"]!["documents"]!.GetValue<string>());
    }

    [Fact]
    public async Task Another_firms_user_is_404_not_403()
    {
        var real = await _world.Patch($"/v1/firm/users/{Carol}", """{"role": "staff"}""", Ids.Alice);
        var invented = await _world.Patch("/v1/firm/users/00000000-0000-4000-8000-000000000000", """{"role": "staff"}""", Ids.Alice);

        Assert.Equal(404, real.Status);
        Assert.Equal(invented.Status, real.Status);
        Assert.Equal(invented.Body, real.Body);
        Assert.Equal(HttpAssert.ErrorBody("NotFoundError", "firm user not found"), real.Body);
        Assert.Equal(FirmRole.Attorney, (await _world.UserAsync(Carol, Ids.FirmB))!.Role);
    }

    [Fact]
    public async Task Disabling_a_colleague_locks_them_out()
    {
        // test_firm_routes.py read /v1/cases; the directory is the same "any
        // case worker" gate, and the case routes are 18.15's.
        Assert.Equal(200, (await _world.Get("/v1/firm/directory", Ids.Bob)).Status);

        await _world.Patch($"/v1/firm/users/{Ids.Bob}", """{"status": "disabled"}""", Ids.Alice);

        Assert.Equal(403, (await _world.Get("/v1/firm/directory", Ids.Bob)).Status);
    }

    [Fact]
    public async Task Removing_a_colleague_is_a_204_with_flasks_empty_body_and_a_second_is_404()
    {
        var removed = await _world.Delete($"/v1/firm/users/{Ids.Bob}", Ids.Alice);

        Assert.Equal(204, removed.Status);
        Assert.Equal("", removed.Body);
        Assert.Null(await _world.UserAsync(Ids.Bob));
        Assert.Equal(404, (await _world.Delete($"/v1/firm/users/{Ids.Bob}", Ids.Alice)).Status);
    }

    // ── The lock-out rule ────────────────────────────────────────────

    [Fact]
    public async Task The_last_admin_cannot_demote_themselves()
    {
        var answer = await _world.Patch($"/v1/firm/users/{Ids.Alice}", """{"isAdmin": false}""", Ids.Alice);

        Assert.Equal(409, answer.Status);
        Assert.Contains("administrator", answer.Str("message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_last_admin_cannot_disable_themselves()
    {
        Assert.Equal(409, (await _world.Patch($"/v1/firm/users/{Ids.Alice}", """{"status": "disabled"}""", Ids.Alice)).Status);
    }

    [Fact]
    public async Task The_last_admin_cannot_be_removed()
    {
        Assert.Equal(409, (await _world.Delete($"/v1/firm/users/{Ids.Alice}", Ids.Alice)).Status);
        Assert.NotNull(await _world.UserAsync(Ids.Alice));
    }

    [Fact]
    public async Task Demoting_is_allowed_once_somebody_else_is_an_admin()
    {
        Assert.Equal(200, (await _world.Patch($"/v1/firm/users/{Ids.Dana}", """{"isAdmin": true}""", Ids.Alice)).Status);
        Assert.Equal(200, (await _world.Patch($"/v1/firm/users/{Ids.Alice}", """{"isAdmin": false}""", Ids.Alice)).Status);
    }

    [Fact]
    public async Task Promoting_a_disabled_colleague_does_not_count()
    {
        await _world.ReplaceAsync(FirmWorld.Member(Ids.Dana, isAdmin: true, status: FirmUserStatus.Disabled));

        Assert.Equal(409, (await _world.Patch($"/v1/firm/users/{Ids.Alice}", """{"isAdmin": false}""", Ids.Alice)).Status);
    }

    [Fact]
    public async Task An_admin_cannot_demote_the_other_last_admin()
    {
        await _world.ReplaceAsync(FirmWorld.Member(Ids.Dana, isAdmin: true));
        await _world.Patch($"/v1/firm/users/{Ids.Alice}", """{"isAdmin": false}""", Ids.Dana);

        Assert.Equal(409, (await _world.Patch($"/v1/firm/users/{Ids.Dana}", """{"isAdmin": false}""", Ids.Dana)).Status);
    }

    // ── CORS and logging ─────────────────────────────────────────────

    [Fact]
    public async Task Cors_allows_the_methods_the_routes_use()
    {
        var answer = await _world.SendAsync(HttpMethod.Get, "/v1/firm/directory", Ids.Bob, headers: new Dictionary<string, string> { ["Origin"] = "http://localhost:3000" });

        var allowed = string.Join(",", answer.Response.Headers.GetValues("Access-Control-Allow-Methods"));
        foreach (var method in new[] { "GET", "POST", "PATCH", "PUT", "DELETE", "OPTIONS" })
        {
            Assert.Contains(method, allowed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Adding_a_colleague_logs_no_email_subject_or_name()
    {
        var answer = await _world.Post("/v1/firm/users", """{"email": "quiet.person@example.test", "firstName": "Quiet", "lastName": "Person", "role": "staff"}""", Ids.Alice);
        var subject = answer.Str("subject")!;

        Assert.Equal("firm user added", Assert.Single(_world.Host.Logs.In("insolvia_api.api.routes.firm")).Message);
        Assert.DoesNotContain(_world.Host.Logs.Entries, entry =>
            entry.Rendered.Contains("quiet.person", StringComparison.Ordinal)
            || entry.Rendered.Contains(subject, StringComparison.Ordinal)
            || entry.Rendered.Contains("Quiet", StringComparison.Ordinal));
    }
}
