using Insolvia.Core.Access;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Clients;
using Insolvia.Core.Firms;

namespace Insolvia.Core.Tests;

// Translated from test_clients.py: the portal binding (ADR 0023) — what an
// invitation may say and which bindings a new one narrows. Its item-shape
// tests are not here (18.10); its portal-profile tests are in AuthTests.
public sealed class ClientBindingTests
{
    private const string Case = "00000000-0000-4000-8000-0000000ca5e1";
    private const string OtherCase = "00000000-0000-4000-8000-0000000ca5e2";
    private const string Staff = Ids.Alice;
    private const string Pat = "00000000-0000-4000-8000-0000000000a1";
    private const string Sam = "00000000-0000-4000-8000-0000000000a2";

    internal static ClientBinding Binding(
        string subject = Pat,
        ClientRole[]? roles = null,
        BindingStatus status = BindingStatus.Invited,
        string caseId = Case,
        string firmId = Ids.Firm) =>
        new(
            firmId,
            caseId,
            subject,
            $"{subject[^2..]}@example.test",
            "Pat Example",
            (roles ?? [ClientRole.Debtor1]).ToValueCollection(),
            status,
            Staff,
            new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));

    // ── The invitation body ─────────────────────────────────────────

    [Fact]
    public void An_invitation_defaults_to_the_first_debtor_and_lowercases_the_address()
    {
        var draft = ClientBindings.ParseInvitation(Body.Of("""{"email": "Pat@Example.TEST", "displayName": " Pat "}"""));
        Assert.Equal("pat@example.test", draft.Email);
        Assert.Equal("Pat", draft.DisplayName);
        Assert.Equal([ClientRole.Debtor1], draft.Roles);
    }

    [Fact]
    public void Both_roles_are_stored_in_canonical_order() =>
        Assert.Equal(
            [ClientRole.Debtor1, ClientRole.Debtor2],
            ClientBindings.ParseInvitation(Body.Of("""{"email": "pat@example.test", "displayName": "Pat", "roles": ["debtor_2", "debtor_1"]}""")).Roles);

    [Theory]
    [InlineData("[]")]
    [InlineData("""["non_filing_spouse"]""")]
    [InlineData("""["debtor_3"]""")]
    [InlineData("""["debtor_1", "debtor_1"]""")]
    [InlineData("\"debtor_1\"")]
    public void Roles_must_be_a_nonempty_set_of_the_two_debtors(string roles) =>
        Assert.Contains("roles", Assert.Throws<FieldValidationException>(() => ClientBindings.ParseInvitation(
            Body.Of($$"""{"email": "pat@example.test", "displayName": "Pat", "roles": {{roles}}}"""))).Fields.Keys);

    [Fact]
    public void A_body_naming_the_case_or_subject_is_refused_not_ignored() =>
        Assert.Throws<ValidationException>(() => ClientBindings.ParseInvitation(
            Body.Of($$"""{"email": "pat@example.test", "displayName": "Pat", "caseId": "{{OtherCase}}"}""")));

    [Fact]
    public void A_missing_name_and_address_are_both_reported() =>
        Assert.Equal(
            ["displayName", "email"],
            Assert.Throws<FieldValidationException>(() => ClientBindings.ParseInvitation(Body.Of("{}"))).Fields.Keys.Order(StringComparer.Ordinal));

    // ── Planning a binding ──────────────────────────────────────────

    [Fact]
    public void A_subject_with_a_live_binding_cannot_be_bound_again() =>
        Assert.Throws<ConflictException>(() => ClientBindings.PlanBinding(Binding(caseId: OtherCase), Binding(status: BindingStatus.Active), []));

    [Fact]
    public void A_revoked_subject_may_be_bound_again_a_refiled_case() =>
        Assert.Empty(ClientBindings.PlanBinding(Binding(caseId: OtherCase), Binding(status: BindingStatus.Revoked), []));

    [Fact]
    public void Inviting_the_second_spouse_narrows_the_first_binding()
    {
        var first = Binding(Pat, [ClientRole.Debtor1, ClientRole.Debtor2], BindingStatus.Active);
        var second = Binding(Sam, [ClientRole.Debtor2]);
        var narrowed = ClientBindings.PlanBinding(second, null, [first]);
        var only = Assert.Single(narrowed);
        Assert.Equal(Pat, only.Subject);
        Assert.Equal([ClientRole.Debtor1], only.Roles);
    }

    [Fact]
    public void A_narrowing_that_would_leave_a_binding_with_no_role_is_refused() =>
        Assert.Throws<ConflictException>(() => ClientBindings.PlanBinding(
            Binding(Sam, [ClientRole.Debtor2]), null, [Binding(Pat, [ClientRole.Debtor2], BindingStatus.Active)]));

    [Fact]
    public void Revoked_bindings_on_the_case_hold_nothing() =>
        Assert.Empty(ClientBindings.PlanBinding(Binding(Sam, [ClientRole.Debtor2]), null, [Binding(Pat, [ClientRole.Debtor2], BindingStatus.Revoked)]));

    // ── Transitions ─────────────────────────────────────────────────

    [Fact]
    public void Activation_moves_only_an_invitation()
    {
        Assert.Equal(BindingStatus.Active, ClientBindings.Activate(Binding()).Status);
        Assert.Equal(BindingStatus.Revoked, ClientBindings.Activate(Binding(status: BindingStatus.Revoked)).Status);
    }

    [Fact]
    public void Revoking_twice_is_a_conflict() =>
        Assert.Throws<ConflictException>(() => ClientBindings.Revoke(Binding(status: BindingStatus.Revoked)));

    [Fact]
    public void Create_binding_starts_invited()
    {
        var draft = ClientBindings.ParseInvitation(Body.Of("""{"email": "pat@example.test", "displayName": "Pat"}"""));
        var made = ClientBindings.CreateBinding(draft, Ids.Firm, Case, Pat, Staff);
        Assert.Equal((BindingStatus.Invited, Staff, Case), (made.Status, made.InvitedBy, made.CaseId));
    }

    // ── The neighbours ADR 0023 touches ─────────────────────────────

    [Theory]
    [InlineData(FirmRole.Attorney)]
    [InlineData(FirmRole.Paralegal)]
    [InlineData(FirmRole.Staff)]
    public void Client_portal_is_a_feature_hidden_from_every_role_by_default(FirmRole role)
    {
        Assert.Contains(Feature.ClientPortal, Wire.Members<Feature>());
        Assert.Equal(PermissionLevel.Hidden, Permissions.DefaultFor(role)[Feature.ClientPortal]);
    }

    [Fact]
    public void An_invitation_row_records_its_roles_and_other_rows_do_not()
    {
        var invite = AccessEvents.Record(Staff, AccessAction.ClientInvite, caseId: Case, roles: ["debtor_1", "debtor_2"]);
        var read = AccessEvents.Record(Pat, AccessAction.PortalRead, caseId: Case);
        Assert.Equal("debtor_1,debtor_2", invite.Roles);
        Assert.Null(read.Roles);
    }

    [Fact]
    public void A_client_accessor_answers_for_its_binding_alone()
    {
        var firm = new Firm(Ids.Firm, "Example", FirmStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var accessor = new ClientAccessor(firm, Binding(roles: [ClientRole.Debtor2]));
        Assert.Equal((Pat, Ids.Firm, Case), (accessor.Subject, accessor.FirmId, accessor.CaseId));
        Assert.Equal([ClientRole.Debtor2], accessor.Roles);
    }

    [Fact]
    public void Every_binding_enum_keeps_its_python_wire_value()
    {
        Assert.Equal(["debtor_1", "debtor_2"], Wire.Names<ClientRole>());
        Assert.Equal(["invited", "active", "revoked"], Wire.Names<BindingStatus>());
    }
}

// access.py had no suite of its own in insolvia_core (services/api's route
// tests exercised it); the one rule is pinned here, at the function.
public sealed class CaseAccessTests
{
    private static Accessor AccessorFor(bool isAdmin = false, bool accessAllCases = false) =>
        new(
            new Firm(Ids.Firm, "Example", FirmStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            FirmsTests.User(isAdmin: isAdmin, accessAllCases: accessAllCases));

    [Fact]
    public void Another_firms_case_is_invisible_to_everyone_admin_included() =>
        Assert.False(CaseAccess.MaySeeCase(AccessorFor(isAdmin: true), Ids.OtherFirm, caseDeleted: false, assigned: true));

    [Fact]
    public void A_deleted_case_is_invisible_to_everyone() =>
        Assert.False(CaseAccess.MaySeeCase(AccessorFor(isAdmin: true), Ids.Firm, caseDeleted: true, assigned: true));

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    public void In_the_firm_the_linkage_rule_decides(bool isAdmin, bool accessAllCases, bool assigned, bool visible) =>
        Assert.Equal(visible, CaseAccess.MaySeeCase(AccessorFor(isAdmin, accessAllCases), Ids.Firm, caseDeleted: false, assigned));

    [Fact]
    public void Feature_checks_go_through_the_permission_rule()
    {
        var staffAccessor = new Accessor(
            new Firm(Ids.Firm, "Example", FirmStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            FirmsTests.User(role: FirmRole.Staff, permissions: Permissions.DefaultFor(FirmRole.Staff)));
        Assert.True(staffAccessor.May(Feature.Documents, PermissionLevel.AddEdit));
        Assert.False(staffAccessor.May(Feature.Cases, PermissionLevel.AddEdit));
    }
}

// The access log's domain rules, from test_clients.py and test_firm_clients.py
// (their `access_item` assertions restated on the event; the item is 18.10's).
public sealed class AccessLogTests
{
    [Fact]
    public void A_client_access_row_is_keyed_by_the_client()
    {
        var accessEvent = AccessEvents.Record(Ids.Alice, AccessAction.ClientRead, clientId: "c-1");
        Assert.Equal(("c-1", (string?)null), (accessEvent.ClientId, accessEvent.CaseId));
        Assert.Equal("CLIENT#c-1", accessEvent.SubjectKey);
    }

    [Fact]
    public void A_case_access_row_is_unchanged_by_the_generalisation()
    {
        var accessEvent = AccessEvents.Record(Ids.Alice, AccessAction.CaseRead, caseId: "k-1");
        Assert.Equal(("k-1", (string?)null), (accessEvent.CaseId, accessEvent.ClientId));
        Assert.Equal("CASE#k-1", accessEvent.SubjectKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_access_row_names_exactly_one_subject(bool both)
    {
        var error = Assert.Throws<ArgumentException>(() => both
            ? AccessEvents.Record(Ids.Alice, AccessAction.CaseRead, caseId: "k-1", clientId: "c-1")
            : AccessEvents.Record(Ids.Alice, AccessAction.CaseRead));
        Assert.Contains("exactly one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_action_keeps_its_python_wire_value()
    {
        Assert.Equal(
            [
                "case.create", "case.read", "case.update", "document.create", "document.read", "document.download", "document.delete",
                "job.accept", "candidate.propose", "candidate.withdraw", "taxid.read", "client.invite", "client.revoke", "portal.read",
                "portal.answer", "client.create", "client.read", "client.update", "client.merge", "case.delete", "credential.enrol",
                "credential.revoke", "credential.open", "authorization.sign", "authorization.withdraw", "filing.approve", "filing.void",
                "filing.consume", "filing.submit", "filing.hand_back", "filing.resolve",

                // Python wrote these without listing them in ACTIONS (18.17).
                "packet.read", "packet.download", "packet.assemble", "petition.review", "document.extract",
                "extraction.read", "extraction.review",
            ],
            Wire.Names<AccessAction>());
        Assert.Equal(["allowed", "denied"], Wire.Names<AccessOutcome>());
        Assert.Equal(["CASE", "CLIENT", "CREDENTIAL", "AUTHORIZATION"], Wire.Names<AccessSubjectKind>());
    }
}
