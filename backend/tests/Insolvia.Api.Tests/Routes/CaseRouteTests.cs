using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Firms;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_cases.py's route half: case CRUD behind auth, and who may see what —
/// TWO USERS, ONE FIRM, ONE CASE. Most of what matters is what the routes
/// REFUSE. (The store, item-shape and cursor tests of that file are the
/// conformance suites' and Core's now — see the class remarks of
/// <c>CaseStoreConformance</c>.)
/// </summary>
public sealed class CaseRouteTests : IAsyncLifetime, IDisposable
{
    private readonly CaseWorld _world = new();

    public static TheoryData<string, string> CaseRoutes => new()
    {
        { "POST", "/v1/cases" },
        { "GET", "/v1/cases" },
        { "GET", "/v1/cases/any-id" },
        { "PATCH", "/v1/cases/any-id" },
    };

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── Auth and provisioning ───────────────────────────────────────

    [Theory]
    [MemberData(nameof(CaseRoutes))]
    public async Task Every_case_route_requires_a_token(string method, string path)
    {
        Assert.Equal(401, (await _world.World.SendAsync(new HttpMethod(method), path, json: "{}")).Status);
    }

    [Theory]
    [MemberData(nameof(CaseRoutes))]
    public async Task A_signed_in_user_with_no_firm_is_403_not_401(string method, string path)
    {
        var answer = await _world.World.SendAsync(new HttpMethod(method), path, Frank, "{" + Tampa + "}");

        Assert.Equal(403, answer.Status);
        Assert.Equal("ForbiddenError", answer.Str("error"));
    }

    [Fact]
    public async Task A_disabled_user_is_refused_with_the_unprovisioned_answer()
    {
        var frank = await _world.Get("/v1/cases", Frank);
        var erin = await _world.Get("/v1/cases", Erin);

        Assert.Equal(403, erin.Status);
        Assert.Equal(frank.Body, erin.Body);
    }

    [Fact]
    public async Task A_suspended_firm_locks_out_even_its_admin()
    {
        var firm = (await _world.World.FirmAsync())!;
        await _world.Firms.UpdateFirmAsync(firm with { Status = FirmStatus.Suspended }, HttpAssert.Ct);

        Assert.Equal(403, (await _world.Get("/v1/cases")).Status);
    }

    [Fact]
    public async Task A_hidden_feature_is_403()
    {
        await _world.ReplaceAsync(FirmWorld.Member(Greg, role: FirmRole.Staff, permissions: FirmWorld.DefaultsWith(FirmRole.Staff, Feature.Cases, PermissionLevel.Hidden)));

        Assert.Equal(403, (await _world.Get("/v1/cases", Greg)).Status);
    }

    [Fact]
    public async Task Staff_cannot_open_a_matter_by_default_but_can_list()
    {
        Assert.Equal(403, (await _world.Post("/v1/cases", "{" + Tampa + "}", Greg)).Status);
        Assert.Equal(200, (await _world.Get("/v1/cases", Greg)).Status);
    }

    [Fact]
    public async Task View_only_can_read_but_not_write()
    {
        await _world.ReplaceAsync(FirmWorld.Member(Dana, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.Cases, PermissionLevel.ViewOnly)));
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}", Dana)).Status);
        Assert.Equal(403, (await _world.Patch($"/v1/cases/{caseId}", """{"court": "flmb", "division": "orlando"}""", Dana)).Status);
        Assert.Equal(403, (await _world.Post("/v1/cases", """{"chapter": 7, "court": "x", "division": "y"}""", Dana)).Status);
    }

    // ── Create ──────────────────────────────────────────────────────

    [Fact]
    public async Task Create_returns_the_case_with_the_district_derived()
    {
        var body = await _world.OpenCaseAsync();

        Assert.Equal(7, body["chapter"]!.GetValue<int>());
        Assert.Equal("flmb", body.Str("court"));
        Assert.Equal("tampa", body.Str("division"));
        Assert.Equal("Middle District of Florida", body.Str("district"));
        Assert.Equal("intake", body.Str("status"));
        Assert.Equal(body.Str("createdAt"), body.Str("updatedAt"));
        Assert.Equal(Alice, body.Str("createdBy"));
        Assert.Equal(["chapter", "court", "createdAt", "createdBy", "district", "division", "id", "status", "updatedAt"], Answer.Keys(body));
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{6}Z$", body.Str("createdAt")!);
    }

    [Fact]
    public async Task Create_ignores_a_firm_and_creator_supplied_by_the_client()
    {
        var clientId = await _world.AddClientAsync();
        var answer = await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "firmId": "{{Ids.FirmB}}", "createdBy": "{{Carol}}", "client_ids": ["{{clientId}}"]}""");

        Assert.Equal(201, answer.Status);
        var stored = (await _world.StoredAsync(answer.Str("id")!))!;
        Assert.Equal((Ids.FirmA, Alice), (stored.FirmId, stored.CreatedBy));
    }

    [Fact]
    public async Task Creating_a_case_links_its_creator()
    {
        var caseId = await _world.OpenCaseIdAsync(Bob);

        Assert.Contains(await _world.Cases.AssigneesAsync(caseId, HttpAssert.Ct), assignment => assignment.Subject == Bob);
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}", Bob)).Status);
        Assert.Equal([caseId], Ids_((await _world.Get("/v1/cases", Bob)).Object["cases"]));
    }

    [Theory]
    [InlineData("filed")]
    [InlineData("prospect")]
    public async Task Create_starts_at_intake_even_if_asked_otherwise(string asked)
    {
        var clientId = await _world.AddClientAsync();

        var answer = await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "status": "{{asked}}", "client_ids": ["{{clientId}}"]}""");

        Assert.Equal("intake", answer.Str("status"));
    }

    [Theory]
    [InlineData("""{"court": "flmb", "division": "tampa"}""", "chapter")]
    [InlineData("""{"chapter": 9, "court": "flmb", "division": "tampa"}""", "chapter")]
    [InlineData("""{"chapter": "7", "court": "flmb", "division": "tampa"}""", "chapter")]
    [InlineData("""{"chapter": true, "court": "flmb", "division": "tampa"}""", "chapter")]
    [InlineData("""{"chapter": 7}""", "court")]
    [InlineData("""{"chapter": 7, "court": "   ", "division": "tampa"}""", "court")]
    [InlineData("""{"chapter": 7, "court": "nyeb", "division": "brooklyn"}""", "court")]
    [InlineData("""{"chapter": 7, "court": "flmb"}""", "division")]
    [InlineData("""{"chapter": 7, "court": "flmb", "division": "miami"}""", "division")]
    [InlineData("""{"chapter": 7, "court": "flmb", "division": "tampa", "district": "NDCA"}""", "district")]
    public async Task Create_rejects_bad_input_naming_the_field(string payload, string field)
    {
        var answer = await _world.Post("/v1/cases", payload);

        Assert.Equal(400, answer.Status);
        Assert.Equal("ValidationError", answer.Str("error"));
        Assert.Contains(field, answer.FieldErrors);
    }

    [Fact]
    public async Task Create_rejects_a_non_object_body()
    {
        Assert.Equal(400, (await _world.Post("/v1/cases", """["not", "an", "object"]""")).Status);
    }

    // ── Tenancy ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_colleague_with_access_all_cases_reads_the_firms_case_unlinked()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.DoesNotContain(await _world.Cases.AssigneesAsync(caseId, HttpAssert.Ct), assignment => assignment.Subject == Dana);
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}", Dana)).Status);
    }

    [Fact]
    public async Task An_admin_sees_a_colleagues_case_without_being_linked()
    {
        var caseId = await _world.OpenCaseIdAsync(Bob);

        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}")).Status);
    }

    [Fact]
    public async Task A_restricted_colleague_cannot_read_an_unlinked_case()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}", Bob)).Status);
    }

    [Fact]
    public async Task Linking_a_colleague_makes_the_case_reachable_and_listed()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}", Bob)).Status);

        Assert.Equal(204, (await _world.Put($"/v1/cases/{caseId}/assignees/{Bob}", null)).Status);

        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}", Bob)).Status);
        Assert.Equal([caseId], Ids_((await _world.Get("/v1/cases", Bob)).Object["cases"]));
    }

    [Fact]
    public async Task Another_firms_case_is_invisible_to_its_admin_and_cannot_be_updated()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}", Carol)).Status);
        Assert.Equal(404, (await _world.Patch($"/v1/cases/{caseId}", """{"court": "flmb", "division": "orlando"}""", Carol)).Status);
        Assert.Equal("tampa", (await _world.StoredAsync(caseId))!.Division);
    }

    [Fact]
    public async Task A_foreign_case_is_indistinguishable_from_a_missing_one()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var foreign = await _world.Get($"/v1/cases/{caseId}", Carol);
        var unlinked = await _world.Get($"/v1/cases/{caseId}", Bob);
        var missing = await _world.Get("/v1/cases/does-not-exist", Carol);

        Assert.All([foreign, unlinked, missing], answer => Assert.Equal(404, answer.Status));
        Assert.Equal(foreign.Body, unlinked.Body);
        Assert.Equal(foreign.Body, missing.Body);
        Assert.Equal("""{"error":"NotFoundError","message":"case not found"}""", missing.Body);
    }

    [Fact]
    public async Task The_list_shows_only_the_firms_cases_and_a_restricted_user_only_theirs()
    {
        await _world.OpenCaseAsync();
        var bobs = await _world.OpenCaseIdAsync(Bob);
        await _world.OpenCaseAsync(Carol);

        Assert.Single((await _world.Get("/v1/cases", Carol)).Object["cases"]!.AsArray());
        Assert.Equal(2, (await _world.Get("/v1/cases")).Object["cases"]!.AsArray().Count);
        Assert.Equal([bobs], Ids_((await _world.Get("/v1/cases", Bob)).Object["cases"]));
    }

    // ── Read and list ───────────────────────────────────────────────

    [Fact]
    public async Task Get_returns_the_case_as_created()
    {
        var created = await _world.OpenCaseAsync();

        var fetched = await _world.Get($"/v1/cases/{created.Str("id")}");

        Assert.Equal(200, fetched.Status);
        Assert.Equal(Http.FlaskJson.Serialize(created), fetched.Body);
    }

    [Fact]
    public async Task The_list_paginates_and_omits_the_cursor_on_the_last_page()
    {
        for (var index = 0; index < 3; index++)
        {
            await _world.OpenCaseAsync();
        }

        var first = (await _world.Get("/v1/cases?limit=2")).Object;
        Assert.Equal(2, first["cases"]!.AsArray().Count);
        Assert.NotNull(first["nextCursor"]);

        var second = (await _world.Get($"/v1/cases?limit=2&cursor={Uri.EscapeDataString(first.Str("nextCursor")!)}")).Object;
        Assert.Single(second["cases"]!.AsArray());
        Assert.False(second.ContainsKey("nextCursor"));
    }

    [Fact]
    public async Task A_cursor_from_the_other_listing_is_refused()
    {
        for (var index = 0; index < 3; index++)
        {
            await _world.OpenCaseAsync();
        }

        var cursor = (await _world.Get("/v1/cases?limit=2")).Str("nextCursor")!;
        await _world.ReplaceAsync(FirmWorld.Member(Alice));

        Assert.Equal(400, (await _world.Get($"/v1/cases?limit=2&cursor={Uri.EscapeDataString(cursor)}")).Status);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task The_list_rejects_a_bad_limit(string limit)
    {
        Assert.Equal(400, (await _world.Get($"/v1/cases?limit={limit}")).Status);
    }

    [Fact]
    public async Task The_list_rejects_a_forged_cursor()
    {
        Assert.Equal(400, (await _world.Get("/v1/cases?cursor=not-base64!!")).Status);
    }

    // ── Update ──────────────────────────────────────────────────────

    [Fact]
    public async Task Update_changes_only_what_was_sent()
    {
        var created = await _world.OpenCaseAsync();

        var updated = (await _world.Patch($"/v1/cases/{created.Str("id")}", """{"court": "flmb", "division": "orlando"}""")).Object;

        Assert.Equal("orlando", updated.Str("division"));
        Assert.Equal("Middle District of Florida", updated.Str("district"));
        Assert.Equal(7, updated["chapter"]!.GetValue<int>());
        Assert.Equal("intake", updated.Str("status"));
    }

    [Fact]
    public async Task A_colleague_can_update_and_created_by_is_not_rewritten()
    {
        var created = await _world.OpenCaseAsync();

        var answer = await _world.Patch($"/v1/cases/{created.Str("id")}", """{"status": "ready_to_file"}""", Dana);

        Assert.Equal(200, answer.Status);
        Assert.Equal(Alice, answer.Str("createdBy"));
        Assert.True(string.CompareOrdinal(answer.Str("updatedAt"), created.Str("updatedAt")) >= 0);
        Assert.Equal(created.Str("createdAt"), answer.Str("createdAt"));
    }

    [Fact]
    public async Task Update_rejects_an_empty_body_an_unknown_status_and_a_missing_case()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(400, (await _world.Patch($"/v1/cases/{caseId}", "{}")).Status);
        var unknown = await _world.Patch($"/v1/cases/{caseId}", """{"status": "shredded"}""");
        Assert.Equal(400, unknown.Status);
        Assert.Contains("status", unknown.FieldErrors);
        Assert.Equal(404, (await _world.Patch("/v1/cases/nope", """{"court": "flmb", "division": "orlando"}""")).Status);
    }

    // ── The exemption election (issue #346) ─────────────────────────

    [Fact]
    public async Task A_case_starts_with_no_election_and_stores_and_echoes_one()
    {
        var created = await _world.OpenCaseAsync();
        Assert.False(created.ContainsKey("exemptionSet"));

        var updated = await _world.Patch($"/v1/cases/{created.Str("id")}", """{"exemption_set": "state_and_federal_nonbankruptcy"}""");

        Assert.Equal(200, updated.Status);
        Assert.Equal("state_and_federal_nonbankruptcy", updated.Str("exemptionSet"));
        Assert.Equal("state_and_federal_nonbankruptcy", (await _world.Get($"/v1/cases/{created.Str("id")}")).Str("exemptionSet"));
    }

    [Fact]
    public async Task The_election_must_be_one_of_the_two_106c_answers()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var answer = await _world.Patch($"/v1/cases/{caseId}", """{"exemption_set": "whatever_is_best"}""");

        Assert.Equal(400, answer.Status);
        Assert.Contains("exemption_set", answer.FieldErrors);
    }

    [Fact]
    public async Task A_case_with_no_debtor_state_yet_accepts_either_election()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(200, (await _world.Patch($"/v1/cases/{caseId}", """{"exemption_set": "federal"}""")).Status);
    }

    // ── The access log ──────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_case_is_recorded_under_the_person()
    {
        var caseId = await _world.OpenCaseIdAsync(Dana);

        var caseRows = _world.Events.Where(e => e.CaseId is not null).ToList();
        Assert.Equal([(AccessAction.CaseCreate, caseId, Dana)], caseRows.Select(e => (e.Action, e.CaseId, e.Principal)));
    }

    [Fact]
    public async Task The_client_copied_into_a_case_is_recorded_as_read()
    {
        var clientId = await _world.AddClientAsync();
        await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""");

        var reads = _world.Events.Where(e => e.Action == AccessAction.ClientRead).ToList();
        Assert.Equal([(clientId, AccessOutcome.Allowed, Alice)], reads.Select(e => (e.ClientId, e.Outcome, e.Principal)));
    }

    [Fact]
    public async Task A_read_is_recorded_and_a_refused_one_as_denied_in_or_out_of_the_firm()
    {
        var caseId = await _world.OpenCaseIdAsync();

        await _world.Get($"/v1/cases/{caseId}");
        Assert.Equal((AccessAction.CaseRead, AccessOutcome.Allowed, Alice), Last());
        await _world.Get($"/v1/cases/{caseId}", Carol);
        Assert.Equal((AccessAction.CaseRead, AccessOutcome.Denied, Carol), Last());
        Assert.Equal(caseId, _world.Events[^1].CaseId);
        await _world.Get($"/v1/cases/{caseId}", Bob);
        Assert.Equal((AccessAction.CaseRead, AccessOutcome.Denied, Bob), Last());
        await _world.Patch($"/v1/cases/{caseId}", """{"court": "flmb", "division": "orlando"}""", Carol);
        Assert.Equal((AccessAction.CaseUpdate, AccessOutcome.Denied, Carol), Last());
    }

    [Fact]
    public async Task Listing_unauthenticated_and_unprovisioned_requests_record_nothing()
    {
        await _world.OpenCaseAsync();
        var before = _world.Events.Count;

        await _world.Get("/v1/cases");
        await _world.Get("/v1/cases/anything", null);
        await _world.Get("/v1/cases/anything", Frank);

        Assert.Equal(before, _world.Events.Count);
    }

    // ── Opening a case for a client (ADR 0022) ──────────────────────

    [Fact]
    public async Task A_case_opened_for_a_client_starts_with_that_client_as_debtor_1()
    {
        var clientId = await _world.AddClientAsync(extra: """ "name": {"given": "Jordan", "surname": "Example"}, "phone": "555-0100", "lead_source": "Referral" """);
        var caseId = (await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""")).Str("id");

        var debtor = Assert.Single((await _world.Get($"/v1/cases/{caseId}/debtors")).Object["debtors"]!.AsArray())!.AsObject();

        Assert.Equal("debtor_1", debtor.Str("filing_role"));
        Assert.Equal(clientId, debtor.Str("client_id"));
        Assert.Equal("""{"given":"Jordan","surname":"Example"}""", Http.FlaskJson.Serialize(debtor["name"]));
        Assert.Equal("555-0100", debtor.Str("phone"));
        Assert.False(debtor.ContainsKey("lead_source"));
        var copied = $$"""{"client_id":"{{clientId}}","source":"client"}""";
        Assert.Equal($$"""{"name.given":{{copied}},"name.surname":{{copied}},"phone":{{copied}}}""", Http.FlaskJson.Serialize(debtor["provenance"]));
        Assert.Equal("[]", debtor["differs_from_client"]!.ToJsonString());
    }

    [Fact]
    public async Task Two_clients_open_a_joint_case_in_filing_order()
    {
        var first = await _world.AddClientAsync(extra: """ "name": {"given": "Jordan"} """);
        var second = await _world.AddClientAsync(extra: """ "name": {"given": "Sam"} """);
        var caseId = (await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{first}}", "{{second}}"]}""")).Str("id");

        var listed = (await _world.Get($"/v1/cases/{caseId}/debtors")).Object["debtors"]!.AsArray();

        Assert.Equal([("debtor_1", first), ("debtor_2", second)], listed.Select(d => (d!.AsObject().Str("filing_role"), d.AsObject().Str("client_id"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData(""", "client_ids": [] """)]
    [InlineData(""", "client_ids": ["a", "b", "c"] """)]
    public async Task A_case_needs_one_or_two_clients(string clients)
    {
        var answer = await _world.Post("/v1/cases", "{" + Tampa + clients + "}");

        Assert.Equal(400, answer.Status);
        Assert.Contains("client_ids", answer.FieldErrors);
    }

    [Fact]
    public async Task The_same_client_cannot_be_both_debtors()
    {
        var clientId = await _world.AddClientAsync();

        var answer = await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}", "{{clientId}}"]}""");

        Assert.Equal(400, answer.Status);
        Assert.Contains("client_ids", answer.FieldErrors);
        Assert.Empty(await _world.FirmCasesAsync());
    }

    [Fact]
    public async Task Another_firms_client_is_indistinguishable_from_no_client_and_both_reads_are_denied()
    {
        var carols = await _world.AddClientAsync(Carol);

        var foreign = await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{carols}}"]}""");
        var unknown = await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["no-such-client"]}""");

        Assert.Equal(400, foreign.Status);
        Assert.Equal(foreign.Body, unknown.Body);
        Assert.Empty(await _world.FirmCasesAsync());
        var denied = _world.Events.Where(e => e.Outcome == AccessOutcome.Denied).Select(e => (e.Action, e.ClientId));
        Assert.Equal([(AccessAction.ClientRead, carols), (AccessAction.ClientRead, "no-such-client")], denied);
    }

    [Fact]
    public async Task An_archived_client_is_restored_before_a_case_is_opened_for_them()
    {
        var clientId = await _world.AddClientAsync();
        await _world.Put($"/v1/firm/clients/{clientId}/status", """{"status": "archived"}""");

        var answer = await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""");

        Assert.Equal(400, answer.Status);
        Assert.Contains("archived", answer.Object["fields"]!["client_ids"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // The Python claimed the merge between the route's read and the store's
    // transaction (a monkeypatched get_client); the transaction's own refusal
    // is the store conformance suite's. Here the claim lands first and the
    // route's read refuses with the same body.
    [Fact]
    public async Task A_client_being_merged_away_cannot_have_a_case_opened()
    {
        var clientId = await _world.AddClientAsync();
        var survivor = await _world.AddClientAsync(extra: """ "name": {"given": "Jordan B."} """);
        Assert.True(await _world.Firms.ClaimClientMergeAsync(Ids.FirmA, clientId, survivor, HttpAssert.Ct));

        var answer = await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""");

        Assert.Equal(400, answer.Status);
        Assert.Equal("""{"client_ids":"That client is being merged into another client."}""", Http.FlaskJson.Serialize(answer.Object["fields"]));
        Assert.Empty(await _world.FirmCasesAsync());
    }

    [Fact]
    public async Task Opening_a_case_needs_the_client_directory_too()
    {
        var clientId = await _world.AddClientAsync();
        const string noClients = "00000000-0000-4000-8000-0000000c0de0";
        await _world.World.WithAsync(FirmWorld.Member(noClients, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.Clients, PermissionLevel.Hidden)));

        Assert.Equal(403, (await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""", noClients)).Status);
    }

    [Fact]
    public async Task A_restricted_creator_is_linked_and_sees_their_debtor()
    {
        var caseId = await _world.OpenCaseIdAsync(Bob);

        var debtor = Assert.Single((await _world.Get($"/v1/cases/{caseId}/debtors", Bob)).Object["debtors"]!.AsArray());
        Assert.Equal("debtor_1", debtor!.AsObject().Str("filing_role"));
    }

    // ── Assignees ───────────────────────────────────────────────────

    [Fact]
    public async Task Assignees_are_subjects_oldest_first_and_assignment_is_idempotent()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(204, (await _world.Put($"/v1/cases/{caseId}/assignees/{Bob}", null)).Status);
        Assert.Equal(204, (await _world.Put($"/v1/cases/{caseId}/assignees/{Bob}", null)).Status);

        var listed = (await _world.Get($"/v1/cases/{caseId}/assignees")).Object["assignees"]!.AsArray();
        Assert.Equal([Alice, Bob], listed.Select(entry => entry!.AsObject().Str("subject")));
        Assert.Equal(["assignedAt", "assignedBy", "subject"], Answer.Keys(listed[1]));
        Assert.Equal(Alice, listed[1]!.AsObject().Str("assignedBy"));
        Assert.Equal((AccessAction.CaseUpdate, AccessOutcome.Allowed, Alice), Last());
    }

    [Fact]
    public async Task Only_an_active_member_of_the_callers_firm_can_be_assigned()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal("""{"error":"NotFoundError","message":"firm user not found"}""", (await _world.Put($"/v1/cases/{caseId}/assignees/{Carol}", null)).Body);
        Assert.Equal(404, (await _world.Put($"/v1/cases/{caseId}/assignees/{Erin}", null)).Status);
        Assert.Equal(404, (await _world.Put($"/v1/cases/{caseId}/assignees/{Frank}", null)).Status);
    }

    [Fact]
    public async Task A_caller_who_cannot_see_the_case_cannot_learn_or_change_who_is_on_it()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/assignees", Bob)).Status);
        Assert.Equal(404, (await _world.Put($"/v1/cases/{caseId}/assignees/{Bob}", null, Bob)).Status);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/assignees", Carol)).Status);
    }

    [Fact]
    public async Task Unassigning_answers_once_and_the_last_person_may_go()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(204, (await _world.Delete($"/v1/cases/{caseId}/assignees/{Alice}")).Status);
        Assert.Equal("""{"error":"NotFoundError","message":"assignee not found"}""", (await _world.Delete($"/v1/cases/{caseId}/assignees/{Alice}")).Body);
        Assert.Empty(await _world.Cases.AssigneesAsync(caseId, HttpAssert.Ct));
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}")).Status);
    }

    // ── The client's cases (ADR 0022) and the courts ────────────────

    [Fact]
    public async Task A_clients_cases_are_the_ones_the_caller_may_see_with_the_role()
    {
        var clientId = await _world.AddClientAsync();
        var seen = (await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""")).Str("id");

        var answer = await _world.Get($"/v1/firm/clients/{clientId}/cases");
        var entry = Assert.Single(answer.Object["cases"]!.AsArray())!.AsObject();
        Assert.Equal("debtor_1", entry.Str("filing_role"));
        Assert.Equal(seen, entry["case"]!.AsObject().Str("id"));

        // Bob may see the client and none of the cases: an empty list, no count.
        Assert.Equal("""{"cases":[]}""", (await _world.Get($"/v1/firm/clients/{clientId}/cases", Bob)).Body);
        Assert.Equal(404, (await _world.Get($"/v1/firm/clients/{clientId}/cases", Carol)).Status);
        Assert.Equal((AccessAction.ClientRead, AccessOutcome.Denied, Carol), Last());
    }

    [Fact]
    public async Task The_courts_are_the_registry_release_in_force_for_any_member()
    {
        var answer = await _world.Get("/v1/courts", Greg);

        Assert.Equal(200, answer.Status);
        Assert.Equal(["districts", "effectiveDate", "releaseId"], Answer.Keys(answer.Json));
        var flmb = answer.Object["districts"]!.AsArray().Single(d => d!.AsObject().Str("code") == "flmb")!.AsObject();
        Assert.Equal("Middle District of Florida", flmb.Str("name"));
        Assert.Contains(flmb["divisions"]!.AsArray(), division => division!.AsObject().Str("code") == "tampa");
        Assert.Equal(403, (await _world.Get("/v1/courts", Frank)).Status);
        Assert.Equal(401, (await _world.Get("/v1/courts", null)).Status);
    }

    private (AccessAction, AccessOutcome, string) Last()
    {
        var last = _world.Events[^1];
        return (last.Action, last.Outcome, last.Principal);
    }

    private static List<string?> Ids_(JsonNode? cases) => [.. cases!.AsArray().Select(found => found!.AsObject().Str("id"))];
}
