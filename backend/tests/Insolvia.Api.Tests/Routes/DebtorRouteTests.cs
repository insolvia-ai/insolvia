using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Debtors;
using Insolvia.Core.Firms;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_debtor_routes.py: the debtors of a case — tenancy, the save's 201/200,
/// provenance, the sealed tax id and its last-four view, the client link
/// (ADR 0022), <c>differs_from_client</c>, and the copies to and from the
/// client record. The other firm is Carol's; the linked-only colleague is Bob.
/// </summary>
public sealed class DebtorRouteTests : IAsyncLifetime, IDisposable
{
    private const string Typed = """{"source": "staff_typed"}""";
    private const string TaxId = """ "tax_id": {"kind": "ssn", "value": "987-65-4321"}, "provenance": {"tax_id": {"source": "staff_typed"}} """;

    private readonly StaleOnce _stale = new();
    private readonly CaseWorld _world;

    public DebtorRouteTests()
    {
        _world = new CaseWorld(services =>
        {
            services.RemoveAll<IDebtorStore>();
            services.AddSingleton<IDebtorStore>(provider =>
            {
                _stale.Inner = provider.GetRequiredService<MemoryDebtorStore>();
                return _stale;
            });
        });
    }

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── Tenancy ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("PUT", "/v1/cases/any-id/debtors/debtor_1")]
    [InlineData("GET", "/v1/cases/any-id/debtors")]
    public async Task Every_route_refuses_an_unauthenticated_caller(string method, string path)
    {
        Assert.Equal(401, (await _world.World.SendAsync(new HttpMethod(method), path, json: "{}")).Status);
    }

    [Fact]
    public async Task Another_firms_case_is_not_found_and_indistinguishable_from_a_missing_one()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Put(caseId, """ "name": {"given": "Ada"}, "provenance": {"name.given": """ + Typed + "}");

        Assert.Equal(404, (await Put(caseId, subject: Carol)).Status);
        var foreign = await _world.Get($"/v1/cases/{caseId}/debtors", Carol);
        var missing = await _world.Get("/v1/cases/no-such-case/debtors");
        Assert.Equal(404, foreign.Status);
        Assert.Equal(missing.Body, foreign.Body);
    }

    // ── The save ────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_debtor_answers_201_and_updating_200_keeping_its_id()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var named = """ "name": {"given": "Ada"}, "provenance": {"name.given": """ + Typed + "}";

        var first = await Put(caseId, named, "non_filing_spouse");
        var second = await Put(caseId, named.Replace("Ada", "Augusta", StringComparison.Ordinal), "non_filing_spouse");

        Assert.Equal((201, 200), (first.Status, second.Status));
        Assert.Equal(first.Str("id"), second.Str("id"));
        Assert.Equal("""{"given":"Augusta"}""", Http.FlaskJson.Serialize(second.Object["name"]));
    }

    [Fact]
    public async Task Debtor_1_already_exists_when_the_case_is_opened_and_an_empty_body_saves()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(200, (await Put(caseId, """ "name": {"given": "Ada"}, "provenance": {"name.given": """ + Typed + "}")).Status);
        Assert.Equal(200, (await Put(caseId)).Status);
    }

    [Fact]
    public async Task A_second_debtor_is_linked_never_minted_by_a_save()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var refused = await Put(caseId, """ "name": {"given": "Sam"}, "provenance": {"name.given": """ + Typed + "}", "debtor_2");

        Assert.Equal(400, refused.Status);
        Assert.Contains("client_id", refused.FieldErrors);
        Assert.Equal(["debtor_1"], await RolesAsync(caseId));
    }

    [Fact]
    public async Task An_unknown_filing_role_is_rejected()
    {
        Assert.Equal(400, (await Put(await _world.OpenCaseIdAsync(), role: "debtor_3")).Status);
    }

    [Theory]
    [InlineData(""" "name": {"given": "Ada"} """)]
    [InlineData(""" "name": {"given": "Ada"}, "provenance": {"name.given": {"source": "ai_extracted"}} """)]
    public async Task A_value_without_confirmed_provenance_is_rejected_with_its_field(string body)
    {
        var answer = await Put(await _world.OpenCaseIdAsync(), body);

        Assert.Equal(400, answer.Status);
        Assert.Contains("provenance.name.given", answer.FieldErrors);
    }

    // ── The tax id ──────────────────────────────────────────────────

    [Fact]
    public async Task A_tax_id_is_stored_and_only_its_last_four_ever_comes_back()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var saved = await Put(caseId, TaxId);
        var listed = await _world.Get($"/v1/cases/{caseId}/debtors");

        Assert.Equal(200, saved.Status);
        Assert.Equal("""{"kind":"ssn","last_four":"4321"}""", Http.FlaskJson.Serialize(saved.Object["tax_id"]));
        Assert.Equal("""{"kind":"ssn","last_four":"4321"}""", Http.FlaskJson.Serialize(listed.Object["debtors"]![0]!["tax_id"]));
        foreach (var body in new[] { saved.Body, listed.Body })
        {
            Assert.DoesNotContain("987654321", body, StringComparison.Ordinal);
            Assert.DoesNotContain("987-65-4321", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_digits_are_sealed_not_stored_and_saving_never_reads_the_full_value()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Put(caseId, TaxId);
        await _world.Get($"/v1/cases/{caseId}/debtors");

        var debtor = (await _world.Service<IDebtorStore>().GetAsync(caseId, FilingRole.Debtor1, HttpAssert.Ct))!;
        var sealedId = (await _world.Service<ITaxIdStore>().GetAsync(caseId, debtor.TaxId!.Ref, HttpAssert.Ct))!;
        Assert.DoesNotContain("987654321", sealedId.ToString(), StringComparison.Ordinal);
        Assert.Equal(Ids.FirmA, sealedId.FirmId);
        Assert.DoesNotContain(_world.Events, e => e.Action == AccessAction.TaxIdRead);
    }

    [Fact]
    public async Task A_tax_id_needs_provenance_at_the_one_path_and_a_valid_number()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Contains("provenance.tax_id", (await Put(caseId, """ "tax_id": {"kind": "ssn", "value": "987-65-4321"} """)).FieldErrors);
        Assert.Contains("tax_id.value", (await Put(caseId, TaxId.Replace("987-65-4321", "000-00-0000", StringComparison.Ordinal))).FieldErrors);
    }

    [Fact]
    public async Task Echoing_the_last_four_keeps_the_number_a_wrong_echo_is_400_and_leaving_it_out_clears_it()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Put(caseId, TaxId);

        var kept = await Put(caseId, """ "name": {"given": "Ada"}, "tax_id": {"kind": "ssn", "last_four": "4321"}, "provenance": {"name.given": """ + Typed + """, "tax_id": """ + Typed + "}");
        Assert.Equal(200, kept.Status);
        Assert.Equal("""{"kind":"ssn","last_four":"4321"}""", Http.FlaskJson.Serialize(kept.Object["tax_id"]));

        var wrong = await Put(caseId, """ "tax_id": {"kind": "ssn", "last_four": "9999"}, "provenance": {"tax_id": """ + Typed + "}");
        Assert.Equal(400, wrong.Status);
        Assert.Contains("tax_id", wrong.FieldErrors);

        var cleared = await Put(caseId);
        Assert.Equal(200, cleared.Status);
        Assert.False(cleared.Object.ContainsKey("tax_id"));
    }

    // ── The list ────────────────────────────────────────────────────

    [Fact]
    public async Task Debtors_list_in_the_order_the_forms_print_them_with_their_provenance()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Put(caseId, role: "non_filing_spouse");
        await Link(caseId, "debtor_2", await _world.AddClientAsync());
        await Put(caseId, """ "name": {"given": "Ada"}, "provenance": {"name.given": """ + Typed + "}");

        Assert.Equal(["debtor_1", "debtor_2", "non_filing_spouse"], await RolesAsync(caseId));
        Assert.Equal("""{"name.given":{"source":"staff_typed"}}""", Http.FlaskJson.Serialize((await DebtorsAsync(caseId))[0]["provenance"]));
    }

    [Fact]
    public async Task The_body_is_flasks_key_order_with_identity_first_class()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var debtor = (await DebtorsAsync(caseId))[0];

        Assert.Equal(["case_id", "client_id", "created_at", "differs_from_client", "filing_role", "id", "name", "provenance", "updated_at"], Answer.Keys(debtor));
    }

    // ── The access log ──────────────────────────────────────────────

    [Fact]
    public async Task A_save_is_an_update_a_list_a_read_a_refusal_denied_and_a_rejected_body_nothing()
    {
        var caseId = await _world.OpenCaseIdAsync();

        await Put(caseId);
        Assert.Equal(AccessAction.CaseUpdate, _world.Events[^1].Action);
        await _world.Get($"/v1/cases/{caseId}/debtors");
        Assert.Equal(AccessAction.CaseRead, _world.Events[^1].Action);
        await _world.Get($"/v1/cases/{caseId}/debtors", Carol);
        Assert.Equal((AccessOutcome.Denied, Carol), (_world.Events[^1].Outcome, _world.Events[^1].Principal));

        var before = _world.Events.Count;
        await Put(caseId, """ "name": {"given": "Ada"} """);
        Assert.Equal(before, _world.Events.Count);
    }

    [Fact]
    public async Task A_second_first_save_cannot_erase_the_first_ones_id()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var named = """ "name": {"given": "Ada"}, "provenance": {"name.given": """ + Typed + "}";
        var first = await Put(caseId, named, "non_filing_spouse");
        Assert.Equal(201, first.Status);

        _stale.MissNextGet = true;
        var second = await Put(caseId, named.Replace("Ada", "Augusta", StringComparison.Ordinal), "non_filing_spouse");

        Assert.Equal(200, second.Status);
        Assert.Equal((first.Str("id"), first.Str("created_at")), (second.Str("id"), second.Str("created_at")));
        Assert.Equal("""{"given":"Augusta"}""", Http.FlaskJson.Serialize(second.Object["name"]));
    }

    // ── Who may ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_colleague_on_the_matter_can_run_its_intake()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/debtors", Bob)).Status);

        Assert.Equal(204, (await _world.Put($"/v1/cases/{caseId}/assignees/{Bob}", null)).Status);

        Assert.Equal(200, (await Put(caseId, """ "name": {"given": "Ada"}, "provenance": {"name.given": """ + Typed + "}", subject: Bob)).Status);
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}/debtors", Bob)).Status);
    }

    [Fact]
    public async Task Intake_hidden_is_403_and_view_only_reads_but_does_not_save()
    {
        var caseId = await _world.OpenCaseIdAsync();

        await RestrictAliceAsync(Feature.Intake, PermissionLevel.Hidden);
        Assert.Equal(403, (await _world.Get($"/v1/cases/{caseId}/debtors")).Status);

        await RestrictAliceAsync(Feature.Intake, PermissionLevel.ViewOnly);
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}/debtors")).Status);
        Assert.Equal(403, (await Put(caseId, """ "name": {"given": "Ada"}, "provenance": {"name.given": """ + Typed + "}")).Status);
    }

    // ── The client link (ADR 0022) ──────────────────────────────────

    [Fact]
    public async Task Linking_a_client_to_an_empty_role_copies_them_in()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var sam = await _world.AddClientAsync(extra: """ "name": {"given": "Sam"}, "email": "sam@example.test" """);

        var answer = await Link(caseId, "debtor_2", sam);

        Assert.Equal(201, answer.Status);
        Assert.Equal(("debtor_2", sam, "sam@example.test"), (answer.Str("filing_role"), answer.Str("client_id"), answer.Str("email")));
        Assert.Equal($$"""{"client_id":"{{sam}}","source":"client"}""", Http.FlaskJson.Serialize(answer.Object["provenance"]!["email"]));
        Assert.Equal("[]", answer.Object["differs_from_client"]!.ToJsonString());
    }

    [Fact]
    public async Task Re_linking_moves_the_link_and_never_the_case_facts()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var original = (await DebtorsAsync(caseId))[0];
        var other = await _world.AddClientAsync(extra: """ "name": {"given": "Robin", "surname": "Other"} """);

        var answer = await Link(caseId, "debtor_1", other);

        Assert.Equal(200, answer.Status);
        Assert.Equal((original.Str("id"), other), (answer.Str("id"), answer.Str("client_id")));
        Assert.Equal(Http.FlaskJson.Serialize(original["name"]), Http.FlaskJson.Serialize(answer.Object["name"]));
        Assert.Equal("""["name.given","name.surname"]""", answer.Object["differs_from_client"]!.ToJsonString());
    }

    [Fact]
    public async Task One_client_holds_one_role_per_case()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var debtor1 = (await DebtorsAsync(caseId))[0];

        var answer = await Link(caseId, "debtor_2", debtor1.Str("client_id")!);

        Assert.Equal(400, answer.Status);
        Assert.Contains("client_id", answer.FieldErrors);
        Assert.Equal(["debtor_1"], await RolesAsync(caseId));
    }

    // The Python claimed the merge between the route's read and the store's
    // conditional write; that condition is the store conformance suite's.
    [Theory]
    [InlineData("debtor_2")]
    [InlineData("debtor_1")]
    public async Task A_client_being_merged_away_cannot_be_linked(string role)
    {
        var caseId = await _world.OpenCaseIdAsync();
        var before = await DebtorsAsync(caseId);
        var sam = await _world.AddClientAsync(extra: """ "name": {"given": "Sam"} """);
        var survivor = await _world.AddClientAsync(extra: """ "name": {"given": "Samuel"} """);
        Assert.True(await _world.Firms.ClaimClientMergeAsync(Ids.FirmA, sam, survivor, HttpAssert.Ct));

        var answer = await Link(caseId, role, sam);

        Assert.Equal(400, answer.Status);
        Assert.Equal("""{"client_id":"That client is being merged into another client."}""", Http.FlaskJson.Serialize(answer.Object["fields"]));
        Assert.Equal(["debtor_1"], await RolesAsync(caseId));
        Assert.Equal(before[0].Str("client_id"), (await DebtorsAsync(caseId))[0].Str("client_id"));
    }

    [Fact]
    public async Task Another_firms_client_cannot_be_linked_and_a_client_id_is_required()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var carols = await _world.AddClientAsync(Carol);

        var foreign = await Link(caseId, "debtor_2", carols);
        var unknown = await Link(caseId, "debtor_2", "no-such-client");

        Assert.Equal(400, foreign.Status);
        Assert.Equal(unknown.Body, foreign.Body);
        Assert.Contains("client_id", (await Link(caseId, "debtor_2", "  ")).FieldErrors);
    }

    [Fact]
    public async Task Linking_needs_the_client_directory()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var sam = await _world.AddClientAsync();
        await RestrictAliceAsync(Feature.Clients, PermissionLevel.Hidden);

        Assert.Equal(403, (await Link(caseId, "debtor_2", sam)).Status);
    }

    [Fact]
    public async Task A_questionnaire_save_keeps_the_client_link()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var before = (await DebtorsAsync(caseId))[0];

        var saved = await Put(caseId, """ "client_id": "someone-else", "name": {"given": "Typed"}, "provenance": {"name.given": """ + Typed + "}");

        Assert.Equal(200, saved.Status);
        Assert.Equal(before.Str("client_id"), saved.Str("client_id"));
        Assert.Equal("""["name.given","name.surname"]""", saved.Object["differs_from_client"]!.ToJsonString());
    }

    [Fact]
    public async Task A_caller_without_the_client_directory_is_not_told_how_a_client_differs()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await RestrictAliceAsync(Feature.Clients, PermissionLevel.Hidden);

        var debtor = (await DebtorsAsync(caseId))[0];

        Assert.True(debtor.ContainsKey("client_id"));
        Assert.False(debtor.ContainsKey("differs_from_client"));
    }

    // ── Client provenance across a save ─────────────────────────────

    [Fact]
    public async Task An_untouched_copied_field_keeps_client_provenance_across_a_save()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var loaded = (await DebtorsAsync(caseId))[0];
        var body = Echo(loaded);
        body["name"]!["given"] = "Jordy";
        body["provenance"]!["name.given"] = JsonNode.Parse(Typed);

        var saved = await _world.Put($"/v1/cases/{caseId}/debtors/debtor_1", body.ToJsonString());

        Assert.True(saved.Status == 200, saved.Body);
        Assert.Equal("""{"source":"staff_typed"}""", Http.FlaskJson.Serialize(saved.Object["provenance"]!["name.given"]));
        Assert.Equal($$"""{"client_id":"{{loaded.Str("client_id")}}","source":"client"}""", Http.FlaskJson.Serialize(saved.Object["provenance"]!["name.surname"]));
    }

    [Fact]
    public async Task Client_provenance_on_a_changed_or_never_copied_value_is_refused()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var loaded = (await DebtorsAsync(caseId))[0];

        var changed = Echo(loaded);
        changed["name"]!["surname"] = "Retyped";
        Assert.Equal(["provenance.name.surname"], (await _world.Put($"/v1/cases/{caseId}/debtors/debtor_1", changed.ToJsonString())).FieldErrors);

        var claimed = Echo(loaded);
        claimed["phone"] = "555-0100";
        claimed["provenance"]!["phone"] = new JsonObject { ["source"] = "client", ["client_id"] = loaded.Str("client_id") };
        Assert.Equal(["provenance.phone"], (await _world.Put($"/v1/cases/{caseId}/debtors/debtor_1", claimed.ToJsonString())).FieldErrors);
    }

    // ── Re-copy from, and copy to, the client ───────────────────────

    [Fact]
    public async Task Recopy_restores_the_clients_values_with_client_provenance_and_keeps_case_facts()
    {
        var (caseId, before) = await DivergedCaseAsync();

        var answer = await _world.Post($"/v1/cases/{caseId}/debtors/debtor_1/copy-from-client", null);

        Assert.Equal(200, answer.Status);
        var body = answer.Object;
        Assert.Equal(before.Str("id"), body.Str("id"));
        Assert.Equal("Jordan", body["name"]!["given"]!.GetValue<string>());
        Assert.Equal($$"""{"client_id":"{{before.Str("client_id")}}","source":"client"}""", Http.FlaskJson.Serialize(body["provenance"]!["name.given"]));
        Assert.Equal("[]", body["differs_from_client"]!.ToJsonString());
        Assert.Equal("""{"basis":"lived_longest_180_days"}""", Http.FlaskJson.Serialize(body["venue"]));
        Assert.Equal("""{"source":"staff_typed"}""", Http.FlaskJson.Serialize(body["provenance"]!["venue.basis"]));
    }

    [Fact]
    public async Task Recopy_empties_a_field_the_client_does_not_hold()
    {
        var (caseId, loaded) = await DivergedCaseAsync();
        var body = Echo(loaded);
        body["email"] = "typed@example.test";
        body["provenance"]!["email"] = JsonNode.Parse(Typed);
        Assert.Equal(200, (await _world.Put($"/v1/cases/{caseId}/debtors/debtor_1", body.ToJsonString())).Status);

        var answer = await _world.Post($"/v1/cases/{caseId}/debtors/debtor_1/copy-from-client", null);

        Assert.False(answer.Object.ContainsKey("email"));
        Assert.False(answer.Object["provenance"]!.AsObject().ContainsKey("email"));
    }

    [Fact]
    public async Task Recopy_is_refused_on_a_filed_case_and_without_a_linked_client()
    {
        var (caseId, _) = await DivergedCaseAsync();
        await _world.FileAsync(caseId);

        Assert.Equal(409, (await _world.Post($"/v1/cases/{caseId}/debtors/debtor_1/copy-from-client", null)).Status);
        Assert.Equal("Jordy", (await DebtorsAsync(caseId))[0]["name"]!["given"]!.GetValue<string>());

        var other = await _world.OpenCaseIdAsync();
        Assert.Equal(201, (await Put(other, role: "non_filing_spouse")).Status);
        Assert.Equal(409, (await _world.Post($"/v1/cases/{other}/debtors/non_filing_spouse/copy-from-client", null)).Status);
    }

    [Fact]
    public async Task Update_client_copies_the_cases_values_onto_the_client_even_when_filed()
    {
        var (caseId, before) = await DivergedCaseAsync();
        await _world.FileAsync(caseId);

        var answer = await _world.Post($"/v1/cases/{caseId}/debtors/debtor_1/copy-to-client", null);

        Assert.Equal(200, answer.Status);
        Assert.Equal("[]", answer.Object["differs_from_client"]!.ToJsonString());
        Assert.Equal(Http.FlaskJson.Serialize(before["name"]), Http.FlaskJson.Serialize(answer.Object["name"]));
        Assert.Equal(Http.FlaskJson.Serialize(before["provenance"]), Http.FlaskJson.Serialize(answer.Object["provenance"]));
        Assert.Equal((AccessAction.ClientUpdate, AccessOutcome.Allowed), (_world.Events[^1].Action, _world.Events[^1].Outcome));
        Assert.Equal("""{"given":"Jordy","surname":"Example"}""", Http.FlaskJson.Serialize((await _world.Get($"/v1/firm/clients/{before.Str("client_id")}")).Object["name"]));
    }

    [Fact]
    public async Task Update_client_needs_clients_add_edit()
    {
        var (caseId, _) = await DivergedCaseAsync();
        await RestrictAliceAsync(Feature.Clients, PermissionLevel.ViewOnly);

        Assert.Equal(403, (await _world.Post($"/v1/cases/{caseId}/debtors/debtor_1/copy-to-client", null)).Status);
    }

    private static JsonObject Echo(JsonObject debtor)
    {
        var body = debtor.DeepClone().AsObject();
        foreach (var key in new[] { "id", "case_id", "filing_role", "created_at", "updated_at", "client_id", "differs_from_client" })
        {
            body.Remove(key);
        }

        return body;
    }

    private async Task<(string CaseId, JsonObject Saved)> DivergedCaseAsync()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var body = Echo((await DebtorsAsync(caseId))[0]);
        body["name"]!["given"] = "Jordy";
        body["venue"] = new JsonObject { ["basis"] = "lived_longest_180_days" };
        body["provenance"]!["name.given"] = JsonNode.Parse(Typed);
        body["provenance"]!["venue.basis"] = JsonNode.Parse(Typed);
        var saved = await _world.Put($"/v1/cases/{caseId}/debtors/debtor_1", body.ToJsonString());
        Assert.Equal("""["name.given"]""", saved.Object["differs_from_client"]!.ToJsonString());
        return (caseId, saved.Object);
    }

    // Alice keeps the case through access_all_cases, loses the admin bypass.
    private Task RestrictAliceAsync(Feature feature, PermissionLevel level) =>
        _world.ReplaceAsync(FirmWorld.Member(Alice, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, feature, level)));

    private Task<Answer> Put(string caseId, string body = "", string role = "debtor_1", string subject = Alice) =>
        _world.Put($"/v1/cases/{caseId}/debtors/{role}", "{" + body + "}", subject);

    private Task<Answer> Link(string caseId, string role, string clientId) =>
        _world.Put($"/v1/cases/{caseId}/debtors/{role}/client", $$"""{"client_id": "{{clientId}}"}""");

    private async Task<List<JsonObject>> DebtorsAsync(string caseId) =>
        [.. (await _world.Get($"/v1/cases/{caseId}/debtors")).Object["debtors"]!.AsArray().Select(d => d!.AsObject())];

    private async Task<List<string?>> RolesAsync(string caseId) =>
        [.. (await DebtorsAsync(caseId)).Select(d => d.Str("filing_role"))];

    /// <summary>The memory debtor store, with one stale read on demand — the Python's monkeypatched <c>get</c>.</summary>
    private sealed class StaleOnce : IDebtorStore
    {
        public MemoryDebtorStore Inner { get; set; } = null!;

        public bool MissNextGet { get; set; }

        public Task<bool> CreateAsync(Debtor debtor, CancellationToken cancellationToken = default) => Inner.CreateAsync(debtor, cancellationToken);

        public Task PutAsync(Debtor debtor, CancellationToken cancellationToken = default) => Inner.PutAsync(debtor, cancellationToken);

        public Task<LinkOutcome> LinkAsync(Debtor debtor, bool create, string firmId, CancellationToken cancellationToken = default) => Inner.LinkAsync(debtor, create, firmId, cancellationToken);

        public Task<Debtor?> GetAsync(string caseId, FilingRole filingRole, CancellationToken cancellationToken = default)
        {
            if (MissNextGet)
            {
                MissNextGet = false;
                return Task.FromResult<Debtor?>(null);
            }

            return Inner.GetAsync(caseId, filingRole, cancellationToken);
        }

        public Task<IReadOnlyList<Debtor>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default) => Inner.ListForCaseAsync(caseId, cancellationToken);

        public Task<IReadOnlyList<(string CaseId, FilingRole FilingRole)>> RolesForClientAsync(string clientId, CancellationToken cancellationToken = default) => Inner.RolesForClientAsync(clientId, cancellationToken);

        public Task<RepointOutcome> RepointClientAsync(string caseId, FilingRole filingRole, string fromClientId, string toClientId, CancellationToken cancellationToken = default) => Inner.RepointClientAsync(caseId, filingRole, fromClientId, toClientId, cancellationToken);

        public Task<IReadOnlyList<DebtorClientLink>> ClientLinksForCaseAsync(string caseId, CancellationToken cancellationToken = default) => Inner.ClientLinksForCaseAsync(caseId, cancellationToken);
    }
}
