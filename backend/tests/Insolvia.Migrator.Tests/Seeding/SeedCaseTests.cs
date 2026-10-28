using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Claims;
using Insolvia.Core.Clients;
using Insolvia.Core.Creditors;
using Insolvia.Core.Debtors;
using Insolvia.Core.Documents;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Income;
using Insolvia.Core.Memory;
using Insolvia.Core.TaxIds;
using Insolvia.Migrator.Seeding;
using static Insolvia.Migrator.Tests.Seeding.SeedWorld;

namespace Insolvia.Migrator.Tests.Seeding;

// services/admin tests/unit/test_seed.py, the case half (cases, references,
// portal clients, firm clients, publish, the committed fixtures), one test
// per Python test in intent. The capture tests are not here: `capture` is not
// ported (the switch PR's list). A CASE IS DERIVED, NOT MINTED, and the bytes
// go through the same confirm step the API's complete route uses.
public sealed class SeedCaseTests : IDisposable
{
    private static readonly IEntityKind Creditors = CaseCollections.Find("creditors")!;
    private readonly SeedWorld _world = new();

    public void Dispose() => _world.Dispose();

    private const string CasesJson = "fixtures/v1/cases.json";

    [Fact]
    public async Task A_fixture_case_lands_with_its_debtor_items_and_documents()
    {
        Assert.Equal(0, await _world.LoadAsync());

        var found = (await _world.TheCaseAsync())!;
        Assert.Equal(7, found.Chapter);

        // A registry reference: the fixture names the pair, the API's own
        // parser derives the printed district from it.
        Assert.Equal(("flmb", "tampa"), (found.Court, found.Division));
        Assert.Equal("Middle District of Florida", found.District);
        Assert.Equal(Core.Cases.CaseStatus.Intake, found.Status);

        var debtor = (await _world.Debtors.GetAsync(found.Id, FilingRole.Debtor1, Ct))!;
        Assert.Equal("Sample", debtor.Body.Name.Given);

        // SEALED the way the API seals one: the record carries the last four
        // and a reference; the digits only open through the logged read.
        Assert.Equal("4321", debtor.TaxId!.LastFour);
        Assert.DoesNotContain(_world.TaxIds.Items.Values, sealedId => sealedId.ToString().Contains("987654321", StringComparison.Ordinal));
        var log = new MemoryAccessLog();
        Assert.Equal("987654321", await TaxIdRules.ReadAsync(debtor.TaxId, found.FirmId, found.Id, FilingRole.Debtor1, "test", "test", _world.Cipher, _world.TaxIds, log, Ct));

        var creditors = await _world.Entities.ListForCaseAsync(found.Id, Creditors, Ct);
        Assert.Equal(["Example Bank"], creditors.Select(creditor => ((CreditorBody)creditor.Body).Name));

        var document = Assert.Single(await _world.Documents.ListForCaseAsync(found.Id, Ct));
        Assert.Equal(DocumentStatus.Stored, document.Status);
        Assert.Equal(Pdf.Length, document.ByteSize);
        Assert.Equal(Pdf, await _world.Blobs.GetBytesAsync(document.StorageRef, Ct));
        Assert.Equal($"cases/{found.Id}/{document.Id}", document.StorageRef);
    }

    [Fact]
    public async Task The_opener_is_assigned_so_the_case_is_reachable()
    {
        Assert.Equal(0, await _world.LoadAsync());
        var found = (await _world.TheCaseAsync())!;

        Assert.Equal([found.CreatedBy], (await _world.Cases.AssigneesAsync(found.Id, Ct)).Select(assignment => assignment.Subject));
    }

    [Fact]
    public async Task Loading_twice_writes_nothing_new()
    {
        Assert.Equal(0, await _world.LoadAsync());
        var first = (await _world.TheCaseAsync())!;
        var copied = _world.Objects.Copied.ToList();

        Assert.Equal(0, await _world.LoadAsync());

        Assert.Equal(first, await _world.TheCaseAsync());
        Assert.Equal(copied, _world.Objects.Copied);
        Assert.Single(await _world.Documents.ListForCaseAsync(first.Id, Ct));
        Assert.Single(await _world.Entities.ListForCaseAsync(first.Id, Creditors, Ct));
        Assert.Single(_world.Cases.Cases);
        Assert.Equal(
            ["firm 'Example & Partners': already seeded", "  case 'sample': present"],
            _world.Output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(0, await _world.LoadAsync(check: true));
    }

    [Fact]
    public void The_same_fixture_seeds_different_ids_into_different_targets()
    {
        // A row copied between environments by hand would otherwise look like the same case.
        Assert.NotEqual(
            SeedIds.Derive(SeedTarget.For("local").CaseTarget, "f", "v1", "sample"),
            SeedIds.Derive(SeedTarget.For("staging").CaseTarget, "f", "v1", "sample"));
    }

    [Fact]
    public async Task Check_reports_the_missing_case_and_writes_nothing()
    {
        _world.Accounts.Subjects["e2e-admin@insolvia.test"] = FakeAccounts.Alice;

        Assert.Equal(1, await _world.LoadAsync(check: true));

        Assert.Empty(_world.Cases.Cases);
        Assert.Empty(_world.Objects.Copied);
        Assert.Contains("  case 'sample': its firm is not seeded yet", _world.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_names_each_missing_part_of_a_seeded_case()
    {
        // A case row can survive while its children do not. Counting is not
        // diagnosing: every missing thing is named where it is counted.
        Assert.Equal(0, await _world.LoadAsync());
        _world.Stores = _world.Stores with { Debtors = new MemoryDebtorStore(), Entities = new MemoryCaseEntityStore(), Documents = new MemoryDocumentStore() };

        Assert.Equal(1, await _world.LoadAsync(check: true));

        var printed = _world.Output.ToString();
        Assert.Contains("case 'sample': present", printed, StringComparison.Ordinal);
        Assert.Contains("debtor debtor_1: missing", printed, StringComparison.Ordinal);
        Assert.Contains("creditors[0]: missing", printed, StringComparison.Ordinal);
        Assert.Contains("document stub.pdf: missing", printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_names_a_document_whose_bytes_never_landed()
    {
        // pending is not absent: an interrupted load, fixed differently.
        Assert.Equal(0, await _world.LoadAsync());
        var found = (await _world.TheCaseAsync())!;
        var document = Assert.Single(await _world.Documents.ListForCaseAsync(found.Id, Ct));
        Assert.NotNull(await _world.Documents.UpdateAsync(document with { Status = DocumentStatus.Pending }, Ct));

        Assert.Equal(1, await _world.LoadAsync(check: true));

        Assert.Contains("document stub.pdf: not stored", _world.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_left_pending_is_finished_by_the_next_load()
    {
        Assert.Equal(0, await _world.LoadAsync());
        var found = (await _world.TheCaseAsync())!;
        var document = Assert.Single(await _world.Documents.ListForCaseAsync(found.Id, Ct));
        await _world.Documents.UpdateAsync(document with { Status = DocumentStatus.Pending }, Ct);

        Assert.Equal(0, await _world.LoadAsync());

        Assert.Equal(DocumentStatus.Stored, Assert.Single(await _world.Documents.ListForCaseAsync(found.Id, Ct)).Status);
    }

    [Fact]
    public async Task Check_names_a_case_row_that_is_missing_entirely()
    {
        Assert.Equal(0, await _world.LoadAsync());
        var debtors = new MemoryDebtorStore(_world.Firms);
        _world.Stores = _world.Stores with { Cases = new MemoryCaseStore(debtors), Debtors = debtors };

        Assert.Equal(1, await _world.LoadAsync(check: true));

        Assert.Contains("  case 'sample': missing", _world.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copy_that_did_not_land_is_refused_before_the_row()
    {
        _world.Objects.Objects.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _world.LoadAsync());

        var found = (await _world.TheCaseAsync())!;
        Assert.Empty(await _world.Documents.ListForCaseAsync(found.Id, Ct));
    }

    [Fact]
    public async Task A_manifest_size_mismatch_is_refused()
    {
        _world.Objects.Objects["v1/objects/stub.pdf"] = [.. Pdf, .. "extra"u8.ToArray()];

        Assert.Equal(2, await _world.LoadAsync());

        var found = (await _world.TheCaseAsync())!;
        Assert.Empty(await _world.Documents.ListForCaseAsync(found.Id, Ct));
    }

    [Fact]
    public async Task A_document_object_missing_from_the_manifest_is_refused()
    {
        _world.Edit(CasesJson, body => body["cases"]![0]!["documents"]![0]!["object"] = "objects/absent.pdf");

        Assert.Equal(2, await _world.LoadAsync());
    }

    [Fact]
    public async Task A_case_needs_its_firm_seeded_first()
    {
        _world.Edit("env.json", body => body["cases"]![0]!["firm"] = "Nobody LLP");

        Assert.Equal(2, await _world.LoadAsync());
    }

    [Fact]
    public async Task A_case_naming_an_unknown_version_or_handle_is_refused()
    {
        _world.Edit("env.json", body => body["cases"]![0]!["case"] = "nothing");
        Assert.Equal(2, await _world.LoadAsync());

        _world.Edit("env.json", body => body["cases"]![0]!["fixture"] = "latest");
        Assert.Equal(2, await _world.LoadAsync());
        Assert.Contains("must look like v1", _world.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_fixture_row_fails_with_the_apis_own_error()
    {
        // The parser is the route's: a populated field without provenance is
        // the same 400-shaped refusal the API gives.
        _world.Edit(CasesJson, body => body["cases"]![0]!["debtors"]!["debtor_1"]!["provenance"] = new JsonObject());

        var refusal = await Assert.ThrowsAnyAsync<ValidationException>(() => _world.LoadAsync());

        Assert.Contains("provenance", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(_world.Cases.Cases);
    }

    // ── publish ─────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_uploads_only_what_the_bucket_lacks()
    {
        _world.Objects.Objects.Clear();
        var folder = Path.Combine(_world.Root, "fixtures", "v1");

        Assert.Equal(1, await SeedPublisher.PublishAsync(folder, _world.Objects, check: true, TextWriter.Null, Ct));
        Assert.Empty(_world.Objects.Objects);

        Assert.Equal(1, await SeedPublisher.PublishAsync(folder, _world.Objects, check: false, TextWriter.Null, Ct));
        Assert.Equal(Pdf, _world.Objects.Objects["v1/objects/stub.pdf"]);
        Assert.Contains("v1/manifest.json", _world.Objects.Objects.Keys);
        Assert.Equal(0, await SeedPublisher.PublishAsync(folder, _world.Objects, check: true, TextWriter.Null, Ct));
    }

    [Fact]
    public async Task Publish_refuses_a_file_that_disagrees_with_its_manifest()
    {
        File.WriteAllText(Path.Combine(_world.Root, "fixtures", "v1", "objects", "stub.pdf"), "not what the manifest says");

        await Assert.ThrowsAsync<SeedRefusedException>(() => SeedPublisher.PublishAsync(Path.Combine(_world.Root, "fixtures", "v1"), _world.Objects, check: false, TextWriter.Null, Ct));
    }

    [Theory]
    [InlineData("insolvia-staging-case-documents-us-east-1")]
    [InlineData("insolvia-shared-dev-fixtures")]
    [InlineData(null)]
    public void Publish_refuses_a_bucket_outside_the_fixture_family(string? bucket)
    {
        Assert.Throws<SeedRefusedException>(() => SeedPublisher.RequireFixtureBucket(bucket));
        SeedPublisher.RequireFixtureBucket("insolvia-shared-dev-fixtures-us-east-1");
    }

    // ── portal clients (ADR 0023) ───────────────────────────────────

    private static JsonObject Client(JsonObject? overrides = null)
    {
        var client = new JsonObject
        {
            ["handle"] = "client",
            ["email"] = "e2e-client@insolvia.test",
            ["password"] = "hunter2ABCDEF",
            ["displayName"] = "Sample Debtor",
            ["roles"] = new JsonArray("debtor_1"),
        };
        foreach (var (key, value) in overrides ?? [])
        {
            client[key] = value?.DeepClone();
        }

        return client;
    }

    private void WithClients(params JsonObject[] clients) =>
        _world.Edit("env.json", body => body["cases"]![0]!["clients"] = new JsonArray([.. clients]));

    [Fact]
    public async Task A_fixture_client_is_bound_to_the_fixture_case()
    {
        WithClients(Client());

        Assert.Equal(0, await _world.LoadAsync());

        var found = (await _world.TheCaseAsync())!;
        var subject = _world.Accounts.Subjects["e2e-client@insolvia.test"];
        var binding = (await _world.Bindings.GetAsync(found.FirmId, subject, Ct))!;
        Assert.Equal(found.Id, binding.CaseId);
        Assert.Equal([ClientRole.Debtor1], binding.Roles);
        Assert.Equal(BindingStatus.Invited, binding.Status);

        // The password is SET: the integration tier signs the client in with
        // no invitation mail in between. And a client is not a firm user.
        Assert.Equal("hunter2ABCDEF", _world.Accounts.Passwords["e2e-client@insolvia.test"]);
        Assert.Null(await _world.Firms.FindUserAsync(subject, Ct));
    }

    [Fact]
    public async Task A_loaded_client_is_left_alone_and_check_is_clean()
    {
        WithClients(Client());
        Assert.Equal(0, await _world.LoadAsync());

        Assert.Equal(0, await _world.LoadAsync(check: true));
        Assert.Equal(0, await _world.LoadAsync());
        Assert.Contains("client 'client': present (invited)", _world.Output.ToString(), StringComparison.Ordinal);
        Assert.Single(await _world.Bindings.ListForCaseAsync((await _world.TheCaseAsync())!.Id, Ct));
    }

    [Fact]
    public async Task Check_reports_a_missing_client_without_binding_it()
    {
        Assert.Equal(0, await _world.LoadAsync());
        WithClients(Client());

        Assert.Equal(1, await _world.LoadAsync(check: true));
        Assert.Empty(await _world.Bindings.ListForCaseAsync((await _world.TheCaseAsync())!.Id, Ct));
    }

    [Fact]
    public async Task A_client_who_is_also_a_firm_person_is_refused()
    {
        WithClients(Client(new JsonObject { ["email"] = "e2e-admin@insolvia.test" }));

        Assert.Equal(2, await _world.LoadAsync());
    }

    [Fact]
    public async Task Two_fixture_clients_cannot_both_hold_debtor_2()
    {
        WithClients(
            Client(new JsonObject { ["roles"] = new JsonArray("debtor_2") }),
            Client(new JsonObject { ["handle"] = "other", ["email"] = "e2e-other@insolvia.test", ["roles"] = new JsonArray("debtor_2") }));

        Assert.Equal(2, await _world.LoadAsync());
    }

    // ── references between a case's records ─────────────────────────

    private void WithReferences() => _world.Edit(CasesJson, body =>
    {
        var collections = body["cases"]![0]!["collections"]!;
        collections["creditors"]![0]!["$handle"] = "bank";
        collections["claims"] = new JsonArray(new JsonObject
        {
            ["creditor_id"] = new JsonObject { ["$ref"] = "creditors/bank" },
            ["claim_class"] = "secured",
            ["provenance"] = new JsonObject { ["creditor_id"] = Typed.DeepClone(), ["claim_class"] = Typed.DeepClone() },
        });
        collections["income_summaries"] = new JsonArray(new JsonObject
        {
            ["debtor_id"] = new JsonObject { ["$ref"] = "debtors/debtor_1" },
            ["wages"] = "100.00",
            ["provenance"] = new JsonObject { ["debtor_id"] = Typed.DeepClone(), ["wages"] = Typed.DeepClone() },
        });
    });

    [Fact]
    public async Task A_reference_lands_as_the_id_this_target_gave_the_record()
    {
        WithReferences();

        Assert.Equal(0, await _world.LoadAsync());

        var found = (await _world.TheCaseAsync())!;
        var creditor = Assert.Single(await _world.Entities.ListForCaseAsync(found.Id, Creditors, Ct));
        var claim = Assert.Single(await _world.Entities.ListForCaseAsync(found.Id, CaseCollections.Find("claims")!, Ct));
        var summary = Assert.Single(await _world.Entities.ListForCaseAsync(found.Id, CaseCollections.Find("income_summaries")!, Ct));
        var debtor = (await _world.Debtors.GetAsync(found.Id, FilingRole.Debtor1, Ct))!;
        Assert.Equal(creditor.Id, ((ClaimBody)claim.Body).CreditorId);
        Assert.Equal(debtor.Id, ((IncomeSummaryBody)summary.Body).DebtorId);
        Assert.Equal(SeedIds.Derive(found.Id, "debtor", "debtor_1"), debtor.Id);
    }

    public static TheoryData<string> BrokenReferences() =>
    [
        """{"$ref": "creditors/nobody"}""",
        """{"$ref": "debtors/debtor_2"}""",
        """{"$ref": "creditors"}""",
        """{"$ref": "claims/bank"}""",
        """{"$ref": "creditors/bank", "extra": "key"}""",
    ];

    [Theory]
    [MemberData(nameof(BrokenReferences))]
    public async Task A_reference_to_nothing_is_refused_before_anything_is_written(string reference)
    {
        WithReferences();
        _world.Edit(CasesJson, body => body["cases"]![0]!["collections"]!["claims"]![0]!["creditor_id"] = JsonNode.Parse(reference));

        Assert.Equal(2, await _world.LoadAsync());
        Assert.Empty(_world.Cases.Cases);
    }

    [Fact]
    public async Task Two_items_with_one_handle_are_refused()
    {
        WithReferences();
        _world.Edit(CasesJson, body =>
        {
            var creditors = (JsonArray)body["cases"]![0]!["collections"]!["creditors"]!;
            creditors.Add(creditors[0]!.DeepClone());
        });

        Assert.Equal(2, await _world.LoadAsync());
        Assert.Empty(_world.Cases.Cases);
    }

    // ── firm clients (ADR 0022) ─────────────────────────────────────

    [Fact]
    public async Task A_seeded_debtor_is_a_copy_of_its_firm_client()
    {
        Assert.Equal(0, await _world.LoadAsync());
        var found = (await _world.TheCaseAsync())!;
        var debtor = (await _world.Debtors.GetAsync(found.Id, FilingRole.Debtor1, Ct))!;

        var expected = SeedIds.Derive(SeedTarget.For("local").FirmTarget, found.FirmId, "v1", "clients", "sample-client");
        Assert.Equal(expected, debtor.ClientId);
        Assert.Equal(found.CreatedAt, debtor.CaseCreatedAt);
        var client = (await _world.Firms.GetClientAsync(found.FirmId, expected, Ct))!;
        Assert.Equal("Sample", client.Name.Given);
        Assert.Empty(ClientCopies.DiffersFromClient(debtor, client));
    }

    [Fact]
    public async Task A_debtor_without_a_client_refuses_the_whole_load()
    {
        // v1 to v3 are pre-client: their rows are what the purge deletes.
        _world.Edit(CasesJson, body => ((JsonObject)body["cases"]![0]!["debtors"]!["debtor_1"]!).Remove("client_id"));

        Assert.Equal(2, await _world.LoadAsync());
        Assert.Empty(_world.Cases.Cases);
        Assert.Empty(_world.Debtors.Debtors);
    }

    [Fact]
    public async Task A_client_ref_to_no_fixture_client_is_refused()
    {
        _world.Edit(CasesJson, body => body["cases"]![0]!["debtors"]!["debtor_1"]!["client_id"] = new JsonObject { ["$ref"] = "clients/nobody" });

        Assert.Equal(2, await _world.LoadAsync());
        Assert.Empty(_world.Cases.Cases);
    }

    [Fact]
    public async Task Check_names_a_missing_client()
    {
        Assert.Equal(0, await _world.LoadAsync());

        // The same firms and people, without the directory entry.
        var firms = new MemoryFirmStore();
        foreach (var firm in _world.Firms.Firms.Values)
        {
            await firms.CreateFirmAsync(firm, Ct);
        }

        foreach (var user in _world.Firms.Users.Values)
        {
            await firms.AddUserAsync(user, Ct);
        }

        _world.Stores = _world.Stores with { Firms = firms };

        Assert.Equal(1, await _world.LoadAsync(check: true));
        Assert.Contains("client 'sample-client': missing", _world.Output.ToString(), StringComparison.Ordinal);
    }

    // ── the committed fixtures ──────────────────────────────────────

    [Theory]
    [InlineData("dev", "local")]
    [InlineData("staging", "staging")]
    public async Task The_committed_environment_fixture_loads_and_converges(string file, string environment)
    {
        // seeds/<env>.json and every version it names, through the loader
        // into the memory twins: every reference resolves, every body passes
        // the API's parsers, and a second load finds nothing missing.
        var seeds = Repository.Seeds;
        var body = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(seeds, $"{file}.json")))!;
        var people = SeedJson(body);
        _world.Accounts = new FakeAccounts(people.Select((email, n) => (email, n)).ToDictionary(pair => pair.email, pair => $"00000000-0000-4000-8000-{pair.n:D12}"));
        foreach (var folder in Directory.GetDirectories(Path.Combine(seeds, "fixtures")))
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")))!;
            foreach (var (key, _) in (JsonObject)manifest["objects"]!)
            {
                _world.Objects.Objects[$"{Path.GetFileName(folder)}/{key}"] = File.ReadAllBytes(Path.Combine(folder, key));
            }
        }

        // Staging's one secret; never a real value here.
        string? Variables(string name) => name == "E2E_TEST_USER_PASSWORD" ? "Example-Passw0rd-not-real" : null;
        var fixture = Path.Combine(seeds, $"{file}.json");

        Assert.Equal(0, await _world.LoadAsync(environment: environment, fixture: fixture, variables: Variables));
        Assert.Equal(0, await _world.LoadAsync(check: true, environment: environment, fixture: fixture, variables: Variables));
        Assert.Equal(((JsonArray)body["cases"]!).Count, _world.Cases.Cases.Count);

        // ADR 0022: every Debtor 1 and Debtor 2 is a copy of a firm client,
        // the copy agrees with the client, and the case is in its list.
        foreach (var debtor in _world.Debtors.Debtors.Where(debtor => debtor.FilingRole is FilingRole.Debtor1 or FilingRole.Debtor2))
        {
            Assert.NotNull(debtor.ClientId);
            var found = _world.Cases.Cases[debtor.CaseId];
            var client = (await _world.Firms.GetClientAsync(found.FirmId, debtor.ClientId, Ct))!;
            Assert.Empty(ClientCopies.DiffersFromClient(debtor, client));
            var admin = (await _world.Firms.ListUsersAsync(found.FirmId, Ct)).First(user => user.IsAdmin);
            var listed = await _world.Cases.ListForClientAsync(client.Id, new Accessor((await _world.Firms.GetFirmAsync(found.FirmId, Ct))!, admin), Ct);
            Assert.Equal([(found.Id, debtor.FilingRole)], listed.Select(entry => (entry.Case.Id, entry.FilingRole)));
        }

        // Nothing the purge would delete: the fixture is post-client by design.
        Assert.All(_world.Cases.Cases.Keys, caseId => Assert.Contains(_world.Debtors.Debtors, debtor => debtor.CaseId == caseId && debtor.ClientId is not null));
    }

    private static List<string> SeedJson(JsonObject body) =>
    [
        .. ((JsonArray)body["firms"]!).SelectMany(firm => (JsonArray)firm!["users"]!).Select(user => user!["email"]!.GetValue<string>()),
        .. ((JsonArray)body["cases"]!).SelectMany(entry => entry!["clients"] as JsonArray ?? []).Select(client => client!["email"]!.GetValue<string>()),
    ];
}

/// <summary>Where the repository's committed seeds are, from the test binary.</summary>
internal static class Repository
{
    public static string Seeds
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "seeds");
                if (File.Exists(Path.Combine(candidate, "dev.json")))
                {
                    return candidate;
                }
            }

            throw new DirectoryNotFoundException("no seeds/dev.json above the test binary");
        }
    }
}
