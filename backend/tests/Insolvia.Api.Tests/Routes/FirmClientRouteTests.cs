using Insolvia.Api.Tests.Support;
using Insolvia.Core;
using Insolvia.Core.Firms;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_firm_clients_routes.py (and the funnel half of
/// test_case_lifecycle_routes.py): <c>/v1/firm/clients</c>, gated by
/// <c>clients</c>. The routes work; the gate is ADR 0009's; every route is
/// scoped to the caller's firm; <c>client.*</c> rows are logged. Where the
/// Python opened a case to give a client one, these link the client through
/// the debtor-store fake, since the case routes are 18.15's.
/// </summary>
public sealed class FirmClientRouteTests : IAsyncLifetime, IDisposable
{
    private const string Admin = Ids.Alice;
    private const string Viewer = Ids.Dana;
    private const string Blocked = Ids.Bob;
    private const string Legacy = "00000000-0000-4000-8000-00000000013c";
    private const string Paralegal = "00000000-0000-4000-8000-0000000000a1";
    private const string OtherAdmin = Ids.Sam;

    private readonly FirmWorld _world = new();

    public static TheoryData<string> NoFeature => [Blocked, Legacy];

    public async ValueTask InitializeAsync()
    {
        // A row from before `clients` existed: every attorney default but that one.
        var legacy = Permissions.DefaultFor(FirmRole.Attorney).Where(pair => pair.Key != Feature.Clients).ToDictionary(pair => pair.Key, pair => pair.Value);
        await (await _world.WithFirmsAsync()).WithAsync(
            FirmWorld.Member(Admin, isAdmin: true, permissions: new Dictionary<Feature, PermissionLevel>()),
            FirmWorld.Member(Viewer, permissions: new Dictionary<Feature, PermissionLevel> { [Feature.Clients] = PermissionLevel.ViewOnly }),
            FirmWorld.Member(Blocked, permissions: new Dictionary<Feature, PermissionLevel> { [Feature.Clients] = PermissionLevel.Hidden }),
            FirmWorld.Member(Legacy, permissions: legacy),
            FirmWorld.Member(Paralegal, role: FirmRole.Paralegal),
            FirmWorld.Member(OtherAdmin, Ids.FirmB, isAdmin: true, permissions: new Dictionary<Feature, PermissionLevel>()));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── Gating (ADR 0009) ────────────────────────────────────────────

    [Fact]
    public async Task View_only_can_list_and_read_but_not_write()
    {
        var id = await AddAsync();

        Assert.Equal(200, (await _world.Get("/v1/firm/clients", Viewer)).Status);
        Assert.Equal(200, (await _world.Get($"/v1/firm/clients/{id}", Viewer)).Status);
        Assert.Equal(403, (await Add(Viewer)).Status);
        Assert.Equal(403, (await _world.Put($"/v1/firm/clients/{id}", """{"name": {"surname": "X"}}""", Viewer)).Status);
        Assert.Equal(403, (await _world.Put($"/v1/firm/clients/{id}/status", """{"status": "archived"}""", Viewer)).Status);
    }

    [Theory]
    [MemberData(nameof(NoFeature))]
    public async Task Without_the_feature_every_route_is_the_adr_0009_403(string subject)
    {
        var id = await AddAsync();
        var url = $"/v1/firm/clients/{id}";

        Answer[] answers =
        [
            await _world.Get("/v1/firm/clients", subject),
            await Add(subject),
            await _world.Get(url, subject),
            await _world.Put(url, """{"name": {"surname": "X"}}""", subject),
            await _world.Put($"{url}/status", """{"status": "archived"}""", subject),
            await _world.Put($"{url}/prospect-stage", """{"prospect_stage": "possible"}""", subject),
        ];

        Assert.All(answers, answer =>
        {
            Assert.Equal(403, answer.Status);
            Assert.Equal(ErrorBodies.FeatureRefused, answer.Body);
        });
    }

    [Fact]
    public async Task A_new_paralegal_row_can_keep_the_directory()
    {
        Assert.Equal(201, (await Add(Paralegal)).Status);
    }

    [Fact]
    public async Task No_auth_is_401()
    {
        Assert.Equal(ErrorBodies.Unauthorized, (await _world.Get("/v1/firm/clients")).Body);
    }

    [Fact]
    public async Task There_is_no_delete()
    {
        var id = await AddAsync();

        Assert.Equal(405, (await _world.Delete($"/v1/firm/clients/{id}", Admin)).Status);
    }

    // ── The five routes ──────────────────────────────────────────────

    [Fact]
    public async Task A_created_client_is_returned_and_then_listed()
    {
        var created = await Add(extra: """, "residence_address": {"line1": "1 Example St", "county": "Sangamon"}, "date_of_birth": "1980-02-29", "lead_source": "Referral" """);

        Assert.Equal(201, created.Status);
        var body = created.Object;
        Assert.Equal("""{"given":"Jordan","surname":"Example"}""", body["name"]!.ToJsonString());
        Assert.Equal("""{"county":"Sangamon","line1":"1 Example St"}""", Http.FlaskJson.Serialize(body["residence_address"]));
        Assert.Equal("active", body["status"]!.GetValue<string>());
        Assert.Equal(Admin, body["created_by"]!.GetValue<string>());
        Assert.Equal("Referral", body["lead_source"]!.GetValue<string>());
        Assert.Equal("1980-02-29", body["date_of_birth"]!.GetValue<string>());

        var listed = (await _world.Get("/v1/firm/clients", Admin)).Object["clients"]!.AsArray();
        Assert.Equal([body["id"]!.GetValue<string>()], listed.Select(client => client!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_body_omits_what_is_absent_in_flasks_key_order()
    {
        var created = await Add();

        Assert.Equal(["created_at", "created_by", "id", "name", "status", "updated_at"], Answer.Keys(created.Json));
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{6}Z$", created.Str("created_at")!);
        Assert.StartsWith("""{"created_at":""", created.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_name_is_a_400_naming_the_field()
    {
        var answer = await _world.Post("/v1/firm/clients", """{"name": {"middle": "Q"}}""", Admin);

        Assert.Equal(400, answer.Status);
        Assert.Contains("name", answer.FieldErrors);
    }

    [Fact]
    public async Task A_tax_id_in_the_body_is_a_400_not_a_silent_drop()
    {
        var answer = await Add(extra: """, "tax_id": {"kind": "ssn", "value": "987-65-4320"}""");

        Assert.Equal(400, answer.Status);
        Assert.Contains("tax_id", answer.FieldErrors);
    }

    [Fact]
    public async Task Get_one_by_id()
    {
        var created = await Add();
        var fetched = await _world.Get($"/v1/firm/clients/{created.Str("id")}", Admin);

        Assert.Equal(200, fetched.Status);
        Assert.Equal(created.Body, fetched.Body);
    }

    [Fact]
    public async Task A_replace_is_whole_record_and_keeps_the_server_owned_fields()
    {
        var created = await Add(extra: """, "lead_source": "Referral" """);
        var id = created.Str("id");

        var replaced = await _world.Put($"/v1/firm/clients/{id}", $$"""{"name": {"surname": "Renamed"}, "status": "archived", "created_by": "{{OtherAdmin}}"}""", Admin);

        Assert.Equal(200, replaced.Status);
        var body = replaced.Object;
        Assert.Equal(id, body["id"]!.GetValue<string>());
        Assert.Equal("""{"surname":"Renamed"}""", body["name"]!.ToJsonString());
        Assert.False(body.ContainsKey("lead_source")); // PUT, whole-record: omitted is cleared
        Assert.Equal("active", body["status"]!.GetValue<string>());
        Assert.Equal(Admin, body["created_by"]!.GetValue<string>());
        Assert.Equal(created.Str("created_at"), body["created_at"]!.GetValue<string>());
    }

    [Fact]
    public async Task Archive_is_a_status_write_and_is_reversible()
    {
        var created = await Add();
        var url = $"/v1/firm/clients/{created.Str("id")}/status";

        var archived = await _world.Put(url, """{"status": "archived"}""", Admin);
        Assert.Equal(200, archived.Status);
        Assert.Equal("archived", archived.Str("status"));
        Assert.Equal(created.Object["name"]!.ToJsonString(), archived.Object["name"]!.ToJsonString());

        // Still in the directory: archived is a state, not an absence.
        var listed = (await _world.Get("/v1/firm/clients", Admin)).Object["clients"]!.AsArray();
        Assert.Equal(["archived"], listed.Select(client => client!["status"]!.GetValue<string>()));

        Assert.Equal("active", (await _world.Put(url, """{"status": "active"}""", Admin)).Str("status"));
    }

    [Fact]
    public async Task An_unknown_status_is_a_400()
    {
        var id = await AddAsync();

        var answer = await _world.Put($"/v1/firm/clients/{id}/status", """{"status": "deleted"}""", Admin);

        Assert.Equal(400, answer.Status);
        Assert.Contains("status", answer.FieldErrors);
    }

    // ── Firm scoping (the anti-oracle rule) ──────────────────────────

    [Fact]
    public async Task Another_firms_client_is_a_404_not_a_403()
    {
        var created = await Add();
        var url = $"/v1/firm/clients/{created.Str("id")}";

        Assert.Equal(404, (await _world.Get(url, OtherAdmin)).Status);
        Assert.Equal(404, (await _world.Put(url, """{"name": {"surname": "Hijacked"}}""", OtherAdmin)).Status);
        Assert.Equal(404, (await _world.Put($"{url}/status", """{"status": "archived"}""", OtherAdmin)).Status);
        Assert.Equal("""{"clients":[]}""", (await _world.Get("/v1/firm/clients", OtherAdmin)).Body);

        Assert.Equal(created.Body, (await _world.Get(url, Admin)).Body);
    }

    // ── The access log ───────────────────────────────────────────────

    [Fact]
    public async Task Create_read_and_update_are_logged_under_the_client()
    {
        var id = await AddAsync();
        await _world.Get($"/v1/firm/clients/{id}", Viewer);
        await _world.Put($"/v1/firm/clients/{id}", """{"name": {"surname": "R"}}""", Admin);
        await _world.Put($"/v1/firm/clients/{id}/status", """{"status": "archived"}""", Admin);

        Assert.Equal(
            [
                ("client.create", id, Admin, "allowed"),
                ("client.read", id, Viewer, "allowed"),
                ("client.update", id, Admin, "allowed"),
                ("client.update", id, Admin, "allowed"),
            ],
            Rows());
        Assert.All(_world.Host.AccessLog.Events, e => Assert.Equal($"CLIENT#{id}", e.SubjectKey));
        Assert.All(_world.Host.AccessLog.Events, e => Assert.Null(e.CaseId));
    }

    [Fact]
    public async Task The_list_is_not_logged()
    {
        await AddAsync();
        var before = _world.Host.AccessLog.Events.Count;

        await _world.Get("/v1/firm/clients", Admin);

        Assert.Equal(before, _world.Host.AccessLog.Events.Count);
    }

    [Fact]
    public async Task A_refused_read_is_logged_as_denied()
    {
        var id = await AddAsync();

        await _world.Get($"/v1/firm/clients/{id}", OtherAdmin);

        Assert.Equal(("client.read", id, OtherAdmin, "denied"), Rows()[^1]);
    }

    [Fact]
    public async Task A_forbidden_caller_writes_no_row()
    {
        var id = await AddAsync();
        var before = _world.Host.AccessLog.Events.Count;

        await _world.Get($"/v1/firm/clients/{id}", Blocked);

        Assert.Equal(before, _world.Host.AccessLog.Events.Count);
    }

    // ── The prospect funnel (14.3 / #355) ────────────────────────────

    [Fact]
    public async Task A_prospect_moves_through_the_funnel()
    {
        var id = await AddAsync();

        foreach (var value in new[] { "possible", "consultation_scheduled", "awaiting_signed_agreement" })
        {
            var answer = await Stage(id, $"\"{value}\"");
            Assert.Equal(200, answer.Status);
            Assert.Equal(value, answer.Str("prospect_stage"));
        }
    }

    [Fact]
    public async Task A_retained_client_takes_no_stage()
    {
        // The Python opened a case; the retained transition is what that did.
        var id = await AddAsync();
        await _world.Firms.MarkClientRetainedAsync(Ids.FirmA, id, new DateOnly(2026, 2, 1), HttpAssert.Ct);

        var answer = await Stage(id, "\"possible\"");

        Assert.Equal(409, answer.Status);
        Assert.Equal(HttpAssert.ErrorBody("ConflictError", "This client has been retained \\u2014 the prospect funnel is behind them."), answer.Body);
    }

    [Fact]
    public async Task A_client_saved_with_a_retained_date_takes_no_stage_and_shows_none()
    {
        var id = (await Add(extra: """, "first_retained_at": "2020-01-02" """)).Str("id")!;

        Assert.Equal(409, (await Stage(id, "\"possible\"")).Status);
        Assert.Equal("2020-01-02", (await _world.Get($"/v1/firm/clients/{id}", Admin)).Str("first_retained_at"));
    }

    [Fact]
    public async Task A_stage_can_be_cleared_and_an_unknown_one_is_refused()
    {
        var id = await AddAsync();
        await Stage(id, "\"exhausted\"");

        var cleared = await Stage(id, "null");
        Assert.Equal(200, cleared.Status);
        Assert.False(cleared.Object.ContainsKey("prospect_stage"));

        var unknown = await Stage(id, "\"won\"");
        Assert.Equal(400, unknown.Status);
        Assert.Contains("prospect_stage", unknown.FieldErrors);
    }

    [Fact]
    public async Task A_stage_body_must_name_the_key()
    {
        var id = await AddAsync();

        var answer = await _world.Put($"/v1/firm/clients/{id}/prospect-stage", "{}", Admin);

        Assert.Equal(400, answer.Status);
        Assert.Contains("prospect_stage", answer.FieldErrors);
    }

    [Fact]
    public async Task Another_firms_client_takes_no_stage_and_the_attempt_is_logged()
    {
        var id = await AddAsync();

        Assert.Equal(404, (await Stage(id, "\"possible\"", OtherAdmin)).Status);
        Assert.Equal(("client.update", id, OtherAdmin, "denied"), Rows()[^1]);
    }

    [Fact]
    public async Task The_whole_record_put_keeps_the_stage()
    {
        var id = await AddAsync();
        await Stage(id, "\"consultation_scheduled\"");

        var edited = await _world.Put($"/v1/firm/clients/{id}", """{"name": {"surname": "Example"}, "lead_source": "Referral"}""", Admin);

        Assert.Equal("consultation_scheduled", edited.Str("prospect_stage"));
    }

    // ── Merge (ADR 0022 PR 7) ────────────────────────────────────────

    [Fact]
    public async Task Merging_moves_the_cases_and_archives_the_merged_client()
    {
        var keep = await AddAsync();
        var dupe = await AddAsync("""{"given": "Jordy", "surname": "Example"}""");
        var theirs = _world.Host.DebtorLinks.CaseFor(dupe);

        var answer = await Merge(keep, dupe);

        Assert.Equal(200, answer.Status);
        Assert.Equal(["client", "merged"], Answer.Keys(answer.Json));
        Assert.Equal(keep, answer.Object["client"]!["id"]!.GetValue<string>());
        Assert.Equal("active", answer.Object["client"]!["status"]!.GetValue<string>());
        Assert.Equal(dupe, answer.Object["merged"]!["id"]!.GetValue<string>());
        Assert.Equal("archived", answer.Object["merged"]!["status"]!.GetValue<string>());
        Assert.Equal(keep, answer.Object["merged"]!["merged_into"]!.GetValue<string>());
        Assert.Equal([theirs], _world.Host.DebtorLinks.CasesOf(keep));
        Assert.Empty(_world.Host.DebtorLinks.CasesOf(dupe));
        Assert.Equal(keep, (await _world.Get($"/v1/firm/clients/{dupe}", Admin)).Str("merged_into"));
    }

    [Fact]
    public async Task The_merge_moves_cases_the_caller_cannot_see_and_says_nothing_of_them()
    {
        var keep = await AddAsync();
        var dupe = await AddAsync("""{"given": "Jordy"}""");
        var hidden = _world.Host.DebtorLinks.CaseFor(dupe);

        var answer = await Merge(keep, dupe, Paralegal);

        Assert.Equal(200, answer.Status);
        Assert.DoesNotContain("cases", answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(hidden, answer.Body, StringComparison.Ordinal);
        Assert.Equal([hidden], _world.Host.DebtorLinks.CasesOf(keep));
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("third")]
    public async Task A_merged_client_cannot_be_merged_again(string mergedAgainInto)
    {
        var ids = new Dictionary<string, string>();
        foreach (var name in new[] { "keep", "dupe", "third" })
        {
            ids[name] = await AddAsync($$"""{"given": "{{name}}"}""");
        }

        await Merge(ids["keep"], ids["dupe"]);
        var answer = await Merge(ids[mergedAgainInto], ids["dupe"]);

        Assert.Equal(409, answer.Status);
        Assert.Equal("ConflictError", answer.Str("error"));
    }

    [Theory]
    [InlineData("", """{"name": {"surname": "Revived"}}""")]
    [InlineData("/status", """{"status": "active"}""")]
    [InlineData("/prospect-stage", """{"prospect_stage": "possible"}""")]
    public async Task A_merged_client_cannot_be_edited_or_restored(string suffix, string body)
    {
        var keep = await AddAsync();
        var dupe = await AddAsync("""{"given": "Jordy"}""");
        await Merge(keep, dupe);

        var answer = await _world.Put($"/v1/firm/clients/{dupe}{suffix}", body, Admin);

        Assert.Equal(409, answer.Status);
        Assert.Equal("archived", (await _world.Get($"/v1/firm/clients/{dupe}", Admin)).Str("status"));
    }

    [Fact]
    public async Task Two_clients_on_one_joint_case_are_a_409_and_nothing_moves()
    {
        var keep = await AddAsync();
        var dupe = await AddAsync("""{"given": "Jordy"}""");
        var joint = _world.Host.DebtorLinks.CaseFor(keep, dupe);

        var answer = await Merge(keep, dupe);

        Assert.Equal(409, answer.Status);
        Assert.Contains("same case", answer.Str("message"), StringComparison.Ordinal);
        Assert.Equal([joint], _world.Host.DebtorLinks.CasesOf(dupe));
        Assert.Equal("active", (await _world.Get($"/v1/firm/clients/{dupe}", Admin)).Str("status"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"merged_client_id": "no-such-client"}""")]
    public async Task The_merged_client_must_be_named_and_exist(string body)
    {
        var keep = await AddAsync();

        var answer = await _world.Post($"/v1/firm/clients/{keep}/merge", body, Admin);

        Assert.Equal(400, answer.Status);
        Assert.Contains("merged_client_id", answer.FieldErrors);
    }

    [Fact]
    public async Task A_client_cannot_be_merged_into_itself()
    {
        var keep = await AddAsync();

        var answer = await Merge(keep, keep);

        Assert.Equal(400, answer.Status);
        Assert.Contains("merged_client_id", answer.FieldErrors);
    }

    [Fact]
    public async Task An_archived_client_is_a_409()
    {
        var keep = await AddAsync();
        var dupe = await AddAsync("""{"given": "Jordy"}""");
        await _world.Put($"/v1/firm/clients/{dupe}/status", """{"status": "archived"}""", Admin);

        Assert.Equal(409, (await Merge(keep, dupe)).Status);
    }

    [Fact]
    public async Task Another_firms_clients_are_the_same_answers_as_none()
    {
        var ours = await AddAsync();
        var theirs = (await Add(OtherAdmin)).Str("id")!;

        var foreignSurvivor = await Merge(theirs, ours);
        var missingSurvivor = await Merge("no-such-client", ours);
        var foreignMerged = await Merge(ours, theirs);
        var missingMerged = await Merge(ours, "no-such-client");

        Assert.Equal(404, foreignSurvivor.Status);
        Assert.Equal(missingSurvivor.Status, foreignSurvivor.Status);
        Assert.Equal(missingSurvivor.Body, foreignSurvivor.Body);
        Assert.Equal(400, foreignMerged.Status);
        Assert.Equal(missingMerged.Status, foreignMerged.Status);
        Assert.Equal(missingMerged.Body, foreignMerged.Body);
    }

    [Fact]
    public async Task Merging_needs_add_edit_on_clients()
    {
        var keep = await AddAsync();
        var dupe = await AddAsync("""{"given": "Jordy"}""");

        Assert.Equal(403, (await Merge(keep, dupe, Viewer)).Status);
        Assert.Equal(403, (await Merge(keep, dupe, Blocked)).Status);
    }

    [Fact]
    public async Task A_merge_is_logged_under_both_clients()
    {
        var keep = await AddAsync();
        var dupe = await AddAsync("""{"given": "Jordy"}""");
        var before = _world.Host.AccessLog.Events.Count;

        await Merge(keep, dupe);

        Assert.Equal([("client.merge", dupe, Admin, "allowed"), ("client.merge", keep, Admin, "allowed")], Rows()[before..]);
    }

    [Fact]
    public async Task A_refused_merge_is_logged_as_denied_under_the_unreachable_id()
    {
        var keep = await AddAsync();

        await Merge("no-such-client", keep);
        await Merge(keep, "no-such-merged");

        Assert.Equal([("client.merge", "no-such-client", Admin, "denied"), ("client.merge", "no-such-merged", Admin, "denied")], Rows()[^2..]);
    }

    [Fact]
    public async Task A_merge_with_no_debtor_store_composed_is_a_500_and_moves_nothing()
    {
        // The Lambda composes no debtor store until 18.11 ports it.
        using var world = new FirmWorld(services: services => Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<Core.Ports.IDebtorClientLinks>(services));
        await (await world.WithFirmsAsync()).WithAsync(FirmWorld.Member(Admin, isAdmin: true));
        var keep = (await world.Post("/v1/firm/clients", """{"name": {"given": "Jordan"}}""", Admin)).Str("id");
        var dupe = (await world.Post("/v1/firm/clients", """{"name": {"given": "Jordy"}}""", Admin)).Str("id");

        var answer = await world.Post($"/v1/firm/clients/{keep}/merge", $$"""{"merged_client_id": "{{dupe}}"}""", Admin);

        Assert.Equal(500, answer.Status);
        Assert.Equal("active", (await world.Get($"/v1/firm/clients/{dupe}", Admin)).Str("status"));
    }

    private Task<Answer> Add(string subject = Admin, string name = """{"given": "Jordan", "surname": "Example"}""", string extra = "") =>
        _world.Post("/v1/firm/clients", "{\"name\": " + name + extra + "}", subject);

    private async Task<string> AddAsync(string name = """{"given": "Jordan", "surname": "Example"}""")
    {
        var answer = await Add(name: name);
        Assert.Equal(201, answer.Status);
        return answer.Str("id")!;
    }

    private Task<Answer> Stage(string id, string value, string subject = Admin) =>
        _world.Put($"/v1/firm/clients/{id}/prospect-stage", "{\"prospect_stage\": " + value + "}", subject);

    private Task<Answer> Merge(string survivor, string merged, string subject = Admin) =>
        _world.Post($"/v1/firm/clients/{survivor}/merge", $$"""{"merged_client_id": "{{merged}}"}""", subject);

    private List<(string Action, string? ClientId, string Principal, string Outcome)> Rows() =>
        [.. _world.Host.AccessLog.Events.Select(e => (e.Action.ToWire(), e.ClientId, e.Principal, e.Outcome.ToWire()))];
}
