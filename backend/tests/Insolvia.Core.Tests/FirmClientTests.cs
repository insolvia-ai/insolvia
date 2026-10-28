using System.Text.Json.Nodes;
using Insolvia.Core.Fields;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;

namespace Insolvia.Core.Tests;

// Translated from test_firm_clients.py (and the parse half of
// test_client_funnel.py): a firm's client (ADR 0022) — parsing, the
// whole-record replace, the status write and the directory order. The item,
// wire and debtor-copy tests are listed in the report.
public sealed class FirmClientTests
{
    internal const string Full = """
        {"name": {"given": "Jordan", "middle": "Q", "surname": "Example", "suffix": "Jr."},
         "other_names_used": [{"id": "alias-1", "surname": "Sample"}],
         "date_of_birth": "1980-02-29",
         "residence_address": {"line1": "1 Example St", "city": "Springfield", "state": "IL", "postal_code": "62701", "county": "Sangamon"},
         "mailing_address": {"line1": "PO Box 1"},
         "phone": "555-0100", "mobile": "555-0101", "email": "jordan@example.test",
         "lead_source": "Referral", "referred_by": "A former client", "first_retained_at": "2026-09-01"}
        """;

    internal static FirmClient Make(string? payload = null, string firmId = Ids.Firm) =>
        FirmClientRules.Create(FirmClientRules.Parse(Body.Of(payload ?? Full)), firmId, Ids.Alice);

    private static JsonObject FullWith(string key, JsonNode? value)
    {
        var body = Body.Of(Full);
        body[key] = value;
        return body;
    }

    private static IReadOnlyDictionary<string, string> Refused(Action act) => Assert.Throws<FieldValidationException>(act).Fields;

    // ── Parsing ─────────────────────────────────────────────────────

    [Fact]
    public void A_name_is_required() => Assert.Contains("name", Refused(() => FirmClientRules.Parse(Body.Of("{}"))).Keys);

    [Theory]
    [InlineData("surname")]
    [InlineData("given")]
    public void Either_half_of_the_name_is_enough(string half)
    {
        var draft = FirmClientRules.Parse(Body.Of($$$"""{"name": {"{{{half}}}": "Example"}}"""));
        Assert.Equal("Example", half == "surname" ? draft.Name.Surname : draft.Name.Given);
    }

    [Fact]
    public void A_middle_name_or_suffix_alone_is_not_a_name() =>
        Assert.Contains("name", Refused(() => FirmClientRules.Parse(Body.Of("""{"name": {"middle": "Q", "suffix": "Jr."}}"""))).Keys);

    [Fact]
    public void Everything_but_the_name_is_optional()
    {
        var draft = FirmClientRules.Parse(Body.Of("""{"name": {"surname": "Example"}}"""));
        Assert.Equal(new Address(), draft.ResidenceAddress);
        Assert.Empty(draft.OtherNamesUsed);
        Assert.Null(draft.DateOfBirth);
        Assert.Null(draft.LeadSource);
    }

    [Fact]
    public void A_full_record_parses()
    {
        var draft = FirmClientRules.Parse(Body.Of(Full));
        Assert.Equal(new PersonName("Jordan", "Q", "Example", "Jr."), draft.Name);
        Assert.Equal("Sangamon", draft.ResidenceAddress.County);
        Assert.Equal("alias-1", draft.OtherNamesUsed[0].Id);
        Assert.Equal(new DateOnly(2026, 9, 1), draft.FirstRetainedAt);
    }

    [Fact]
    public void Dates_are_form_dates()
    {
        var body = FullWith("date_of_birth", "1980-02-30");
        body["first_retained_at"] = "Sept 1";
        Assert.Equal(["date_of_birth", "first_retained_at"], Refused(() => FirmClientRules.Parse(body)).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_date_of_birth_in_the_future_is_refused()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains("date_of_birth", Refused(() => FirmClientRules.Parse(FullWith("date_of_birth", "2026-10-28"), clock: clock)).Keys);
        Assert.Equal(new DateOnly(2026, 10, 27), FirmClientRules.Parse(FullWith("date_of_birth", "2026-10-27"), clock: clock).DateOfBirth);
    }

    [Fact]
    public void An_alias_needs_its_own_id_exactly_as_a_debtors_does() =>
        Assert.Contains("other_names_used[0].id", Refused(() => FirmClientRules.Parse(FullWith("other_names_used", Body.Value("""[{"surname": "Sample"}]""")))).Keys);

    [Theory]
    [InlineData("tax_id", """{"kind": "ssn", "value": "987-65-4320"}""")]
    [InlineData("tax_id_ref", "\"00000000-0000-4000-8000-0000000000ef\"")]
    [InlineData("tax_id_last_four", "\"4320\"")]
    public void The_tax_id_cannot_be_set_through_the_client_body(string key, string value) =>
        Assert.Contains(key, Refused(() => FirmClientRules.Parse(FullWith(key, Body.Value(value)))).Keys);

    [Fact]
    public void Server_owned_fields_in_the_body_are_ignored()
    {
        var body = Body.Of(Full);
        body["id"] = "x";
        body["status"] = "archived";
        body["created_by"] = "someone";
        body["prospect_stage"] = "possible";
        Assert.Equal(FirmClientRules.Parse(Body.Of(Full)), FirmClientRules.Parse(body));
    }

    // ── Transitions ─────────────────────────────────────────────────

    [Fact]
    public void A_new_client_is_active_and_stamped()
    {
        var client = Make();
        Assert.Equal(FirmClientStatus.Active, client.Status);
        Assert.Equal(Ids.Firm, client.FirmId);
        Assert.Equal(Ids.Alice, client.CreatedBy);
        Assert.Equal(client.CreatedAt, client.UpdatedAt);
        Assert.Null(client.TaxIdRef);
    }

    [Fact]
    public void A_replace_keeps_what_the_caller_does_not_own()
    {
        var client = Make() with { Status = FirmClientStatus.Archived, TaxIdRef = "00000000-0000-4000-8000-0000000000ef", TaxIdLastFour = "4320" };
        var replaced = FirmClientRules.Replace(client, FirmClientRules.Parse(Body.Of("""{"name": {"surname": "Renamed"}}""")));
        Assert.Equal((client.Id, client.FirmId, client.CreatedAt, client.CreatedBy), (replaced.Id, replaced.FirmId, replaced.CreatedAt, replaced.CreatedBy));
        Assert.Equal(FirmClientStatus.Archived, replaced.Status);
        Assert.Equal(client.TaxIdRef, replaced.TaxIdRef);
        Assert.Equal("4320", replaced.TaxIdLastFour);

        // Whole record: what the draft left out is cleared.
        Assert.Equal(new PersonName(Surname: "Renamed"), replaced.Name);
        Assert.Equal(new Address(), replaced.ResidenceAddress);
        Assert.Null(replaced.LeadSource);
    }

    [Fact]
    public void A_status_write_changes_the_status_and_nothing_else()
    {
        var client = Make();
        var archived = FirmClientRules.SetStatus(client, FirmClientStatus.Archived);
        Assert.Equal(FirmClientStatus.Archived, archived.Status);
        Assert.True(archived.Archived);
        Assert.Equal(client, archived with { Status = FirmClientStatus.Active, UpdatedAt = client.UpdatedAt });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"status": "deleted"}""")]
    [InlineData("""{"status": null}""")]
    public void The_status_must_be_one_of_the_two(string body) =>
        Assert.Contains("status", Refused(() => FirmClientRules.ParseClientStatus(Body.Of(body))).Keys);

    [Fact]
    public void The_directory_is_ordered_by_surname_then_given_ignoring_case()
    {
        var names = new[]
        {
            """{"name": {"surname": "zeta", "given": "A"}}""",
            """{"name": {"surname": "Adams", "given": "Bea"}}""",
            """{"name": {"surname": "adams", "given": "al"}}""",
            """{"name": {"given": "Mononym"}}""",
        };
        var ordered = FirmClientRules.Sorted(names.Select(name => Make(name)));
        Assert.Equal(
            [(null, "Mononym"), ("adams", "al"), ("Adams", "Bea"), ("zeta", "A")],
            ordered.Select(client => (client.Name.Surname, client.Name.Given)));
    }

    [Fact]
    public void The_identity_fields_are_ones_a_debtor_accepts()
    {
        // ADR 0022 copies a client's identity onto a debtor; a value the
        // client record accepts must be one the debtor's own parser accepts.
        var full = Body.Of(Full);
        var copied = new JsonObject();
        foreach (var key in FirmClients.ClientCopies.CopiedFields)
        {
            if (full[key] is { } value)
            {
                copied[key] = value.DeepClone();
            }
        }

        _ = Debtors.DebtorRules.Parse(copied, enforceProvenance: false);
    }

    // ── The feature ─────────────────────────────────────────────────

    [Fact]
    public void Clients_is_a_feature_with_adr_0022s_defaults()
    {
        Assert.Contains(Feature.Clients, Wire.Members<Feature>());
        Assert.Equal(PermissionLevel.AddEdit, Permissions.DefaultFor(FirmRole.Attorney)[Feature.Clients]);
        Assert.Equal(PermissionLevel.AddEdit, Permissions.DefaultFor(FirmRole.Paralegal)[Feature.Clients]);
        Assert.Equal(PermissionLevel.ViewOnly, Permissions.DefaultFor(FirmRole.Staff)[Feature.Clients]);
    }

    [Fact]
    public void An_existing_row_without_the_feature_has_it_hidden()
    {
        var permissions = Permissions.DefaultFor(FirmRole.Attorney).Where(pair => pair.Key != Feature.Clients).ToValueDictionary();
        Assert.Equal(PermissionLevel.Hidden, Permissions.PermissionFor(FirmsTests.User(permissions: permissions), Feature.Clients));
    }

    // ── The merge and funnel bodies (test_client_merge.py, test_client_funnel.py) ─

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"merged_client_id": " "}""")]
    [InlineData("""{"merged_client_id": 7}""")]
    public void A_merge_body_must_name_the_merged_client(string payload) =>
        Assert.Contains("merged_client_id", Refused(() => FirmClientRules.ParseClientMerge(Body.Of(payload))).Keys);

    [Fact]
    public void A_merge_body_names_the_merged_client() =>
        Assert.Equal("client-b", FirmClientRules.ParseClientMerge(Body.Of("""{"merged_client_id": " client-b "}""")));

    [Fact]
    public void A_client_is_a_prospect_until_retained()
    {
        Assert.True(Make("""{"name": {"surname": "Example"}}""").Prospect);
        Assert.False(Make("""{"name": {"surname": "Example"}, "first_retained_at": "2026-07-20"}""").Prospect);
    }

    [Theory]
    [InlineData("\"possible\"", ProspectStage.Possible)]
    [InlineData("\"consultation_scheduled\"", ProspectStage.ConsultationScheduled)]
    [InlineData("\"awaiting_signed_agreement\"", ProspectStage.AwaitingSignedAgreement)]
    [InlineData("\"exhausted\"", ProspectStage.Exhausted)]
    [InlineData("null", null)]
    public void A_stage_or_null_parses(string stage, ProspectStage? expected) =>
        Assert.Equal(expected, FirmClientRules.ParseProspectStage(Body.Of($$"""{"prospect_stage": {{stage}}}""")));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"prospect_stage": "won"}""")]
    [InlineData("""{"prospect_stage": 3}""")]
    public void An_unknown_or_missing_stage_is_refused(string payload) =>
        Assert.Throws<FieldValidationException>(() => FirmClientRules.ParseProspectStage(Body.Of(payload)));

    [Fact]
    public void A_refused_link_reads_like_the_read_check()
    {
        Assert.Equal("No such client.", FirmClientRules.RefusalForRefusedLink(null));
        Assert.Equal(FirmClientRules.LinkRaced, FirmClientRules.RefusalForRefusedLink(Make()));
        Assert.Equal("That client is archived — restore them first.", FirmClientRules.RefusalForRefusedLink(Make() with { Status = FirmClientStatus.Archived }));
    }

    [Fact]
    public void Every_client_enum_keeps_its_python_wire_value()
    {
        Assert.Equal(["active", "archived"], Wire.Names<FirmClientStatus>());
        Assert.Equal(["possible", "consultation_scheduled", "awaiting_signed_agreement", "exhausted"], Wire.Names<ProspectStage>());
        Assert.Equal(["debtor_1", "debtor_2", "non_filing_spouse"], Wire.Names<Debtors.FilingRole>());
        Assert.Equal(["written", "absent", "client_taken"], Wire.Names<Debtors.RepointOutcome>());
    }
}
