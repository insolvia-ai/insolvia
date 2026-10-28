using Insolvia.Core.Clients;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.LibraryCreditors;
using Insolvia.Core.Memory;
using Insolvia.Core.Questionnaires;

namespace Insolvia.Core.Tests;

// Translated from the MEMORY half of test_firm_store.py, plus the memory-store
// tests of test_client_funnel.py, test_questionnaire's store tests (in
// test_firm_store.py) and test_document_requests.py's firm checklist. The
// memory store is held to the conditions the real store enforces; the
// DynamoDB half of each file tested the attribute conversion, which has no
// successor (18.10 brings the Postgres store and its own data-tier tests).
public sealed class MemoryFirmStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Firm Firm(string firmId = Ids.Firm, string name = "Example & Partners") =>
        new(firmId, name, FirmStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static FirmUser User(string subject = Ids.Alice, string firmId = Ids.Firm, string firstName = "Alice", string lastName = "Attorney") =>
        FirmsTests.User(subject: subject, firmId: firmId, firstName: firstName, lastName: lastName);

    private static LibraryCreditor Creditor(string firmId = Ids.Firm, string name = "Acme Collections") =>
        LibraryCreditorRules.Create(LibraryCreditorRules.Parse(Body.Of($$"""{"name": "{{name}}", "preferred": true}""")), firmId);

    private static FirmClient Client(string firmId = Ids.Firm, string surname = "Example") =>
        FirmClientTests.Make($$$"""{"name": {"given": "Jordan", "surname": "{{{surname}}}"}}""", firmId);

    // ── Firm users ──────────────────────────────────────────────────

    [Fact]
    public async Task Adding_the_same_person_twice_is_refused()
    {
        var store = new MemoryFirmStore();
        await store.AddUserAsync(User(), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddUserAsync(User(lastName: "Impostor"), Ct));
        Assert.Equal("Attorney", (await store.GetUserAsync(Ids.Firm, Ids.Alice, Ct))!.LastName);
    }

    [Fact]
    public async Task A_user_is_read_within_their_firm()
    {
        var store = new MemoryFirmStore();
        await store.AddUserAsync(User(), Ct);
        Assert.NotNull(await store.GetUserAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Null(await store.GetUserAsync(Ids.OtherFirm, Ids.Alice, Ct));
    }

    [Fact]
    public async Task Finding_a_subject_in_two_firms_raises_rather_than_guessing()
    {
        var store = new MemoryFirmStore();
        await store.AddUserAsync(User(), Ct);
        await store.AddUserAsync(User(firmId: Ids.OtherFirm), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindUserAsync(Ids.Alice, Ct));
    }

    [Fact]
    public async Task Finding_an_unprovisioned_subject_is_none_not_an_error() =>
        Assert.Null(await new MemoryFirmStore().FindUserAsync(Ids.Alice, Ct));

    [Fact]
    public async Task A_staff_list_is_one_firm_ordered_by_surname()
    {
        var store = new MemoryFirmStore();
        await store.AddUserAsync(User(subject: Ids.Bob, firstName: "Adam", lastName: "Zimmerman"), Ct);
        await store.AddUserAsync(User(firstName: "Zoe", lastName: "Adams"), Ct);
        await store.AddUserAsync(User(subject: Ids.Bob, firmId: Ids.OtherFirm, firstName: "Aaron", lastName: "Other"), Ct);
        Assert.Equal([("Zoe", "Adams"), ("Adam", "Zimmerman")], (await store.ListUsersAsync(Ids.Firm, Ct)).Select(p => (p.FirstName, p.LastName)));
    }

    [Fact]
    public async Task Updating_someone_who_is_not_there_is_none()
    {
        var store = new MemoryFirmStore();
        Assert.Null(await store.UpdateUserAsync(User(), Ct));
        Assert.Empty(store.Users);
    }

    [Fact]
    public async Task Updating_across_firms_does_not_move_someone()
    {
        var store = new MemoryFirmStore();
        await store.AddUserAsync(User(), Ct);
        Assert.Null(await store.UpdateUserAsync(User() with { FirmId = Ids.OtherFirm }, Ct));
        Assert.Null(await store.GetUserAsync(Ids.OtherFirm, Ids.Alice, Ct));
    }

    [Fact]
    public async Task Removing_someone_twice_reports_the_truth()
    {
        var store = new MemoryFirmStore();
        await store.AddUserAsync(User(), Ct);
        Assert.True(await store.RemoveUserAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.False(await store.RemoveUserAsync(Ids.Firm, Ids.Alice, Ct));
    }

    // ── Firms cross-tenant (#212) ───────────────────────────────────

    [Fact]
    public async Task The_memory_firm_list_is_ordered_by_name_then_id()
    {
        var store = new MemoryFirmStore();
        await store.CreateFirmAsync(Firm(Ids.OtherFirm, "Other Firm LLP"), Ct);
        await store.CreateFirmAsync(Firm(Ids.Firm, "Example & Partners"), Ct);
        Assert.Equal(["Example & Partners", "Other Firm LLP"], (await store.ListFirmsAsync(Ct)).Select(firm => firm.Name));
    }

    [Fact]
    public async Task Updating_a_firm_that_is_not_there_is_none()
    {
        var store = new MemoryFirmStore();
        Assert.Null(await store.UpdateFirmAsync(Firm(), Ct));
        Assert.Empty(store.Firms);
    }

    [Fact]
    public async Task Updating_a_firm_writes_it_back()
    {
        var store = new MemoryFirmStore();
        await store.CreateFirmAsync(Firm(), Ct);
        var suspended = FirmLifecycle.SetFirmStatus(Firm(), FirmStatus.Suspended);
        Assert.Equal(suspended, await store.UpdateFirmAsync(suspended, Ct));
        Assert.Equal(FirmStatus.Suspended, (await store.GetFirmAsync(Ids.Firm, Ct))!.Status);
    }

    [Fact]
    public async Task Creating_the_same_firm_twice_is_refused()
    {
        var store = new MemoryFirmStore();
        await store.CreateFirmAsync(Firm(), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateFirmAsync(Firm(name: "Impostor"), Ct));
    }

    // ── Library creditors ───────────────────────────────────────────

    [Fact]
    public async Task Creating_the_same_library_creditor_twice_is_refused()
    {
        var store = new MemoryFirmStore();
        var creditor = Creditor();
        await store.CreateLibraryCreditorAsync(creditor, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateLibraryCreditorAsync(creditor, Ct));
    }

    [Fact]
    public async Task A_library_creditor_is_read_within_its_firm()
    {
        var store = new MemoryFirmStore();
        var creditor = Creditor();
        await store.CreateLibraryCreditorAsync(creditor, Ct);
        Assert.NotNull(await store.GetLibraryCreditorAsync(Ids.Firm, creditor.Id, Ct));
        Assert.Null(await store.GetLibraryCreditorAsync(Ids.OtherFirm, creditor.Id, Ct));
    }

    [Fact]
    public async Task The_library_is_listed_by_name_within_one_firm()
    {
        var store = new MemoryFirmStore();
        await store.CreateLibraryCreditorAsync(Creditor(name: "Zeta Bank"), Ct);
        await store.CreateLibraryCreditorAsync(Creditor(name: "Acme Collections"), Ct);
        await store.CreateLibraryCreditorAsync(Creditor(Ids.OtherFirm, "Other Firm's Bank"), Ct);
        Assert.Equal(["Acme Collections", "Zeta Bank"], (await store.ListLibraryCreditorsAsync(Ids.Firm, Ct)).Select(c => c.Name));
    }

    [Fact]
    public async Task Updating_a_library_creditor_that_is_not_there_is_none() =>
        Assert.Null(await new MemoryFirmStore().UpdateLibraryCreditorAsync(Creditor(), Ct));

    [Fact]
    public async Task Updating_across_firms_does_not_move_a_library_creditor()
    {
        var store = new MemoryFirmStore();
        var creditor = Creditor();
        await store.CreateLibraryCreditorAsync(creditor, Ct);
        Assert.Null(await store.UpdateLibraryCreditorAsync(creditor with { FirmId = Ids.OtherFirm }, Ct));
        Assert.Null(await store.GetLibraryCreditorAsync(Ids.OtherFirm, creditor.Id, Ct));
    }

    [Fact]
    public async Task Removing_a_library_creditor_twice_reports_the_truth()
    {
        var store = new MemoryFirmStore();
        var creditor = Creditor();
        await store.CreateLibraryCreditorAsync(creditor, Ct);
        Assert.True(await store.DeleteLibraryCreditorAsync(Ids.Firm, creditor.Id, Ct));
        Assert.False(await store.DeleteLibraryCreditorAsync(Ids.Firm, creditor.Id, Ct));
    }

    // ── Firm clients ────────────────────────────────────────────────

    [Fact]
    public async Task Creating_the_same_client_twice_is_refused()
    {
        var store = new MemoryFirmStore();
        var client = Client();
        await store.CreateClientAsync(client, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateClientAsync(client, Ct));
    }

    [Fact]
    public async Task A_client_is_read_within_its_firm()
    {
        var store = new MemoryFirmStore();
        var client = Client();
        await store.CreateClientAsync(client, Ct);
        Assert.Equal(client, await store.GetClientAsync(Ids.Firm, client.Id, Ct));
        Assert.Null(await store.GetClientAsync(Ids.OtherFirm, client.Id, Ct));
    }

    [Fact]
    public async Task The_directory_is_one_firm_in_directory_order()
    {
        var store = new MemoryFirmStore();
        await store.CreateClientAsync(Client(surname: "Zeta"), Ct);
        await store.CreateClientAsync(Client(surname: "adams"), Ct);
        await store.CreateClientAsync(Client(Ids.OtherFirm, "Other"), Ct);
        await store.CreateClientAsync(FirmClientRules.SetStatus(Client(surname: "Middle"), FirmClientStatus.Archived), Ct);

        // Archived clients are listed too: the directory is the whole directory.
        Assert.Equal(["adams", "Middle", "Zeta"], (await store.ListClientsAsync(Ids.Firm, Ct)).Select(c => c.Name.Surname));
    }

    [Fact]
    public async Task Updating_a_client_that_is_not_there_is_none() =>
        Assert.Null(await new MemoryFirmStore().UpdateClientAsync(Client(), Ct));

    [Fact]
    public async Task Updating_across_firms_does_not_move_a_client()
    {
        var store = new MemoryFirmStore();
        var client = Client();
        await store.CreateClientAsync(client, Ct);
        Assert.Null(await store.UpdateClientAsync(client with { FirmId = Ids.OtherFirm }, Ct));
        Assert.Null(await store.GetClientAsync(Ids.OtherFirm, client.Id, Ct));
    }

    [Fact]
    public async Task A_client_update_never_writes_the_merge_attributes()
    {
        var store = new MemoryFirmStore();
        var client = Client();
        await store.CreateClientAsync(client, Ct);
        var written = await store.UpdateClientAsync(client with { MergingInto = "x", MergingFrom = "y", ProspectStage = ProspectStage.Possible, Phone = "555-0199" }, Ct);
        Assert.NotNull(written);
        Assert.Equal(("555-0199", (string?)null, (string?)null, (ProspectStage?)null), (written.Phone, written.MergingInto, written.MergingFrom, written.ProspectStage));
    }

    // ── The prospect funnel (test_client_funnel.py) ─────────────────

    private static FirmClient AClient(string? retained = null) =>
        FirmClientTests.Make(retained is null ? """{"name": {"surname": "Example"}}""" : $$"""{"name": {"surname": "Example"}, "first_retained_at": "{{retained}}"}""");

    [Fact]
    public async Task A_prospects_stage_is_set_and_cleared()
    {
        var store = new MemoryFirmStore();
        var client = AClient();
        await store.CreateClientAsync(client, Ct);
        var staged = await store.SetClientProspectStageAsync(Ids.Firm, client.Id, ProspectStage.Possible, Ct);
        var cleared = await store.SetClientProspectStageAsync(Ids.Firm, client.Id, null, Ct);
        Assert.Equal(ProspectStage.Possible, staged!.ProspectStage);
        Assert.Null(cleared!.ProspectStage);
    }

    [Fact]
    public async Task A_retained_client_takes_no_stage()
    {
        var store = new MemoryFirmStore();
        var client = AClient("2026-07-20");
        await store.CreateClientAsync(client, Ct);
        Assert.Null(await store.SetClientProspectStageAsync(Ids.Firm, client.Id, ProspectStage.Possible, Ct));
    }

    [Fact]
    public async Task The_retained_transition_stamps_and_clears_the_stage()
    {
        var store = new MemoryFirmStore();
        var client = AClient() with { ProspectStage = ProspectStage.AwaitingSignedAgreement };
        await store.CreateClientAsync(client, Ct);
        var retained = await store.MarkClientRetainedAsync(Ids.Firm, client.Id, new DateOnly(2026, 10, 1), Ct);
        Assert.Equal(new DateOnly(2026, 10, 1), retained!.FirstRetainedAt);
        Assert.Null(retained.ProspectStage);
    }

    [Fact]
    public async Task The_retained_transition_keeps_an_earlier_date()
    {
        var store = new MemoryFirmStore();
        var client = AClient("2020-01-02");
        await store.CreateClientAsync(client, Ct);
        var retained = await store.MarkClientRetainedAsync(Ids.Firm, client.Id, new DateOnly(2026, 10, 1), Ct);
        Assert.Equal(new DateOnly(2020, 1, 2), retained!.FirstRetainedAt);
    }

    [Fact]
    public async Task A_whole_record_save_leaves_the_stage_as_stored()
    {
        var store = new MemoryFirmStore();
        var client = AClient();
        await store.CreateClientAsync(client, Ct);
        await store.SetClientProspectStageAsync(Ids.Firm, client.Id, ProspectStage.Possible, Ct);
        await store.UpdateClientAsync(client with { LeadSource = "Referral" }, Ct);
        Assert.Equal(ProspectStage.Possible, (await store.GetClientAsync(Ids.Firm, client.Id, Ct))!.ProspectStage);
    }

    // ── The questionnaire config ────────────────────────────────────

    private static QuestionnaireConfig Questionnaire(string firmId = Ids.Firm, bool other = false)
    {
        var sections = string.Join(", ", Questionnaires.Questionnaire.SectionIds.Select(id =>
            $$"""{"id": "{{id}}", "enabled": {{(id != "other" || other ? "true" : "false")}}}"""));
        return Questionnaires.Questionnaire.BuildConfig(Questionnaires.Questionnaire.ParseUpdate(Body.Of($$"""{"sections": [{{sections}}]}""")), firmId, Ids.Alice);
    }

    [Fact]
    public async Task A_firm_with_no_questionnaire_config_reads_none() =>
        Assert.Null(await new MemoryFirmStore().GetQuestionnaireAsync(Ids.Firm, Ct));

    [Fact]
    public async Task A_questionnaire_config_is_read_within_its_firm()
    {
        var store = new MemoryFirmStore();
        var saved = Questionnaire();
        await store.PutQuestionnaireAsync(saved, Ct);
        Assert.Equal(saved, await store.GetQuestionnaireAsync(Ids.Firm, Ct));
        Assert.Null(await store.GetQuestionnaireAsync(Ids.OtherFirm, Ct));
    }

    [Fact]
    public async Task A_questionnaire_save_replaces_the_stored_one()
    {
        var store = new MemoryFirmStore();
        await store.PutQuestionnaireAsync(Questionnaire(), Ct);
        await store.PutQuestionnaireAsync(Questionnaire(other: true), Ct);
        Assert.True((await store.GetQuestionnaireAsync(Ids.Firm, Ct))!.Sections["other"].Enabled);
    }

    [Fact]
    public async Task Resetting_the_questionnaire_twice_reports_the_truth()
    {
        var store = new MemoryFirmStore();
        await store.PutQuestionnaireAsync(Questionnaire(), Ct);
        Assert.True(await store.DeleteQuestionnaireAsync(Ids.Firm, Ct));
        Assert.False(await store.DeleteQuestionnaireAsync(Ids.Firm, Ct));
        Assert.Null(await store.GetQuestionnaireAsync(Ids.Firm, Ct));
    }

    // ── The document checklist (test_document_requests.py) ──────────

    [Fact]
    public async Task The_memory_firm_store_holds_one_checklist_per_firm()
    {
        var store = new MemoryFirmStore();
        var saved = DocumentChecklists.Build(DocumentChecklists.DefaultChecklist.Take(1), Ids.Firm, Ids.Alice);
        Assert.Null(await store.GetDocumentChecklistAsync(Ids.Firm, Ct));
        await store.PutDocumentChecklistAsync(saved, Ct);
        Assert.Equal(saved, await store.GetDocumentChecklistAsync(Ids.Firm, Ct));
        Assert.Null(await store.GetDocumentChecklistAsync(Ids.OtherFirm, Ct));
        Assert.True(await store.DeleteDocumentChecklistAsync(Ids.Firm, Ct));
        Assert.False(await store.DeleteDocumentChecklistAsync(Ids.Firm, Ct));
    }
}

// Translated from the memory half of test_client_binding_store.py.
public sealed class MemoryClientBindingStoreTests
{
    private const string Case = "00000000-0000-4000-8000-0000000ca5e1";
    private const string Pat = "00000000-0000-4000-8000-0000000000a1";
    private const string Sam = "00000000-0000-4000-8000-0000000000a2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClientBinding Binding(string subject = Pat, ClientRole[]? roles = null, BindingStatus status = BindingStatus.Invited, string firmId = Ids.Firm) =>
        ClientBindingTests.Binding(subject, roles, status, Case, firmId);

    [Fact]
    public async Task Two_bindings_cannot_both_hold_debtor_2()
    {
        var store = new MemoryClientBindingStore();
        await store.BindAsync(Binding(Pat, [ClientRole.Debtor2]), [], Ct);
        await Assert.ThrowsAsync<ConflictException>(() => store.BindAsync(Binding(Sam, [ClientRole.Debtor2]), [], Ct));
        Assert.Equal(Pat, store.Claims[(Case, ClientRole.Debtor2)]);
        Assert.Null(await store.GetAsync(Ids.Firm, Sam, Ct));
    }

    [Fact]
    public async Task A_narrowing_moves_the_role_and_rewrites_the_first_binding()
    {
        var store = new MemoryClientBindingStore();
        var first = Binding(Pat, [ClientRole.Debtor1, ClientRole.Debtor2], BindingStatus.Active);
        await store.BindAsync(first, [], Ct);
        await store.BindAsync(Binding(Sam, [ClientRole.Debtor2]), [first with { Roles = [ClientRole.Debtor1] }], Ct);
        Assert.Equal(Pat, store.Claims[(Case, ClientRole.Debtor1)]);
        Assert.Equal(Sam, store.Claims[(Case, ClientRole.Debtor2)]);
        Assert.Equal([ClientRole.Debtor1], (await store.GetAsync(Ids.Firm, Pat, Ct))!.Roles);
        Assert.Equal([Pat, Sam], (await store.ListForCaseAsync(Case, Ct)).Select(b => b.Subject));
    }

    [Fact]
    public async Task A_live_subject_cannot_be_bound_twice_but_a_revoked_one_can()
    {
        var store = new MemoryClientBindingStore();
        await store.BindAsync(Binding(), [], Ct);
        await Assert.ThrowsAsync<ConflictException>(() => store.BindAsync(Binding(), [], Ct));
        await store.UpdateAsync(ClientBindings.Revoke(Binding()), Ct);
        await store.BindAsync(Binding(), [], Ct);
        Assert.Equal(BindingStatus.Invited, (await store.GetAsync(Ids.Firm, Pat, Ct))!.Status);
    }

    [Fact]
    public async Task Revoking_releases_the_role_for_someone_else()
    {
        var store = new MemoryClientBindingStore();
        await store.BindAsync(Binding(Pat, [ClientRole.Debtor2]), [], Ct);
        await store.UpdateAsync(ClientBindings.Revoke(Binding(Pat, [ClientRole.Debtor2])), Ct);
        await store.BindAsync(Binding(Sam, [ClientRole.Debtor2]), [], Ct);
        Assert.Equal(Sam, store.Claims[(Case, ClientRole.Debtor2)]);
    }

    [Fact]
    public async Task Updating_a_binding_that_does_not_exist_returns_none() =>
        Assert.Null(await new MemoryClientBindingStore().UpdateAsync(Binding(status: BindingStatus.Revoked), Ct));

    [Fact]
    public async Task A_subject_in_two_firms_raises_rather_than_choosing()
    {
        var store = new MemoryClientBindingStore();
        await store.BindAsync(Binding(firmId: Ids.Firm), [], Ct);
        await store.BindAsync(Binding(firmId: Ids.OtherFirm), [], Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindAsync(Pat, Ct));
    }

    [Fact]
    public async Task The_email_lookup_is_firm_scoped()
    {
        var store = new MemoryClientBindingStore();
        await store.BindAsync(Binding(firmId: Ids.OtherFirm), [], Ct);
        Assert.Null(await store.FindByEmailAsync(Ids.Firm, "a1@example.test", Ct));
        Assert.NotNull(await store.FindByEmailAsync(Ids.OtherFirm, "A1@example.test", Ct));
    }

    [Fact]
    public async Task A_refused_bind_writes_nothing_at_all()
    {
        // The narrowed binding vanished between the plan and the write: the
        // whole transaction is refused, the new binding included.
        var store = new MemoryClientBindingStore();
        await Assert.ThrowsAsync<ConflictException>(() => store.BindAsync(Binding(Sam, [ClientRole.Debtor2]), [Binding(Pat, [ClientRole.Debtor1])], Ct));
        Assert.Null(await store.GetAsync(Ids.Firm, Sam, Ct));
        Assert.Empty(store.Claims);
    }
}

// Translated from test_user_directory.py: the fake's duplicate detection
// matches the real pool's (case-insensitive), and so do its resend refusals.
public sealed class MemoryUserDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("taken@example.test")]
    [InlineData("TAKEN@EXAMPLE.TEST")]
    [InlineData("Taken@Example.Test")]
    public async Task A_duplicate_address_is_refused_in_any_casing(string duplicate)
    {
        var directory = new MemoryUserDirectory();
        await directory.CreateUserAsync("taken@example.test", Ct);
        await Assert.ThrowsAsync<ConflictException>(() => directory.CreateUserAsync(duplicate, Ct));
    }

    [Fact]
    public async Task Distinct_addresses_each_get_their_own_subject()
    {
        var directory = new MemoryUserDirectory();
        var first = await directory.CreateUserAsync("a@example.test", Ct);
        var second = await directory.CreateUserAsync("b@example.test", Ct);
        Assert.NotEqual(first, second);
        Assert.Equal(["a@example.test", "b@example.test"], directory.Subjects.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Resending_to_an_unknown_address_is_not_found() =>
        await Assert.ThrowsAsync<NotFoundException>(() => new MemoryUserDirectory().ResendInviteAsync("nobody@example.test", Ct));

    [Fact]
    public async Task Resending_to_an_uncompleted_invite_records_the_resend()
    {
        var directory = new MemoryUserDirectory();
        await directory.CreateUserAsync("invited@example.test", Ct);
        await directory.ResendInviteAsync("invited@example.test", Ct);
        Assert.Equal(["invited@example.test"], directory.Resent);
    }

    [Fact]
    public async Task Resending_to_a_confirmed_user_is_refused()
    {
        var directory = new MemoryUserDirectory();
        await directory.CreateUserAsync("active@example.test", Ct);
        directory.Confirm("active@example.test");
        await Assert.ThrowsAsync<ConflictException>(() => directory.ResendInviteAsync("active@example.test", Ct));
    }

    [Fact]
    public async Task The_memory_access_log_keeps_what_it_recorded()
    {
        var log = new MemoryAccessLog();
        var accessEvent = AccessLog.AccessEvents.Record(Ids.Alice, AccessLog.AccessAction.CaseRead, caseId: "k-1");
        await log.RecordAsync(accessEvent, Ct);
        Assert.Equal([accessEvent], log.Events);
    }
}
