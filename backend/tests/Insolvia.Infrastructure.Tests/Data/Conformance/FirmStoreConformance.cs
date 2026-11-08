using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Fields;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.LibraryCreditors;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Core.Questionnaires;
using Insolvia.Data.Firms;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What every <see cref="IFirmStore"/> must do — run against the memory twin
/// (the oracle) AND the Postgres store, so the two cannot drift. Ported from
/// <c>MemoryStoreTests.cs</c> (itself the memory half of
/// <c>test_firm_store.py</c>, <c>test_client_funnel.py</c> and the store
/// tests of the questionnaire and checklist), plus a case for every
/// conditional write's refusal and every transaction's all-or-nothing.
/// </summary>
public abstract class FirmStoreConformance : IAsyncLifetime
{
    private protected FixedClock Clock { get; } = new();

    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Another handle on the SAME store (for Postgres: another connection to the same database), stamping with <see cref="Clock"/>.</summary>
    private protected abstract IFirmStore Connect();

    /// <summary>The two firms the tenant rows below belong to.</summary>
    private protected async Task<IFirmStore> SeededAsync()
    {
        var store = Connect();
        await store.CreateFirmAsync(Make.Firm(Ids.Firm), Ct);
        await store.CreateFirmAsync(Make.Firm(Ids.OtherFirm, "Other Firm LLP"), Ct);
        return store;
    }

    // ── Firms ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_firm_list_is_ordered_by_name_then_id()
    {
        var store = Connect();
        await store.CreateFirmAsync(Make.Firm(Ids.OtherFirm, "Other Firm LLP"), Ct);
        await store.CreateFirmAsync(Make.Firm(Ids.Firm, "Example & Partners"), Ct);
        await store.CreateFirmAsync(Make.Firm("00000000-0000-4000-8000-00000000f180", "Other Firm LLP"), Ct);
        Assert.Equal(
            [("Example & Partners", Ids.Firm), ("Other Firm LLP", "00000000-0000-4000-8000-00000000f180"), ("Other Firm LLP", Ids.OtherFirm)],
            (await store.ListFirmsAsync(Ct)).Select(firm => (firm.Name, firm.Id)));
    }

    [Fact]
    public async Task Creating_the_same_firm_twice_is_refused_and_keeps_the_first()
    {
        var store = Connect();
        await store.CreateFirmAsync(Make.Firm(), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateFirmAsync(Make.Firm(name: "Impostor"), Ct));
        Assert.Equal("Example & Partners", (await Connect().GetFirmAsync(Ids.Firm, Ct))!.Name);
    }

    [Fact]
    public async Task Updating_a_firm_that_is_not_there_is_none_and_creates_nothing()
    {
        var store = Connect();
        Assert.Null(await store.UpdateFirmAsync(Make.Firm(), Ct));
        Assert.Null(await store.GetFirmAsync(Ids.Firm, Ct));
    }

    [Fact]
    public async Task Updating_a_firm_writes_it_back()
    {
        var store = Connect();
        await store.CreateFirmAsync(Make.Firm(), Ct);
        var suspended = FirmLifecycle.SetFirmStatus(Make.Firm(), FirmStatus.Suspended, Clock);
        Assert.Equal(suspended, await store.UpdateFirmAsync(suspended, Ct));
        Assert.Equal(suspended, await Connect().GetFirmAsync(Ids.Firm, Ct));
    }

    [Fact]
    public async Task A_firm_round_trips_its_provenance_and_defaults()
    {
        var store = Connect();
        var firm = Make.Firm() with
        {
            CreatedBy = "staff-sub",
            CreatedByEmail = "staff@example.test",
            DefaultCourt = new DefaultCourt("flmb", "tampa"),
            DefaultChapter = 13,
            Letterhead = new Letterhead("Example & Partners", new Address("1 Main St", City: "Tampa", State: "FL", PostalCode: "33602"), "555-0100"),
        };
        await store.CreateFirmAsync(firm, Ct);
        Assert.Equal(firm, await Connect().GetFirmAsync(Ids.Firm, Ct));
    }

    // ── Firm users ──────────────────────────────────────────────────

    [Fact]
    public async Task Adding_the_same_person_twice_is_refused()
    {
        var store = await SeededAsync();
        await store.AddUserAsync(Make.User(), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddUserAsync(Make.User(lastName: "Impostor"), Ct));
        Assert.Equal("Attorney", (await Connect().GetUserAsync(Ids.Firm, Ids.Alice, Ct))!.LastName);
    }

    [Fact]
    public async Task A_user_is_read_within_their_firm()
    {
        var store = await SeededAsync();
        await store.AddUserAsync(Make.User(), Ct);
        Assert.Equal(Make.User(), await store.GetUserAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Null(await store.GetUserAsync(Ids.OtherFirm, Ids.Alice, Ct));
    }

    [Fact]
    public async Task A_user_round_trips_their_permissions_and_signature_block()
    {
        var store = await SeededAsync();
        var user = Make.User(role: FirmRole.Staff) with
        {
            IsAdmin = true,
            AccessAllCases = true,
            Status = FirmUserStatus.Disabled,
            SignatureBlock = new SignatureBlock("12345", "FL", "Example & Partners", new Address("1 Main St"), "555-0100", "alice@example.test"),
        };
        await store.AddUserAsync(user, Ct);
        var read = await Connect().GetUserAsync(Ids.Firm, Ids.Alice, Ct);
        Assert.Equal(user, read);
        Assert.Equal(Permissions.DefaultFor(FirmRole.Staff), read!.Permissions);
    }

    [Fact]
    public async Task Finding_a_subject_resolves_their_one_firm()
    {
        var store = await SeededAsync();
        await store.AddUserAsync(Make.User(), Ct);
        Assert.Equal(Ids.Firm, (await store.FindUserAsync(Ids.Alice, Ct))!.FirmId);
    }

    [Fact]
    public async Task Finding_a_subject_in_two_firms_raises_rather_than_guessing()
    {
        var store = await SeededAsync();
        await store.AddUserAsync(Make.User(), Ct);
        await store.AddUserAsync(Make.User(firmId: Ids.OtherFirm), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindUserAsync(Ids.Alice, Ct));
    }

    [Fact]
    public async Task Finding_an_unprovisioned_subject_is_none_not_an_error() =>
        Assert.Null(await Connect().FindUserAsync(Ids.Alice, Ct));

    [Fact]
    public async Task A_staff_list_is_one_firm_ordered_by_surname_then_first_name_then_subject()
    {
        var store = await SeededAsync();
        await store.AddUserAsync(Make.User(subject: Ids.Bob, firstName: "Adam", lastName: "Zimmerman"), Ct);
        await store.AddUserAsync(Make.User(firstName: "Zoe", lastName: "Adams"), Ct);
        await store.AddUserAsync(Make.User(subject: Ids.Pat, firstName: "Abe", lastName: "Adams"), Ct);
        await store.AddUserAsync(Make.User(subject: Ids.Sam, firstName: "Abe", lastName: "Adams"), Ct);
        await store.AddUserAsync(Make.User(subject: Ids.Bob, firmId: Ids.OtherFirm, firstName: "Aaron", lastName: "Other"), Ct);
        Assert.Equal(
            [Ids.Pat, Ids.Sam, Ids.Alice, Ids.Bob],
            (await store.ListUsersAsync(Ids.Firm, Ct)).Select(user => user.Subject));
    }

    [Fact]
    public async Task Updating_someone_who_is_not_there_is_none_and_adds_nobody()
    {
        var store = await SeededAsync();
        Assert.Null(await store.UpdateUserAsync(Make.User(), Ct));
        Assert.Null(await store.GetUserAsync(Ids.Firm, Ids.Alice, Ct));
    }

    [Fact]
    public async Task Updating_across_firms_does_not_move_someone()
    {
        var store = await SeededAsync();
        await store.AddUserAsync(Make.User(), Ct);
        Assert.Null(await store.UpdateUserAsync(Make.User() with { FirmId = Ids.OtherFirm, LastName = "Moved" }, Ct));
        Assert.Null(await store.GetUserAsync(Ids.OtherFirm, Ids.Alice, Ct));
        Assert.Equal("Attorney", (await store.GetUserAsync(Ids.Firm, Ids.Alice, Ct))!.LastName);
    }

    [Fact]
    public async Task Updating_someone_writes_them_back()
    {
        var store = await SeededAsync();
        await store.AddUserAsync(Make.User(), Ct);
        var changed = FirmLifecycle.ApplyUserChanges(Make.User(), new FirmUserChanges(LastName: "Advocate", IsAdmin: true), Clock);
        Assert.Equal(changed, await store.UpdateUserAsync(changed, Ct));
        Assert.Equal(changed, await Connect().GetUserAsync(Ids.Firm, Ids.Alice, Ct));
    }

    [Fact]
    public async Task Removing_someone_twice_reports_the_truth()
    {
        var store = await SeededAsync();
        await store.AddUserAsync(Make.User(), Ct);
        Assert.False(await store.RemoveUserAsync(Ids.OtherFirm, Ids.Alice, Ct));
        Assert.True(await store.RemoveUserAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.False(await store.RemoveUserAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Null(await store.FindUserAsync(Ids.Alice, Ct));
    }

    // ── Library creditors ───────────────────────────────────────────

    [Fact]
    public async Task Creating_the_same_library_creditor_twice_is_refused()
    {
        var store = await SeededAsync();
        var creditor = Make.Creditor();
        await store.CreateLibraryCreditorAsync(creditor, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateLibraryCreditorAsync(creditor with { Name = "Impostor" }, Ct));
        Assert.Equal("Acme Collections", (await store.GetLibraryCreditorAsync(Ids.Firm, creditor.Id, Ct))!.Name);
    }

    [Fact]
    public async Task A_library_creditor_is_read_within_its_firm()
    {
        var store = await SeededAsync();
        var creditor = Make.Creditor();
        await store.CreateLibraryCreditorAsync(creditor, Ct);
        Assert.Equal(creditor, await Connect().GetLibraryCreditorAsync(Ids.Firm, creditor.Id, Ct));
        Assert.Null(await store.GetLibraryCreditorAsync(Ids.OtherFirm, creditor.Id, Ct));
    }

    [Fact]
    public async Task A_library_creditor_round_trips_its_address_and_notice_parties()
    {
        var store = await SeededAsync();
        var creditor = LibraryCreditorRules.Create(
            LibraryCreditorRules.Parse(Make.Json("""
                {"name": "Acme Collections", "address": {"line1": "PO Box 1", "city": "Dover", "state": "DE", "postal_code": "19901"},
                 "additional_notice_parties": [{"id": "np-1", "name": "Acme Legal", "address": {"raw": "c/o Counsel, Dover DE"}, "account_last4": "1234"}],
                 "notes": "Prefers fax."}
                """)),
            Ids.Firm);
        await store.CreateLibraryCreditorAsync(creditor, Ct);
        Assert.Equal(creditor, await Connect().GetLibraryCreditorAsync(Ids.Firm, creditor.Id, Ct));
    }

    [Fact]
    public async Task The_library_is_listed_by_name_within_one_firm()
    {
        var store = await SeededAsync();
        await store.CreateLibraryCreditorAsync(Make.Creditor(name: "Zeta Bank"), Ct);
        await store.CreateLibraryCreditorAsync(Make.Creditor(name: "Acme Collections"), Ct);
        await store.CreateLibraryCreditorAsync(Make.Creditor(name: "acme lower"), Ct);
        await store.CreateLibraryCreditorAsync(Make.Creditor(Ids.OtherFirm, "Other Firm's Bank"), Ct);
        Assert.Equal(["Acme Collections", "Zeta Bank", "acme lower"], (await store.ListLibraryCreditorsAsync(Ids.Firm, Ct)).Select(c => c.Name));
    }

    [Fact]
    public async Task Updating_a_library_creditor_that_is_not_there_is_none()
    {
        var store = await SeededAsync();
        var creditor = Make.Creditor();
        Assert.Null(await store.UpdateLibraryCreditorAsync(creditor, Ct));
        Assert.Null(await store.GetLibraryCreditorAsync(Ids.Firm, creditor.Id, Ct));
    }

    [Fact]
    public async Task Updating_across_firms_does_not_move_a_library_creditor()
    {
        var store = await SeededAsync();
        var creditor = Make.Creditor();
        await store.CreateLibraryCreditorAsync(creditor, Ct);
        Assert.Null(await store.UpdateLibraryCreditorAsync(creditor with { FirmId = Ids.OtherFirm }, Ct));
        Assert.Null(await store.GetLibraryCreditorAsync(Ids.OtherFirm, creditor.Id, Ct));
    }

    [Fact]
    public async Task Updating_a_library_creditor_writes_it_back()
    {
        var store = await SeededAsync();
        var creditor = Make.Creditor();
        await store.CreateLibraryCreditorAsync(creditor, Ct);
        var replaced = LibraryCreditorRules.Replace(creditor, LibraryCreditorRules.Parse(Make.Json("""{"name": "Acme Recovery", "preferred": false}""")), Clock);
        Assert.Equal(replaced, await store.UpdateLibraryCreditorAsync(replaced, Ct));
        Assert.Equal(replaced, await Connect().GetLibraryCreditorAsync(Ids.Firm, creditor.Id, Ct));
    }

    [Fact]
    public async Task Removing_a_library_creditor_twice_reports_the_truth()
    {
        var store = await SeededAsync();
        var creditor = Make.Creditor();
        await store.CreateLibraryCreditorAsync(creditor, Ct);
        Assert.False(await store.DeleteLibraryCreditorAsync(Ids.OtherFirm, creditor.Id, Ct));
        Assert.True(await store.DeleteLibraryCreditorAsync(Ids.Firm, creditor.Id, Ct));
        Assert.False(await store.DeleteLibraryCreditorAsync(Ids.Firm, creditor.Id, Ct));
    }

    // ── Firm clients ────────────────────────────────────────────────

    [Fact]
    public async Task Creating_the_same_client_twice_is_refused()
    {
        var store = await SeededAsync();
        var client = Make.Client();
        await store.CreateClientAsync(client, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateClientAsync(client with { Phone = "555-0199" }, Ct));
        Assert.Equal(client, await store.GetClientAsync(Ids.Firm, client.Id, Ct));
    }

    [Fact]
    public async Task A_client_is_read_within_its_firm()
    {
        var store = await SeededAsync();
        var client = Make.Client();
        await store.CreateClientAsync(client, Ct);
        Assert.Equal(client, await Connect().GetClientAsync(Ids.Firm, client.Id, Ct));
        Assert.Null(await store.GetClientAsync(Ids.OtherFirm, client.Id, Ct));
    }

    [Fact]
    public async Task A_client_round_trips_every_field()
    {
        var store = await SeededAsync();
        var client = FirmClientRules.Create(
            FirmClientRules.Parse(Make.Json("""
                {"name": {"given": "Jordan", "middle": "Q", "surname": "Example", "suffix": "Jr."},
                 "other_names_used": [{"id": "on-1", "given": "Jordy", "surname": "Sample"}],
                 "date_of_birth": "1980-02-29",
                 "residence_address": {"line1": "1 Main St", "city": "Tampa", "state": "FL", "postal_code": "33602", "county": "Hillsborough"},
                 "mailing_address": {"raw": "PO Box 9, Tampa FL"},
                 "phone": "555-0100", "mobile": "555-0101", "email": "jordan@example.test",
                 "lead_source": "Website", "referred_by": "A. Friend", "first_retained_at": "2026-07-20"}
                """)),
            Ids.Firm,
            Ids.Alice) with
        { TaxIdRef = "taxid-ref-1", TaxIdLastFour = "6789" };
        await store.CreateClientAsync(client, Ct);
        Assert.Equal(client, await Connect().GetClientAsync(Ids.Firm, client.Id, Ct));
    }

    [Fact]
    public async Task The_directory_is_one_firm_in_directory_order()
    {
        var store = await SeededAsync();
        await store.CreateClientAsync(Make.Client(surname: "Zeta"), Ct);
        await store.CreateClientAsync(Make.Client(surname: "adams"), Ct);
        await store.CreateClientAsync(Make.Client(Ids.OtherFirm, "Other"), Ct);
        await store.CreateClientAsync(FirmClientRules.SetStatus(Make.Client(surname: "Middle"), FirmClientStatus.Archived), Ct);

        // Archived clients are listed too: the directory is the whole directory.
        Assert.Equal(["adams", "Middle", "Zeta"], (await store.ListClientsAsync(Ids.Firm, Ct)).Select(c => c.Name.Surname));
    }

    [Fact]
    public async Task Updating_a_client_that_is_not_there_is_none()
    {
        var store = await SeededAsync();
        var client = Make.Client();
        Assert.Null(await store.UpdateClientAsync(client, Ct));
        Assert.Null(await store.GetClientAsync(Ids.Firm, client.Id, Ct));
    }

    [Fact]
    public async Task Updating_across_firms_does_not_move_a_client()
    {
        var store = await SeededAsync();
        var client = Make.Client();
        await store.CreateClientAsync(client, Ct);
        Assert.Null(await store.UpdateClientAsync(client with { FirmId = Ids.OtherFirm }, Ct));
        Assert.Null(await store.GetClientAsync(Ids.OtherFirm, client.Id, Ct));
    }

    [Fact]
    public async Task A_client_update_writes_the_record_and_never_the_merge_attributes()
    {
        var store = await SeededAsync();
        var client = Make.Client();
        await store.CreateClientAsync(client, Ct);
        var written = await store.UpdateClientAsync(client with { MergingInto = "x", MergingFrom = "y", ProspectStage = ProspectStage.Possible, Phone = "555-0199" }, Ct);
        Assert.NotNull(written);
        Assert.Equal(("555-0199", (string?)null, (string?)null, (ProspectStage?)null), (written.Phone, written.MergingInto, written.MergingFrom, written.ProspectStage));
        Assert.Equal(written, await Connect().GetClientAsync(Ids.Firm, client.Id, Ct));
    }

    [Fact]
    public async Task A_client_update_leaves_a_stored_claim_and_stage_exactly_as_they_were()
    {
        var store = await SeededAsync();
        var (merged, survivor, other) = (Make.Client(surname: "Merged"), Make.Client(surname: "Survivor"), Make.Client(surname: "Other"));
        foreach (var client in new[] { merged, survivor, other })
        {
            await store.CreateClientAsync(client, Ct);
        }

        await store.SetClientProspectStageAsync(Ids.Firm, merged.Id, ProspectStage.ConsultationScheduled, Ct);
        await store.SetClientProspectStageAsync(Ids.Firm, survivor.Id, ProspectStage.Possible, Ct);
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));

        // A whole-record save from a stale read: every server-owned attribute null.
        var savedMerged = await store.UpdateClientAsync(merged with { Email = "m@example.test" }, Ct);
        var savedSurvivor = await store.UpdateClientAsync(survivor with { MergingFrom = other.Id, Email = "s@example.test" }, Ct);

        Assert.Equal(("m@example.test", survivor.Id, (string?)null, ProspectStage.ConsultationScheduled), (savedMerged!.Email, savedMerged.MergingInto, savedMerged.MergedInto, savedMerged.ProspectStage));
        Assert.Equal(("s@example.test", merged.Id, ProspectStage.Possible), (savedSurvivor!.Email, savedSurvivor.MergingFrom, savedSurvivor.ProspectStage));
        Assert.Equal(savedMerged, await Connect().GetClientAsync(Ids.Firm, merged.Id, Ct));
    }

    [Fact]
    public async Task A_merged_client_takes_no_update()
    {
        var store = await SeededAsync();
        var (merged, survivor) = await MergedPairAsync(store);
        Assert.Null(await store.UpdateClientAsync(merged with { Phone = "555-0199", Status = FirmClientStatus.Active }, Ct));
        Assert.Equal(FirmClientStatus.Archived, (await store.GetClientAsync(Ids.Firm, merged.Id, Ct))!.Status);
        Assert.NotNull(await store.UpdateClientAsync(survivor with { Phone = "555-0199" }, Ct));
    }

    // ── The prospect funnel (test_client_funnel.py) ─────────────────

    [Fact]
    public async Task A_prospects_stage_is_set_and_cleared_and_stamped()
    {
        var store = await SeededAsync();
        var client = Make.Client();
        await store.CreateClientAsync(client, Ct);
        var staged = await store.SetClientProspectStageAsync(Ids.Firm, client.Id, ProspectStage.Possible, Ct);
        Clock.Now = Clock.Now.AddMinutes(1);
        var cleared = await store.SetClientProspectStageAsync(Ids.Firm, client.Id, null, Ct);
        Assert.Equal(ProspectStage.Possible, staged!.ProspectStage);
        Assert.Null(cleared!.ProspectStage);
        Assert.Equal(Clock.Now, cleared.UpdatedAt);
        Assert.Equal(cleared, await Connect().GetClientAsync(Ids.Firm, client.Id, Ct));
    }

    [Fact]
    public async Task A_retained_client_takes_no_stage_and_nothing_is_written()
    {
        var store = await SeededAsync();
        var client = Make.Client(retained: "2026-07-20");
        await store.CreateClientAsync(client, Ct);
        Assert.Null(await store.SetClientProspectStageAsync(Ids.Firm, client.Id, ProspectStage.Possible, Ct));
        Assert.Equal(client, await store.GetClientAsync(Ids.Firm, client.Id, Ct));
    }

    [Fact]
    public async Task A_stage_is_refused_for_a_missing_a_foreign_or_a_merged_client()
    {
        var store = await SeededAsync();
        var client = Make.Client();
        await store.CreateClientAsync(client, Ct);
        var (merged, _) = await MergedPairAsync(store);
        Assert.Null(await store.SetClientProspectStageAsync(Ids.Firm, "no-such-client", ProspectStage.Possible, Ct));
        Assert.Null(await store.SetClientProspectStageAsync(Ids.OtherFirm, client.Id, ProspectStage.Possible, Ct));
        Assert.Null(await store.SetClientProspectStageAsync(Ids.Firm, merged.Id, ProspectStage.Possible, Ct));
        Assert.Null((await store.GetClientAsync(Ids.Firm, merged.Id, Ct))!.ProspectStage);
    }

    [Fact]
    public async Task The_retained_transition_stamps_and_clears_the_stage()
    {
        var store = await SeededAsync();
        var client = Make.Client() with { ProspectStage = ProspectStage.AwaitingSignedAgreement };
        await store.CreateClientAsync(client, Ct);
        var retained = await store.MarkClientRetainedAsync(Ids.Firm, client.Id, new DateOnly(2026, 10, 1), Ct);
        Assert.Equal(new DateOnly(2026, 10, 1), retained!.FirstRetainedAt);
        Assert.Null(retained.ProspectStage);
        Assert.Equal(Clock.Now, retained.UpdatedAt);
        Assert.Equal(retained, await Connect().GetClientAsync(Ids.Firm, client.Id, Ct));
    }

    [Fact]
    public async Task The_retained_transition_keeps_an_earlier_date()
    {
        var store = await SeededAsync();
        var client = Make.Client(retained: "2020-01-02");
        await store.CreateClientAsync(client, Ct);
        var retained = await store.MarkClientRetainedAsync(Ids.Firm, client.Id, new DateOnly(2026, 10, 1), Ct);
        Assert.Equal(new DateOnly(2020, 1, 2), retained!.FirstRetainedAt);
    }

    [Fact]
    public async Task The_retained_transition_is_refused_for_a_missing_a_foreign_or_a_merged_client()
    {
        var store = await SeededAsync();
        var client = Make.Client();
        await store.CreateClientAsync(client, Ct);
        var (merged, _) = await MergedPairAsync(store);
        Assert.Null(await store.MarkClientRetainedAsync(Ids.Firm, "no-such-client", new DateOnly(2026, 10, 1), Ct));
        Assert.Null(await store.MarkClientRetainedAsync(Ids.OtherFirm, client.Id, new DateOnly(2026, 10, 1), Ct));
        Assert.Null(await store.MarkClientRetainedAsync(Ids.Firm, merged.Id, new DateOnly(2026, 10, 1), Ct));
        Assert.Null((await store.GetClientAsync(Ids.Firm, merged.Id, Ct))!.FirstRetainedAt);
    }

    [Fact]
    public async Task A_whole_record_save_leaves_the_stage_as_stored()
    {
        var store = await SeededAsync();
        var client = Make.Client();
        await store.CreateClientAsync(client, Ct);
        await store.SetClientProspectStageAsync(Ids.Firm, client.Id, ProspectStage.Possible, Ct);
        await store.UpdateClientAsync(client with { LeadSource = "Referral" }, Ct);
        Assert.Equal(ProspectStage.Possible, (await store.GetClientAsync(Ids.Firm, client.Id, Ct))!.ProspectStage);
    }

    // ── The client merge's claim (client_merge.py's race) ───────────

    [Fact]
    public async Task A_claim_marks_both_rows_and_a_rerun_for_the_same_pair_passes()
    {
        var store = await SeededAsync();
        var (merged, survivor) = await PairAsync(store);
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));
        Assert.Equal(survivor.Id, (await store.GetClientAsync(Ids.Firm, merged.Id, Ct))!.MergingInto);
        Assert.Equal(merged.Id, (await store.GetClientAsync(Ids.Firm, survivor.Id, Ct))!.MergingFrom);
    }

    [Fact]
    public async Task A_client_cannot_be_merged_into_itself()
    {
        var store = await SeededAsync();
        var (merged, _) = await PairAsync(store);
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, merged.Id, Ct));
        Assert.Null((await store.GetClientAsync(Ids.Firm, merged.Id, Ct))!.MergingInto);
    }

    [Fact]
    public async Task A_claim_needs_both_rows_in_the_firm()
    {
        var store = await SeededAsync();
        var (merged, survivor) = await PairAsync(store);
        var foreign = Make.Client(Ids.OtherFirm, "Foreign");
        await store.CreateClientAsync(foreign, Ct);

        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, "no-such-client", Ct));
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, "no-such-client", survivor.Id, Ct));
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, foreign.Id, Ct));
        Assert.False(await store.ClaimClientMergeAsync(Ids.OtherFirm, merged.Id, survivor.Id, Ct));
        await AssertUnclaimedAsync(store, merged, survivor);
    }

    [Fact]
    public async Task A_claim_is_refused_over_an_archived_row_and_writes_neither_half()
    {
        var store = await SeededAsync();
        var (merged, survivor) = await PairAsync(store);
        await store.UpdateClientAsync(FirmClientRules.SetStatus(survivor, FirmClientStatus.Archived), Ct);
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, survivor.Id, merged.Id, Ct));

        // The merged row's half ran first and must have rolled back with the survivor's refusal.
        await AssertUnclaimedAsync(store, merged, survivor);
    }

    [Fact]
    public async Task A_claim_is_refused_over_a_row_already_merged()
    {
        var store = await SeededAsync();
        var (gone, survivor) = await MergedPairAsync(store);
        var third = Make.Client(surname: "Third");
        await store.CreateClientAsync(third, Ct);
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, gone.Id, third.Id, Ct));
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, third.Id, gone.Id, Ct));
        Assert.Null((await store.GetClientAsync(Ids.Firm, third.Id, Ct))!.MergingInto);
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, third.Id, survivor.Id, Ct));
    }

    [Fact]
    public async Task A_row_in_one_merge_cannot_join_another()
    {
        var store = await SeededAsync();
        var (merged, survivor) = await PairAsync(store);
        var third = Make.Client(surname: "Third");
        await store.CreateClientAsync(third, Ct);
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));

        // The merged row is claimed by another survivor; the survivor is receiving another merge;
        // a row being merged away cannot survive; a row receiving a merge cannot be merged away.
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, third.Id, Ct));
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, third.Id, survivor.Id, Ct));
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, third.Id, merged.Id, Ct));
        Assert.False(await store.ClaimClientMergeAsync(Ids.Firm, survivor.Id, third.Id, Ct));

        var untouched = await store.GetClientAsync(Ids.Firm, third.Id, Ct);
        Assert.Equal(((string?)null, (string?)null), (untouched!.MergingInto, untouched.MergingFrom));
        Assert.Equal(survivor.Id, (await store.GetClientAsync(Ids.Firm, merged.Id, Ct))!.MergingInto);
    }

    [Fact]
    public async Task Two_racing_claims_on_the_same_rows_let_exactly_one_through()
    {
        var store = await SeededAsync();
        var (left, right) = await PairAsync(store);
        var third = Make.Client(surname: "Third");
        await store.CreateClientAsync(third, Ct);

        var opposite = await Task.WhenAll(
            Connect().ClaimClientMergeAsync(Ids.Firm, left.Id, right.Id, Ct),
            Connect().ClaimClientMergeAsync(Ids.Firm, right.Id, left.Id, Ct));
        Assert.Single(opposite, won => won);

        await store.ReleaseClientMergeAsync(Ids.Firm, left.Id, right.Id, Ct);
        await store.ReleaseClientMergeAsync(Ids.Firm, right.Id, left.Id, Ct);
        var sameSurvivor = await Task.WhenAll(
            Connect().ClaimClientMergeAsync(Ids.Firm, left.Id, right.Id, Ct),
            Connect().ClaimClientMergeAsync(Ids.Firm, third.Id, right.Id, Ct));
        Assert.Single(sameSurvivor, won => won);
    }

    [Fact]
    public async Task Finishing_archives_the_merged_row_and_releases_both_halves()
    {
        var store = await SeededAsync();
        var (merged, survivor) = await PairAsync(store);
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));
        var at = Clock.Now.AddHours(1);

        var archived = await store.FinishClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, at, Ct);

        Assert.NotNull(archived);
        Assert.Equal((FirmClientStatus.Archived, survivor.Id, (string?)null, at), (archived.Status, archived.MergedInto, archived.MergingInto, archived.UpdatedAt));
        Assert.Equal(archived, await Connect().GetClientAsync(Ids.Firm, merged.Id, Ct));
        Assert.Null((await store.GetClientAsync(Ids.Firm, survivor.Id, Ct))!.MergingFrom);
    }

    [Fact]
    public async Task Finishing_without_this_pairs_claim_is_none_and_writes_nothing()
    {
        var store = await SeededAsync();
        var (merged, survivor) = await PairAsync(store);
        var third = Make.Client(surname: "Third");
        await store.CreateClientAsync(third, Ct);

        Assert.Null(await store.FinishClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Clock.Now, Ct));
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));
        Assert.Null(await store.FinishClientMergeAsync(Ids.Firm, merged.Id, third.Id, Clock.Now, Ct));
        Assert.Null(await store.FinishClientMergeAsync(Ids.OtherFirm, merged.Id, survivor.Id, Clock.Now, Ct));

        var stillClaimed = await store.GetClientAsync(Ids.Firm, merged.Id, Ct);
        Assert.Equal((FirmClientStatus.Active, survivor.Id, (string?)null), (stillClaimed!.Status, stillClaimed.MergingInto, stillClaimed.MergedInto));
    }

    [Fact]
    public async Task Finishing_needs_both_halves_of_the_claim_and_rolls_back_the_first()
    {
        // A half-claim no port writes (a crash between two DynamoDB writes
        // could): the merged row names the survivor, the survivor names nobody.
        var store = await SeededAsync();
        var survivor = Make.Client(surname: "Survivor");
        await store.CreateClientAsync(survivor, Ct);
        var merged = Make.Client(surname: "Merged") with { MergingInto = survivor.Id };
        await store.CreateClientAsync(merged, Ct);

        Assert.Null(await store.FinishClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Clock.Now, Ct));

        // The merged row's archive ran first and rolled back with the survivor's refusal.
        Assert.Equal(merged, await store.GetClientAsync(Ids.Firm, merged.Id, Ct));
        Assert.Equal(survivor, await store.GetClientAsync(Ids.Firm, survivor.Id, Ct));
    }

    [Fact]
    public async Task Finishing_twice_is_none_the_second_time()
    {
        var store = await SeededAsync();
        var (merged, survivor) = await PairAsync(store);
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));
        Assert.NotNull(await store.FinishClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Clock.Now, Ct));
        Assert.Null(await store.FinishClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Clock.Now, Ct));
    }

    [Fact]
    public async Task Releasing_drops_this_pairs_claim_and_leaves_another_pairs_alone()
    {
        var store = await SeededAsync();
        var (merged, survivor) = await PairAsync(store);
        var third = Make.Client(surname: "Third");
        await store.CreateClientAsync(third, Ct);
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));

        await store.ReleaseClientMergeAsync(Ids.Firm, merged.Id, third.Id, Ct);
        await store.ReleaseClientMergeAsync(Ids.Firm, third.Id, survivor.Id, Ct);
        Assert.Equal(survivor.Id, (await store.GetClientAsync(Ids.Firm, merged.Id, Ct))!.MergingInto);
        Assert.Equal(merged.Id, (await store.GetClientAsync(Ids.Firm, survivor.Id, Ct))!.MergingFrom);

        await store.ReleaseClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct);
        await AssertUnclaimedAsync(store, merged, survivor);
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, third.Id, Ct));
    }

    // ── The questionnaire config ────────────────────────────────────

    private static QuestionnaireConfig Config(string firmId = Ids.Firm, bool other = false, string? propertyInstructions = null)
    {
        var sections = string.Join(", ", Questionnaire.SectionIds.Select(id =>
            $$"""{"id": "{{id}}", "enabled": {{(id != "other" || other ? "true" : "false")}}{{(id == "property" && propertyInstructions is not null ? $", \"instructions\": \"{propertyInstructions}\"" : "")}}}"""));
        return Questionnaire.BuildConfig(Questionnaire.ParseUpdate(Make.Json($$"""{"sections": [{{sections}}]}""")), firmId, Ids.Alice);
    }

    [Fact]
    public async Task A_firm_with_no_questionnaire_config_reads_none() =>
        Assert.Null(await Connect().GetQuestionnaireAsync(Ids.Firm, Ct));

    [Fact]
    public async Task A_questionnaire_config_is_read_within_its_firm()
    {
        var store = await SeededAsync();
        var saved = Config(propertyInstructions: "Include the boat.");
        await store.PutQuestionnaireAsync(saved, Ct);
        Assert.Equal(saved, await Connect().GetQuestionnaireAsync(Ids.Firm, Ct));
        Assert.Null(await store.GetQuestionnaireAsync(Ids.OtherFirm, Ct));
    }

    [Fact]
    public async Task A_questionnaire_save_replaces_the_stored_one()
    {
        var store = await SeededAsync();
        await store.PutQuestionnaireAsync(Config(), Ct);
        var replacement = Config(other: true);
        await store.PutQuestionnaireAsync(replacement, Ct);
        Assert.Equal(replacement, await store.GetQuestionnaireAsync(Ids.Firm, Ct));
        Assert.True((await store.GetQuestionnaireAsync(Ids.Firm, Ct))!.Sections["other"].Enabled);
    }

    [Fact]
    public async Task Resetting_the_questionnaire_twice_reports_the_truth()
    {
        var store = await SeededAsync();
        await store.PutQuestionnaireAsync(Config(), Ct);
        Assert.False(await store.DeleteQuestionnaireAsync(Ids.OtherFirm, Ct));
        Assert.True(await store.DeleteQuestionnaireAsync(Ids.Firm, Ct));
        Assert.False(await store.DeleteQuestionnaireAsync(Ids.Firm, Ct));
        Assert.Null(await store.GetQuestionnaireAsync(Ids.Firm, Ct));
    }

    // ── The document checklist (test_document_requests.py) ──────────

    [Fact]
    public async Task The_firm_store_holds_one_checklist_per_firm()
    {
        var store = await SeededAsync();
        var saved = DocumentChecklists.Build(DocumentChecklists.DefaultChecklist.Take(1), Ids.Firm, Ids.Alice);
        Assert.Null(await store.GetDocumentChecklistAsync(Ids.Firm, Ct));
        await store.PutDocumentChecklistAsync(saved, Ct);
        Assert.Equal(saved, await Connect().GetDocumentChecklistAsync(Ids.Firm, Ct));
        Assert.Null(await store.GetDocumentChecklistAsync(Ids.OtherFirm, Ct));
        Assert.True(await store.DeleteDocumentChecklistAsync(Ids.Firm, Ct));
        Assert.False(await store.DeleteDocumentChecklistAsync(Ids.Firm, Ct));
    }

    [Fact]
    public async Task A_checklist_save_replaces_the_stored_one_and_an_empty_one_is_a_choice()
    {
        var store = await SeededAsync();
        await store.PutDocumentChecklistAsync(DocumentChecklists.Build(DocumentChecklists.DefaultChecklist, Ids.Firm, Ids.Alice), Ct);
        var empty = DocumentChecklists.Build([], Ids.Firm, Ids.Bob);
        await store.PutDocumentChecklistAsync(empty, Ct);
        var read = await store.GetDocumentChecklistAsync(Ids.Firm, Ct);
        Assert.Equal(empty, read);
        Assert.Empty(read!.Items);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static async Task<(FirmClient Merged, FirmClient Survivor)> PairAsync(IFirmStore store)
    {
        var merged = Make.Client(surname: "Merged");
        var survivor = Make.Client(surname: "Survivor");
        await store.CreateClientAsync(merged, Ct);
        await store.CreateClientAsync(survivor, Ct);
        return (merged, survivor);
    }

    private async Task<(FirmClient Merged, FirmClient Survivor)> MergedPairAsync(IFirmStore store)
    {
        var (merged, survivor) = await PairAsync(store);
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));
        var archived = await store.FinishClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Clock.Now, Ct);
        return (archived!, (await store.GetClientAsync(Ids.Firm, survivor.Id, Ct))!);
    }

    private static async Task AssertUnclaimedAsync(IFirmStore store, params FirmClient[] clients)
    {
        foreach (var client in clients)
        {
            var read = await store.GetClientAsync(client.FirmId, client.Id, Ct);
            Assert.Equal(((string?)null, (string?)null, (string?)null), (read!.MergingInto, read.MergingFrom, read.MergedInto));
        }
    }
}

/// <summary>The oracle: <see cref="MemoryFirmStore"/> against its own suite.</summary>
public sealed class MemoryFirmStoreConformance : FirmStoreConformance
{
    private readonly MemoryFirmStore _store;

    public MemoryFirmStoreConformance()
    {
        _store = new MemoryFirmStore(Clock);
    }

    private protected override IFirmStore Connect() => _store;
}

/// <summary><see cref="PostgresFirmStore"/>, held to the oracle's suite on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresFirmStoreConformance(PostgresFixture postgres) : FirmStoreConformance
{
    private readonly PostgresHarness _harness = new(postgres);

    public override async ValueTask InitializeAsync() => await _harness.InitializeAsync();

    public override async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await base.DisposeAsync();
    }

    private protected override IFirmStore Connect() => new PostgresFirmStore(_harness.NewContext(), Clock);
}
