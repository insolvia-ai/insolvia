using Insolvia.Core.Firms;

namespace Insolvia.Core.Tests;

// Translated from test_firms.py: the firm domain's entities, parsing and
// permissions. The stored-item and API-representation tests are not here —
// see the report: the item shape is 18.10's, the JSON the API's.
public sealed class FirmsTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static FirmUser User(
        string subject = Ids.Alice,
        string firmId = Ids.Firm,
        string firstName = "Alice",
        string lastName = "Attorney",
        FirmRole role = FirmRole.Attorney,
        bool isAdmin = false,
        bool accessAllCases = false,
        ValueDictionary<Feature, PermissionLevel>? permissions = null,
        FirmUserStatus status = FirmUserStatus.Active) =>
        new(
            firmId,
            subject,
            "alice@example.test",
            firstName,
            lastName,
            role,
            isAdmin,
            accessAllCases,
            permissions ?? Permissions.DefaultFor(FirmRole.Attorney),
            status,
            Stamp,
            Stamp);

    private static ValueDictionary<Feature, PermissionLevel> Map(params (Feature Feature, PermissionLevel Level)[] pairs) =>
        pairs.Select(pair => new KeyValuePair<Feature, PermissionLevel>(pair.Feature, pair.Level)).ToValueDictionary();

    private static IReadOnlyDictionary<string, string> Refused(Action act) => Assert.Throws<FieldValidationException>(act).Fields;

    // ── Permissions ─────────────────────────────────────────────────

    [Fact]
    public void An_unknown_feature_is_hidden()
    {
        var without = User(permissions: Map((Feature.Cases, PermissionLevel.AddEdit)));
        Assert.Equal(PermissionLevel.Hidden, Permissions.PermissionFor(without, Feature.ExtractionReview));
        Assert.False(Permissions.Permits(without, Feature.ExtractionReview, PermissionLevel.ViewOnly));
    }

    [Fact]
    public void A_level_this_version_does_not_know_is_hidden()
    {
        // Python ranked an unknown STORED level as hidden in permission_for.
        // A PermissionLevel cannot hold one, so the rule lives where a wire
        // value becomes a member: an unknown level never parses.
        Assert.False(Wire.TryParse<PermissionLevel>("full_control", out _));
        Assert.Equal(PermissionLevel.Hidden, Permissions.PermissionFor(User(permissions: Map((Feature.Cases, (PermissionLevel)7))), Feature.Cases));
    }

    [Fact]
    public void A_disabled_user_has_no_permissions()
    {
        var disabled = User(status: FirmUserStatus.Disabled, isAdmin: true);
        Assert.Equal(PermissionLevel.Hidden, Permissions.PermissionFor(disabled, Feature.Cases));
        Assert.False(Permissions.Permits(disabled, Feature.Cases, PermissionLevel.ViewOnly));
    }

    [Fact]
    public void An_admin_gets_add_edit_on_everything()
    {
        var admin = User(isAdmin: true, permissions: Map());
        Assert.All(Wire.Members<Feature>(), feature => Assert.Equal(PermissionLevel.AddEdit, Permissions.PermissionFor(admin, feature)));
    }

    [Theory]
    [InlineData(PermissionLevel.AddEdit, PermissionLevel.AddEdit, true)]
    [InlineData(PermissionLevel.AddEdit, PermissionLevel.ViewOnly, true)]
    [InlineData(PermissionLevel.AddEdit, PermissionLevel.Hidden, true)]
    [InlineData(PermissionLevel.ViewOnly, PermissionLevel.AddEdit, false)]
    [InlineData(PermissionLevel.ViewOnly, PermissionLevel.ViewOnly, true)]
    [InlineData(PermissionLevel.Hidden, PermissionLevel.ViewOnly, false)]
    [InlineData(PermissionLevel.Hidden, PermissionLevel.AddEdit, false)]
    public void Levels_are_ordered_weakest_to_strongest(PermissionLevel held, PermissionLevel required, bool allowed) =>
        Assert.Equal(allowed, Permissions.Permits(User(permissions: Map((Feature.Cases, held))), Feature.Cases, required));

    [Fact]
    public void Levels_are_ranked_in_the_documented_order() =>
        Assert.Equal([PermissionLevel.Hidden, PermissionLevel.ViewOnly, PermissionLevel.AddEdit], Wire.Members<PermissionLevel>());

    [Fact]
    public void Permits_refuses_a_requirement_it_cannot_rank() =>
        Assert.Throws<ValidationException>(() => Permissions.Permits(User(), Feature.Cases, (PermissionLevel)99));

    [Theory]
    [InlineData(FirmRole.Attorney)]
    [InlineData(FirmRole.Paralegal)]
    [InlineData(FirmRole.Staff)]
    public void No_role_gets_firm_administration_by_default(FirmRole role) =>
        Assert.Equal(PermissionLevel.Hidden, Permissions.DefaultFor(role)[Feature.FirmAdministration]);

    [Fact]
    public void Staff_defaults_are_read_only_on_the_case_record()
    {
        var staff = Permissions.DefaultFor(FirmRole.Staff);
        Assert.Equal(PermissionLevel.ViewOnly, staff[Feature.Cases]);
        Assert.Equal(PermissionLevel.ViewOnly, staff[Feature.Intake]);
        Assert.Equal(PermissionLevel.AddEdit, staff[Feature.Documents]);
    }

    [Fact]
    public void Attorney_and_paralegal_defaults_are_identical() =>
        Assert.Equal(Permissions.DefaultFor(FirmRole.Attorney), Permissions.DefaultFor(FirmRole.Paralegal));

    [Theory]
    [InlineData(FirmRole.Attorney)]
    [InlineData(FirmRole.Paralegal)]
    [InlineData(FirmRole.Staff)]
    public void Every_role_default_names_every_feature(FirmRole role) =>
        Assert.Equal(Wire.Members<Feature>().ToHashSet(), Permissions.DefaultFor(role).Keys.ToHashSet());

    // ── Parsing ─────────────────────────────────────────────────────

    [Fact]
    public void A_firm_needs_a_name() => Assert.Contains("name", Refused(() => FirmParser.ParseFirmCreation(Body.Of("{}"))).Keys);

    [Fact]
    public void A_firm_starts_active()
    {
        var firm = FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(Body.Of("""{"name": "  Example & Partners  "}""")));
        Assert.Equal("Example & Partners", firm.Name);
        Assert.Equal(FirmStatus.Active, firm.Status);
    }

    [Fact]
    public void Creating_a_user_fills_in_the_role_defaults()
    {
        var draft = FirmParser.ParseFirmUserCreation(Body.Of("""{"email": "Bob@Example.test", "firstName": "Bob", "lastName": "Bobson", "role": "staff"}"""));
        Assert.Equal("bob@example.test", draft.Email);
        Assert.Equal(Permissions.DefaultFor(FirmRole.Staff), draft.Permissions);
        Assert.False(draft.IsAdmin);
        Assert.False(draft.AccessAllCases);
    }

    [Fact]
    public void Supplied_permissions_merge_over_the_defaults_on_creation()
    {
        var draft = FirmParser.ParseFirmUserCreation(Body.Of("""
            {"email": "bob@example.test", "firstName": "Bob", "lastName": "Bobson", "role": "staff",
             "permissions": {"extraction_review": "view_only"}}
            """));
        Assert.Equal(PermissionLevel.ViewOnly, draft.Permissions[Feature.ExtractionReview]);
        Assert.Equal(PermissionLevel.AddEdit, draft.Permissions[Feature.Documents]);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("\"TRUE\"")]
    [InlineData("1")]
    [InlineData("\"yes\"")]
    [InlineData("null")]
    public void The_admin_flag_must_be_a_real_boolean(string value) =>
        Assert.Contains("isAdmin", Refused(() => FirmParser.ParseFirmUserCreation(Body.Of($$"""
            {"email": "bob@example.test", "firstName": "Bob", "lastName": "Bobson", "role": "staff", "isAdmin": {{value}}}
            """))).Keys);

    [Fact]
    public void An_unknown_feature_is_refused_rather_than_dropped() =>
        Assert.Contains("permissions", Refused(() => FirmParser.ParseFirmUserCreation(Body.Of("""
            {"email": "bob@example.test", "firstName": "Bob", "lastName": "Bobson", "role": "staff",
             "permissions": {"document": "add_edit"}}
            """))).Keys);

    [Fact]
    public void An_unknown_level_is_refused() =>
        Assert.Throws<FieldValidationException>(() => FirmParser.ParseFirmUserCreation(Body.Of("""
            {"email": "bob@example.test", "firstName": "Bob", "lastName": "Bobson", "role": "staff",
             "permissions": {"documents": "full_control"}}
            """)));

    [Fact]
    public void A_patch_replaces_the_permission_map()
    {
        var changes = FirmParser.ParseFirmUserUpdate(Body.Of("""{"permissions": {"cases": "view_only"}}"""));
        Assert.Equal(Map((Feature.Cases, PermissionLevel.ViewOnly)), changes.Permissions);
        var updated = FirmLifecycle.ApplyUserChanges(User(), changes);
        Assert.Equal(PermissionLevel.Hidden, Permissions.PermissionFor(updated, Feature.Documents));
    }

    [Fact]
    public void A_patch_cannot_change_an_email() =>
        Assert.Throws<ValidationException>(() => FirmParser.ParseFirmUserUpdate(Body.Of("""{"email": "elsewhere@example.test"}""")));

    [Fact]
    public void An_empty_patch_is_refused() => Assert.Throws<ValidationException>(() => FirmParser.ParseFirmUserUpdate(Body.Of("{}")));

    [Fact]
    public void Changing_a_role_leaves_the_permission_map_alone()
    {
        var tuned = User(
            role: FirmRole.Staff,
            permissions: Permissions.DefaultFor(FirmRole.Staff).Append(new(Feature.Cases, PermissionLevel.Hidden)).ToValueDictionary());
        var promoted = FirmLifecycle.ApplyUserChanges(tuned, FirmParser.ParseFirmUserUpdate(Body.Of("""{"role": "attorney"}""")));
        Assert.Equal(FirmRole.Attorney, promoted.Role);
        Assert.Equal(PermissionLevel.Hidden, Permissions.PermissionFor(promoted, Feature.Cases));
    }

    [Fact]
    public void A_self_update_renames_and_nothing_else()
    {
        var updated = FirmLifecycle.ApplyUserChanges(User(), FirmParser.ParseSelfUpdate(Body.Of("""{"firstName": "Robert", "lastName": "Attorney"}""")));
        Assert.Equal(("Robert", "Attorney"), (updated.FirstName, updated.LastName));
        Assert.Equal((User().Role, User().IsAdmin, User().Status), (updated.Role, updated.IsAdmin, updated.Status));
    }

    [Theory]
    [InlineData("""{"firstName": "Robert"}""", "Robert", "Attorney")]
    [InlineData("""{"lastName": "Barrister"}""", "Alice", "Barrister")]
    public void A_self_update_takes_either_half_alone(string payload, string first, string last)
    {
        var updated = FirmLifecycle.ApplyUserChanges(User(), FirmParser.ParseSelfUpdate(Body.Of(payload)));
        Assert.Equal((first, last), (updated.FirstName, updated.LastName));
    }

    [Fact]
    public void A_self_update_still_accepts_one_display_name()
    {
        var updated = FirmLifecycle.ApplyUserChanges(User(), FirmParser.ParseSelfUpdate(Body.Of("""{"displayName": "Robert Barrister"}""")));
        Assert.Equal(("Robert", "Barrister"), (updated.FirstName, updated.LastName));
    }

    [Fact]
    public void A_legacy_self_update_refuses_a_single_token_name() =>
        Assert.Contains("displayName", Refused(() => FirmParser.ParseSelfUpdate(Body.Of("""{"displayName": "Cher"}"""))).Keys);

    [Theory]
    [InlineData("""{"role": "attorney"}""")]
    [InlineData("""{"isAdmin": true}""")]
    [InlineData("""{"status": "disabled"}""")]
    [InlineData("{}")]
    public void A_self_update_without_a_name_is_refused(string payload) =>
        Assert.Throws<ValidationException>(() => FirmParser.ParseSelfUpdate(Body.Of(payload)));

    public static TheoryData<string, string> BadHalves()
    {
        var data = new TheoryData<string, string>();
        foreach (var field in new[] { "firstName", "lastName" })
        {
            foreach (var value in new[] { "\"\"", "\"   \"", "null", "7", "\"" + new string('x', 101) + "\"" })
            {
                data.Add(field, value);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BadHalves))]
    public void A_self_update_validates_each_half_like_any_other_name(string field, string value) =>
        Assert.Contains(field, Refused(() => FirmParser.ParseSelfUpdate(Body.Of($$"""{"{{field}}": {{value}}}"""))).Keys);

    [Fact]
    public void A_subject_must_be_a_cognito_sub()
    {
        var draft = FirmParser.ParseFirmUserCreation(Body.Of("""{"email": "bob@example.test", "firstName": "Bob", "lastName": "Bobson", "role": "staff"}"""));
        Assert.Throws<ValidationException>(() => FirmLifecycle.CreateFirmUser(draft, Ids.Firm, "USER#" + Ids.Bob));
    }

    // ── The name, and the migration under it ────────────────────────

    [Theory]
    [InlineData("Alice", "Attorney", "Alice Attorney")]
    [InlineData("Cher", "", "Cher")]
    [InlineData("", "Attorney", "Attorney")]
    [InlineData("", "", "")]
    public void Full_name_joins_and_drops_the_gap(string first, string last, string expected) =>
        Assert.Equal(expected, User(firstName: first, lastName: last).FullName);

    [Theory]
    [InlineData("Alice Attorney", "Alice", "Attorney")]
    [InlineData("Mary Anne Smith", "Mary Anne", "Smith")]
    [InlineData("Cher", "Cher", "")]
    [InlineData("", "", "")]
    [InlineData("   ", "", "")]
    [InlineData("  Alice   Attorney  ", "Alice", "Attorney")]
    public void Split_legacy_name_guesses_the_same_way_everywhere(string stored, string first, string last) =>
        Assert.Equal((first, last), FirmNames.SplitLegacyName(stored));

    // ── Provenance and status (#212) ────────────────────────────────

    [Fact]
    public void Provenance_is_recorded_when_the_creator_is_known()
    {
        var firm = FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(Body.Of("""{"name": "Example"}""")), Ids.Alice, "staff@example.test");
        Assert.Equal(Ids.Alice, firm.CreatedBy);
        Assert.Equal("staff@example.test", firm.CreatedByEmail);
    }

    [Fact]
    public void A_seeded_firm_has_no_provenance()
    {
        var firm = FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(Body.Of("""{"name": "Example"}""")));
        Assert.Null(firm.CreatedBy);
        Assert.Null(firm.CreatedByEmail);
    }

    [Fact]
    public void Suspending_a_firm_changes_status_and_refreshes_updated_at()
    {
        var firm = FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(Body.Of("""{"name": "Example"}""")));
        var suspended = FirmLifecycle.SetFirmStatus(firm, FirmStatus.Suspended);
        Assert.Equal(FirmStatus.Suspended, suspended.Status);
        Assert.True(suspended.UpdatedAt >= firm.UpdatedAt);
        Assert.Equal(firm.Id, suspended.Id);
    }

    [Fact]
    public void Resuspending_is_idempotent_rather_than_an_error()
    {
        var firm = FirmLifecycle.SetFirmStatus(FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(Body.Of("""{"name": "Example"}"""))), FirmStatus.Suspended);
        Assert.Equal(FirmStatus.Suspended, FirmLifecycle.SetFirmStatus(firm, FirmStatus.Suspended).Status);
    }

    [Fact]
    public void An_unknown_status_is_refused_before_it_can_be_stored()
    {
        Assert.False(Wire.TryParse<FirmStatus>("disabled", out _));
        var firm = FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(Body.Of("""{"name": "Example"}""")));
        Assert.Throws<ValidationException>(() => FirmLifecycle.SetFirmStatus(firm, (FirmStatus)9));
    }

    [Fact]
    public void A_firm_update_renames_and_keeps_provenance()
    {
        var firm = FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(Body.Of("""{"name": "Example"}""")), "staff-subject", "operator@example.test");
        var renamed = FirmLifecycle.ApplyFirmChanges(firm, FirmParser.ParseFirmUpdate(Body.Of("""{"name": "Example, LLP"}"""), FakeCourtRegistry.Instance));
        Assert.Equal("Example, LLP", renamed.Name);
        Assert.True(renamed.UpdatedAt >= firm.UpdatedAt);
        Assert.Equal((firm.Id, firm.Status, firm.CreatedBy, firm.CreatedByEmail), (renamed.Id, renamed.Status, renamed.CreatedBy, renamed.CreatedByEmail));
    }

    [Theory]
    [InlineData("""{"status": "suspended"}""")]
    [InlineData("""{"status": "active"}""")]
    [InlineData("{}")]
    public void A_firm_update_cannot_touch_status(string payload) =>
        Assert.Throws<ValidationException>(() => FirmParser.ParseFirmUpdate(Body.Of(payload), FakeCourtRegistry.Instance));

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    [InlineData("7")]
    public void A_firm_update_validates_the_name(string value) =>
        Assert.Contains("name", Refused(() => FirmParser.ParseFirmUpdate(Body.Of($$"""{"name": {{value}}}"""), FakeCourtRegistry.Instance)).Keys);

    [Fact]
    public void A_firm_update_validates_the_name_length() =>
        Assert.Contains("name", Refused(() => FirmParser.ParseFirmUpdate(Body.Of($$"""{"name": "{{new string('x', 201)}}"}"""), FakeCourtRegistry.Instance)).Keys);

    // ── Firm defaults and signature blocks (issue #360) ────────────

    [Fact]
    public void A_default_court_is_validated_against_the_registry()
    {
        var changes = FirmParser.ParseFirmUpdate(Body.Of("""{"defaultCourt": "flmb", "defaultDivision": "tampa"}"""), FakeCourtRegistry.Instance);
        Assert.NotNull(changes.DefaultCourt);
        Assert.Equal(new DefaultCourt("flmb", "tampa"), changes.DefaultCourt.Value);
    }

    [Theory]
    [InlineData("""{"defaultCourt": "nyeb", "defaultDivision": "brooklyn"}""", "defaultCourt")]
    [InlineData("""{"defaultCourt": "flmb", "defaultDivision": "miami"}""", "defaultDivision")]
    [InlineData("""{"defaultCourt": "flmb"}""", "defaultDivision")]
    [InlineData("""{"defaultDivision": "tampa"}""", "defaultCourt")]
    [InlineData("""{"defaultChapter": 9}""", "defaultChapter")]
    [InlineData("""{"defaultChapter": "7"}""", "defaultChapter")]
    [InlineData("""{"letterhead": "Example & Partners"}""", "letterhead")]
    [InlineData("""{"letterhead": {"phone": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}}""", "letterhead.phone")]
    public void A_bad_default_names_its_field(string payload, string field) =>
        Assert.Contains(field, Refused(() => FirmParser.ParseFirmUpdate(Body.Of(payload), FakeCourtRegistry.Instance)).Keys);

    [Fact]
    public void Defaults_are_applied_and_can_be_cleared()
    {
        var firm = FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(Body.Of("""{"name": "Example"}""")));
        var withDefaults = FirmLifecycle.ApplyFirmChanges(firm, FirmParser.ParseFirmUpdate(Body.Of("""
            {"defaultCourt": "flmb", "defaultDivision": "tampa", "defaultChapter": 13,
             "letterhead": {"name": "Example & Partners, P.A.", "address": {"line1": "1 Main St", "city": "Tampa", "state": "FL"}, "phone": "813-555-0100"}}
            """), FakeCourtRegistry.Instance));
        Assert.Equal(new DefaultCourt("flmb", "tampa"), withDefaults.DefaultCourt);
        Assert.Equal(13, withDefaults.DefaultChapter);
        Assert.NotNull(withDefaults.Letterhead);
        Assert.Equal("Tampa", withDefaults.Letterhead.Address.City);

        // A rename leaves every default alone: null means "leave unchanged".
        var renamed = FirmLifecycle.ApplyFirmChanges(withDefaults, FirmParser.ParseFirmUpdate(Body.Of("""{"name": "Ex"}"""), FakeCourtRegistry.Instance));
        Assert.Equal(withDefaults.DefaultCourt, renamed.DefaultCourt);

        // `null` clears — the one thing a bare null cannot express.
        var cleared = FirmLifecycle.ApplyFirmChanges(withDefaults, FirmParser.ParseFirmUpdate(
            Body.Of("""{"defaultCourt": null, "defaultDivision": null, "letterhead": null}"""), FakeCourtRegistry.Instance));
        Assert.Null(cleared.DefaultCourt);
        Assert.Null(cleared.Letterhead);
        Assert.Equal(13, cleared.DefaultChapter);
    }

    [Fact]
    public void A_signature_block_is_the_filing_professionals_shape()
    {
        var changes = FirmParser.ParseSelfUpdate(Body.Of("""
            {"signatureBlock": {"bar_number": "0123456", "bar_state": "FL", "firm_name": "Example & Partners",
             "address": {"line1": "1 Main St", "city": "Tampa", "state": "FL"}, "phone": "813-555-0100", "email": "alice@example.test"}}
            """));
        Assert.NotNull(changes.SignatureBlock);
        var block = changes.SignatureBlock.Value;
        Assert.NotNull(block);
        Assert.Equal("0123456", block.BarNumber);
        Assert.Equal(block, FirmLifecycle.ApplyUserChanges(User(), changes).SignatureBlock);
    }

    [Theory]
    [InlineData("""{"signatureBlock": "0123456"}""", "signatureBlock")]
    [InlineData("""{"signatureBlock": {"bar_state": "FLA"}}""", "signatureBlock.bar_state")]
    [InlineData("""{"signatureBlock": {"bar_number": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}}""", "signatureBlock.bar_number")]
    public void A_bad_signature_block_names_its_field(string payload, string field) =>
        Assert.Contains(field, Refused(() => FirmParser.ParseFirmUserUpdate(Body.Of(payload))).Keys);

    [Fact]
    public void A_signature_block_can_be_cleared_by_admin_or_self()
    {
        var withBlock = FirmLifecycle.ApplyUserChanges(User(), FirmParser.ParseFirmUserUpdate(Body.Of("""{"signatureBlock": {"bar_number": "1"}}""")));
        Assert.NotNull(withBlock.SignatureBlock);
        var cleared = FirmLifecycle.ApplyUserChanges(withBlock, FirmParser.ParseSelfUpdate(Body.Of("""{"signatureBlock": null}""")));
        Assert.Null(cleared.SignatureBlock);
    }

    // ── The admin invariant (no Python test; the rule is firms.py's) ─

    [Fact]
    public void Demoting_the_last_active_admin_leaves_no_admin()
    {
        var admin = User(isAdmin: true);
        var colleague = User(subject: Ids.Bob);
        Assert.True(Permissions.WouldLeaveNoAdmin([admin, colleague], changed: admin with { IsAdmin = false }));
        Assert.True(Permissions.WouldLeaveNoAdmin([admin, colleague], removed: admin.Subject));
        Assert.False(Permissions.WouldLeaveNoAdmin([admin, colleague with { IsAdmin = true }], changed: admin with { IsAdmin = false }));
        Assert.True(Permissions.WouldLeaveNoAdmin([admin, colleague with { IsAdmin = true, Status = FirmUserStatus.Disabled }], removed: admin.Subject));
    }

    // ── Wire values ─────────────────────────────────────────────────

    [Fact]
    public void Every_firm_enum_keeps_its_python_wire_value()
    {
        Assert.Equal(["attorney", "paralegal", "staff"], Wire.Names<FirmRole>());
        Assert.Equal(["active", "suspended"], Wire.Names<FirmStatus>());
        Assert.Equal(["active", "disabled"], Wire.Names<FirmUserStatus>());
        Assert.Equal(["hidden", "view_only", "add_edit"], Wire.Names<PermissionLevel>());
        Assert.Equal(
            ["cases", "intake", "documents", "extraction_review", "creditor_library", "notes", "events", "tasks", "clients", "client_portal", "electronic_filing", "firm_administration"],
            Wire.Names<Feature>());
    }
}
