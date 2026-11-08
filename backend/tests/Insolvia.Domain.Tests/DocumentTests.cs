using System.Text.Json.Nodes;
using Insolvia.Core.Documents;

namespace Insolvia.Core.Tests;

/// <summary>Document metadata, translated from test_documents.py.</summary>
public sealed class DocumentTests
{
    private const string CaseId = "00000000-0000-4000-8000-0000000000ca";
    private const string RequestId = "00000000-0000-4000-8000-0000000000e1";
    private const string DocumentId = "00000000-0000-4000-8000-0000000000d0";

    public static TheoryData<string, string> Refusals() => new()
    {
        { "kind", "null" },
        { "kind", "\"wedding_photos\"" },
        { "kind", "7" },
        { "fileName", "null" },
        { "fileName", "\"   \"" },
        { "fileName", new JsonArray(new string('x', DocumentRules.MaxFileName + 1))[0]!.ToJsonString() },
        { "fileName", "\"../../etc/passwd\"" },
        { "fileName", "\"sub\\\\dir.pdf\"" },
        { "fileName", "\"line\\nbreak.pdf\"" },
        { "fileName", "\"invoice\\u202efdp.exe\"" },
        { "fileName", "\"report\\u202dsomething.pdf\"" },
        { "fileName", "\"invoice\\u2067fdp.exe\"" },
        { "fileName", "\"state\\u200bment.pdf\"" },
        { "fileName", "\"state\\u200dment.pdf\"" },
        { "fileName", "\"\\ufeffstatement.pdf\"" },
        { "fileName", "\"soft\\u00adhyphen.pdf\"" },
        { "fileName", "\".\"" },
        { "fileName", "\"..\"" },
        { "fileName", "\"  ..  \"" },
        { "contentType", "null" },
        { "contentType", "\"application/zip\"" },
        { "contentType", "\"image/svg+xml\"" },
        { "contentType", "\"text/html\"" },
        { "byteSize", "null" },
        { "byteSize", "0" },
        { "byteSize", "-1" },
        { "byteSize", (DocumentRules.MaxByteSize + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
        { "byteSize", "\"1024\"" },
        { "byteSize", "true" },
        { "provenance", """{"kind": {"source": "staff_typed"}}""" },
    };

    public static TheoryData<DocumentContentType> ContentTypes() => [.. Wire.Members<DocumentContentType>()];

    public static TheoryData<DocumentKind> Kinds() => [.. Wire.Members<DocumentKind>()];

    private static JsonObject Payload(Action<JsonObject>? overrides = null)
    {
        var payload = Body.Of("""{"kind": "pay_stub", "fileName": "statement.pdf", "contentType": "application/pdf", "byteSize": 1024}""");
        overrides?.Invoke(payload);
        return payload;
    }

    private static Document NewDocument(Action<JsonObject>? overrides = null) =>
        DocumentRules.Create(DocumentRules.ParseUpload(Payload(overrides)), CaseId, Ids.Alice);

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Upload_is_refused(string field, string value) =>
        Assert.Contains(field, Assert.Throws<FieldValidationException>(() => DocumentRules.ParseUpload(Payload(p => p[field] = Body.Value(value)))).Fields.Keys);

    [Fact]
    public void The_largest_accepted_document_is_accepted() =>
        Assert.Equal(DocumentRules.MaxByteSize, DocumentRules.ParseUpload(Payload(p => p["byteSize"] = DocumentRules.MaxByteSize)).ByteSize);

    [Theory]
    [MemberData(nameof(ContentTypes))]
    public void Every_allowlisted_content_type_is_accepted(DocumentContentType contentType) =>
        Assert.Equal(contentType, DocumentRules.ParseUpload(Payload(p => p["contentType"] = contentType.ToWire())).ContentType);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_is_accepted(DocumentKind kind) =>
        Assert.Equal(kind, DocumentRules.ParseUpload(Payload(p => p["kind"] = kind.ToWire())).Kind);

    [Fact]
    public void Content_type_is_normalised() =>
        Assert.Equal(DocumentContentType.Pdf, DocumentRules.ParseUpload(Payload(p => p["contentType"] = " Application/PDF ")).ContentType);

    [Fact]
    public void Unknown_keys_are_ignored() =>
        Assert.Equal("statement.pdf", DocumentRules.ParseUpload(Payload(p =>
        {
            p["sha256"] = "deadbeef";
            p["pageCount"] = 12;
        })).FileName);

    [Fact]
    public void The_object_key_is_case_and_document_id_only() =>
        Assert.Equal($"cases/{CaseId}/{DocumentId}", DocumentRules.ObjectKey(CaseId, DocumentId));

    [Fact]
    public void No_file_name_can_reach_the_object_key()
    {
        var document = NewDocument(p => p["fileName"] = "jane-smith-2023-tax-return.pdf");
        Assert.DoesNotContain("jane", document.StorageRef, StringComparison.Ordinal);
        Assert.DoesNotContain("smith", document.StorageRef, StringComparison.Ordinal);
        Assert.DoesNotContain(".pdf", document.StorageRef, StringComparison.Ordinal);
        Assert.Equal($"cases/{CaseId}/{document.Id}", document.StorageRef);
    }

    [Theory]
    [InlineData("../other-case")]
    [InlineData("smith-jane.pdf")]
    [InlineData("")]
    [InlineData("CASE#" + CaseId)]
    [InlineData(CaseId + "/x")]
    [InlineData(CaseId + "\n")]
    [InlineData(CaseId + "\r\n")]
    public void A_key_is_refused_for_anything_that_is_not_a_uuid(string caseId) =>
        Assert.Throws<ValidationException>(() => DocumentRules.ObjectKey(caseId, DocumentId));

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\n\n")]
    public void Neither_half_of_a_key_may_end_in_a_newline(string suffix) =>
        Assert.Throws<ValidationException>(() => DocumentRules.ObjectKey(CaseId, DocumentId + suffix));

    [Fact]
    public void No_accepted_key_can_contain_a_newline() =>
        Assert.DoesNotContain("\n", DocumentRules.ObjectKey(CaseId, DocumentId), StringComparison.Ordinal);

    [Fact]
    public void Create_stamps_server_identity()
    {
        var document = NewDocument();
        Assert.Equal((CaseId, Ids.Alice), (document.CaseId, document.UploadedBy));
        Assert.NotEmpty(document.Id);
        Assert.NotEqual(CaseId, document.Id);
        Assert.Equal(TimeSpan.Zero, document.UploadedAt.Offset);
        Assert.Equal(0, document.UploadedAt.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public void Two_documents_never_share_an_id()
    {
        var draft = DocumentRules.ParseUpload(Payload());
        var first = DocumentRules.Create(draft, CaseId, Ids.Alice);
        var second = DocumentRules.Create(draft, CaseId, Ids.Alice);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.StorageRef, second.StorageRef);
    }

    // test_item_round_trips, test_item_lands_in_the_cases_partition,
    // test_a_pending_item_carries_no_etag_attribute, test_a_confirmed_item_round_trips,
    // test_a_row_written_before_confirmation_existed_reads_as_pending,
    // test_from_item_rejects_a_malformed_row,
    // test_json_exposes_the_status_and_not_the_etag,
    // test_json_omits_the_uploader_and_the_object_key and
    // test_the_clients_view_of_an_upload_names_no_case: the item shape and the
    // two JSON views; dropped (18.10 and the API). A record's defaults —
    // pending, no etag, staff — are pinned below.

    [Fact]
    public void A_new_record_is_pending_and_has_no_etag()
    {
        var document = NewDocument();
        Assert.Equal(DocumentStatus.Pending, document.Status);
        Assert.Null(document.Etag);
    }

    [Fact]
    public void Confirming_replaces_the_claimed_size_with_the_real_one()
    {
        var document = NewDocument(p => p["byteSize"] = 2048);
        var confirmed = DocumentRules.Confirm(document, new StoredBlob(1999, "abc"));
        Assert.Equal((DocumentStatus.Stored, 1999L, "abc"), (confirmed.Status, confirmed.ByteSize, confirmed.Etag));
        Assert.Equal((document.Id, document.StorageRef, document.UploadedAt), (confirmed.Id, confirmed.StorageRef, confirmed.UploadedAt));
    }

    [Fact]
    public void Confirming_twice_produces_the_same_record()
    {
        var document = NewDocument();
        var blob = new StoredBlob(1999, "abc");
        Assert.Equal(DocumentRules.Confirm(document, blob), DocumentRules.Confirm(DocumentRules.Confirm(document, blob), blob));
    }

    [Fact]
    public void Ordering_is_by_time_then_id()
    {
        static Document At(string uploadedAt, string id) =>
            new(id, CaseId, DocumentKind.PayStub, "a.pdf", DocumentContentType.Pdf, 1, $"cases/{CaseId}/{id}", Ids.Alice, DateTimeOffset.Parse(uploadedAt, System.Globalization.CultureInfo.InvariantCulture));

        Document[] documents = [At("2026-01-01T00:00:00.000Z", "b"), At("2026-06-01T00:00:00.000Z", "a"), At("2026-01-01T00:00:00.000Z", "a")];
        Assert.Equal(
            [("2026-06-01", "a"), ("2026-01-01", "b"), ("2026-01-01", "a")],
            documents.Order(DocumentRules.ListOrder).Select(d => (d.UploadedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), d.Id)));
    }

    [Fact]
    public void An_expiry_is_a_utc_instant_in_the_future()
    {
        var now = DocumentRules.ExpiryTimestamp(0);
        var later = DocumentRules.ExpiryTimestamp(900);
        Assert.Equal(TimeSpan.Zero, later.Offset);
        Assert.True(later > now);
    }

    [Fact]
    public void A_staff_upload_is_the_default_channel_against_no_request()
    {
        var document = NewDocument();
        Assert.Equal((DocumentChannel.Staff, null), (document.Channel, document.RequestId));
    }

    [Fact]
    public void A_client_upload_carries_its_channel_and_request()
    {
        var document = DocumentRules.Create(DocumentRules.ParseUpload(Payload()), CaseId, Ids.Alice, DocumentChannel.Client, RequestId);
        Assert.Equal((DocumentChannel.Client, RequestId), (document.Channel, document.RequestId));
    }

    [Fact]
    public void A_server_decided_kind_overrides_the_payloads() =>
        Assert.Equal(DocumentKind.TaxReturn, DocumentRules.ParseUpload(Payload(p => p["kind"] = "wedding_photos"), DocumentKind.TaxReturn).Kind);

    [Fact]
    public void The_document_vocabularies_keep_their_wire_strings()
    {
        Assert.Equal(["application/pdf", "image/jpeg", "image/png", "image/heic", "image/tiff"], Wire.Names<DocumentContentType>());
        Assert.Equal(["pending", "stored"], Wire.Names<DocumentStatus>());
        Assert.Equal(["staff", "client"], Wire.Names<DocumentChannel>());
    }
}
