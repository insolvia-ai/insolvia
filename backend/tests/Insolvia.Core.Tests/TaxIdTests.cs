using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Debtors;
using Insolvia.Core.Memory;
using Insolvia.Core.TaxIds;

namespace Insolvia.Core.Tests;

/// <summary>
/// The debtor's tax identifier, translated from test_tax_ids.py. The cipher
/// is the KMS envelope's stand-in (<see cref="FakeCipher"/>): the AES-GCM
/// envelope itself is 18.12's, and its tests go with it.
/// </summary>
public sealed class TaxIdTests
{
    private const string Firm = "firm-0001";
    private const string OtherFirm = "firm-0002";
    private const string CaseId = "case-0001";
    private const string Ssn = "987654321";
    private const string Itin = "987654329";

    private readonly FakeCipher _cipher = new();
    private readonly MemoryTaxIdStore _store = new();
    private readonly MemoryAccessLog _log = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TaxIdInput? Parse(JsonNode? value)
    {
        var errors = new Dictionary<string, string>();
        var result = TaxIdRules.Parse(value, "tax_id", errors);
        return errors.Count > 0 ? throw new FieldValidationException(errors) : result;
    }

    private Task<TaxIdRef?> StoreAsync(TaxIdInput? given, TaxIdRef? existing = null) =>
        TaxIdRules.StoreAsync(given, existing, Firm, CaseId, _cipher, _store, cancellationToken: Ct);

    private Task<string?> ReadAsync(TaxIdRef reference, string firmId = Firm, string purpose = "b121") =>
        TaxIdRules.ReadAsync(reference, firmId, CaseId, FilingRole.Debtor1, "subject-0001", purpose, _cipher, _store, _log, Ct);

    // ── Parsing ─────────────────────────────────────────────────────

    [Fact]
    public void Absent_is_absent() => Assert.Null(Parse(null));

    [Theory]
    [InlineData("987-65-4321")]
    [InlineData("987654321")]
    [InlineData("987 65 4321")]
    public void Dashes_and_spaces_are_stripped(string spelling) =>
        Assert.Equal(new TaxIdInput(TaxIdKind.Ssn, Value: Ssn), Parse(new JsonObject { ["kind"] = "ssn", ["value"] = spelling }));

    [Fact]
    public void An_itin_is_nine_digits_starting_with_nine() =>
        Assert.Equal(new TaxIdInput(TaxIdKind.Itin, Value: Itin), Parse(Body.Of("""{"kind": "itin", "value": "987-65-4329"}""")));

    [Theory]
    [InlineData("ssn", "12345", "tax_id.value")]
    [InlineData("ssn", "987-65-432A", "tax_id.value")]
    [InlineData("ssn", "000-12-3456", "tax_id.value")]
    [InlineData("ssn", "666-12-3456", "tax_id.value")]
    [InlineData("ssn", "900-12-3456", "tax_id.value")]
    [InlineData("ssn", "987-65-4330", "tax_id.value")]
    [InlineData("ssn", "123-00-3456", "tax_id.value")]
    [InlineData("ssn", "123-45-0000", "tax_id.value")]
    [InlineData("itin", "123-45-6789", "tax_id.value")]
    [InlineData("itin", "987-66-4329", "tax_id.value")]
    [InlineData("itin", "987-89-4329", "tax_id.value")]
    [InlineData("ein", "987-65-4321", "tax_id.kind")]
    public void Rejects_a_value_that_is_not_the_claimed_kind(string kind, string value, string path) =>
        Assert.Contains(path, Assert.Throws<FieldValidationException>(() => Parse(new JsonObject { ["kind"] = kind, ["value"] = value })).Fields.Keys);

    [Theory]
    [InlineData("50")]
    [InlineData("65")]
    [InlineData("70")]
    [InlineData("88")]
    [InlineData("90")]
    [InlineData("92")]
    [InlineData("94")]
    [InlineData("99")]
    public void Accepts_every_itin_group_range_boundary(string group) =>
        Assert.NotNull(Parse(new JsonObject { ["kind"] = "itin", ["value"] = $"900{group}1234" }));

    [Fact]
    public void The_advertising_block_is_accepted_as_an_ssn()
    {
        for (var serial = 4320; serial < 4330; serial++)
        {
            Assert.NotNull(Parse(new JsonObject { ["kind"] = "ssn", ["value"] = $"98765{serial}" }));
        }
    }

    [Fact]
    public void The_last_four_alone_means_keep() =>
        Assert.Equal(new TaxIdInput(TaxIdKind.Ssn, LastFour: "4321"), Parse(Body.Of("""{"kind": "ssn", "last_four": "4321"}""")));

    [Theory]
    [InlineData("\"432\"")]
    [InlineData("\"43210\"")]
    [InlineData("\"abcd\"")]
    [InlineData("4321")]
    public void A_malformed_last_four_is_refused(string kept) =>
        Assert.Contains("tax_id.last_four", Assert.Throws<FieldValidationException>(() => Parse(new JsonObject { ["kind"] = "ssn", ["last_four"] = Body.Value(kept) })).Fields.Keys);

    [Fact]
    public void Both_or_neither_member_is_refused()
    {
        Assert.Contains("tax_id", Assert.Throws<FieldValidationException>(() => Parse(Body.Of("""{"kind": "ssn", "value": "987-65-4321", "last_four": "4321"}"""))).Fields.Keys);
        Assert.Contains("tax_id", Assert.Throws<FieldValidationException>(() => Parse(Body.Of("""{"kind": "ssn"}"""))).Fields.Keys);
    }

    [Fact]
    public void A_non_object_is_refused() =>
        Assert.Contains("tax_id", Assert.Throws<FieldValidationException>(() => Parse(JsonValue.Create("987-65-4321"))).Fields.Keys);

    // ── Printing ────────────────────────────────────────────────────

    [Fact]
    public void An_ssn_prints_with_dashes() => Assert.Equal("987-65-4321", TaxIdRules.FormatForB121(TaxIdKind.Ssn, Ssn));

    [Fact]
    public void An_itin_prints_the_ten_characters_after_the_preprinted_nine() =>
        Assert.Equal("87-65-4329", TaxIdRules.FormatForB121(TaxIdKind.Itin, Itin));

    // test_the_view_is_the_kind_and_the_last_four_only: the JSON view; dropped.

    // ── The write and the logged read ───────────────────────────────

    [Fact]
    public async Task A_number_is_sealed_and_the_debtor_gets_a_reference()
    {
        var reference = (await StoreAsync(new TaxIdInput(TaxIdKind.Ssn, Value: Ssn)))!;
        Assert.Equal((TaxIdKind.Ssn, "4321"), (reference.Kind, reference.LastFour));
        var sealedId = (await _store.GetAsync(CaseId, reference.Ref, Ct))!;
        Assert.DoesNotContain(Ssn, sealedId.ToString(), StringComparison.Ordinal);
        Assert.Equal(("4321", Firm), (sealedId.LastFour, sealedId.FirmId));
    }

    [Fact]
    public async Task The_logged_read_opens_the_number()
    {
        var reference = (await StoreAsync(new TaxIdInput(TaxIdKind.Ssn, Value: Ssn)))!;
        Assert.Equal(Ssn, await ReadAsync(reference));
    }

    [Fact]
    public async Task The_read_records_who_which_debtor_and_why()
    {
        var reference = (await StoreAsync(new TaxIdInput(TaxIdKind.Itin, Value: Itin)))!;
        await ReadAsync(reference, purpose: "b121");
        var recorded = Assert.Single(_log.Events);
        Assert.Equal(AccessAction.TaxIdRead, recorded.Action);
        Assert.Equal("taxid.read", recorded.Action.ToWire());
        Assert.Equal(("subject-0001", CaseId, "debtor_1", "b121"), (recorded.Principal, recorded.CaseId, recorded.FilingRole, recorded.Purpose));
    }

    [Fact]
    public void An_ordinary_row_carries_neither_extra_member()
    {
        var recorded = AccessEvents.Record("p", AccessAction.CaseRead, caseId: CaseId);
        Assert.Null(recorded.FilingRole);
        Assert.Null(recorded.Purpose);
    }

    [Fact]
    public async Task The_read_is_recorded_even_when_the_item_is_missing()
    {
        Assert.Null(await ReadAsync(new TaxIdRef(TaxIdKind.Ssn, "4321", "never-written")));
        Assert.Equal([AccessAction.TaxIdRead], _log.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task Another_firm_cannot_open_the_envelope()
    {
        var reference = (await StoreAsync(new TaxIdInput(TaxIdKind.Ssn, Value: Ssn)))!;
        await Assert.ThrowsAsync<CryptographicException>(() => ReadAsync(reference, firmId: OtherFirm));
    }

    [Fact]
    public async Task A_ciphertext_cannot_be_replayed_under_another_ref()
    {
        var reference = (await StoreAsync(new TaxIdInput(TaxIdKind.Ssn, Value: Ssn)))!;
        var sealedId = (await _store.GetAsync(CaseId, reference.Ref, Ct))!;
        await Assert.ThrowsAsync<CryptographicException>(() => _cipher.OpenAsync(sealedId.Envelope, TaxIdRules.EncryptionContext(Firm, "other-ref"), Ct));
    }

    [Fact]
    public async Task Keeping_the_stored_number_echoes_the_view()
    {
        var stored = await StoreAsync(new TaxIdInput(TaxIdKind.Ssn, Value: Ssn));
        Assert.Equal(stored, await StoreAsync(new TaxIdInput(TaxIdKind.Ssn, LastFour: "4321"), stored));
    }

    [Theory]
    [InlineData(TaxIdKind.Ssn, "9999")]
    [InlineData(TaxIdKind.Itin, "4321")]
    public async Task An_echo_that_does_not_match_is_refused(TaxIdKind kind, string lastFour)
    {
        var stored = await StoreAsync(new TaxIdInput(TaxIdKind.Ssn, Value: Ssn));
        var error = await Assert.ThrowsAsync<FieldValidationException>(() => StoreAsync(new TaxIdInput(kind, LastFour: lastFour), stored));
        Assert.Contains("tax_id", error.Fields.Keys);
    }

    [Fact]
    public async Task Keeping_with_nothing_stored_is_refused() =>
        await Assert.ThrowsAsync<FieldValidationException>(() => StoreAsync(new TaxIdInput(TaxIdKind.Ssn, LastFour: "4321")));

    [Fact]
    public async Task A_corrected_number_keeps_the_ref_and_replaces_the_item()
    {
        var first = (await StoreAsync(new TaxIdInput(TaxIdKind.Ssn, Value: "987654321")))!;
        var second = (await StoreAsync(new TaxIdInput(TaxIdKind.Ssn, Value: "987654322"), first))!;
        Assert.Equal(first.Ref, second.Ref);
        Assert.Equal("4322", second.LastFour);
        Assert.Equal("987654322", await ReadAsync(second));
        var sealedId = (await _store.GetAsync(CaseId, second.Ref, Ct))!;
        Assert.True(sealedId.CreatedAt <= sealedId.UpdatedAt);
    }

    [Fact]
    public async Task Clearing_leaves_the_sealed_item_in_place()
    {
        var stored = (await StoreAsync(new TaxIdInput(TaxIdKind.Ssn, Value: Ssn)))!;
        Assert.Null(await StoreAsync(null, stored));
        Assert.NotNull(await _store.GetAsync(CaseId, stored.Ref, Ct));
    }

    // test_the_item_round_trips_through_the_attribute_converter: the DynamoDB
    // item; dropped.

    [Fact]
    public void The_context_names_the_purpose_the_grant_conditions_on()
    {
        var context = TaxIdRules.EncryptionContext(Firm, "r1");
        Assert.Equal(
            new Dictionary<string, string> { ["purpose"] = TaxIdRules.Purpose, ["firm_id"] = Firm, ["tax_id_ref"] = "r1" }.ToValueDictionary(),
            context);
        Assert.Equal("debtor-tax-id", TaxIdRules.Purpose);
    }

    // The KMS cipher tests (FakeKms seal/open, the unwrap refusing another
    // context, the key alias) and the context_aad canonical-order assertion
    // are the envelope adapter's — 18.12 (Insolvia.Aws) ports them.

    [Fact]
    public void The_tax_id_kinds_keep_their_wire_strings() => Assert.Equal(["ssn", "itin"], Wire.Names<TaxIdKind>());
}
