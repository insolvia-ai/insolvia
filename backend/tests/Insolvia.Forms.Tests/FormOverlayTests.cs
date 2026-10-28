using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Engines.Projections;
using Insolvia.Forms.Tests.Support;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Insolvia.Forms.Tests;

// test_form_overlay.py, translated: the options model and its parsing, the
// /s/ signature and amended-caption augmentations, page selection and the
// stamps; plus pdf_draw's operators pinned against Python's own bytes.
public sealed class FormOverlayTests
{
    private static readonly FormRelease B101 = FormTemplates.Latest("form/b101");
    private static readonly FormRelease B106Dec = FormTemplates.Latest("form/b106dec");
    private static readonly FormRelease B106AB = FormTemplates.Latest("form/b106ab");
    private static readonly FormRelease B106I = FormTemplates.Latest("form/b106i");
    private static readonly FormRelease B2010 = FormTemplates.Latest("form/b2010");
    private static readonly FormRelease B2030 = FormTemplates.Latest("form/b2030");
    private static readonly FormRelease B121 = FormTemplates.Latest("form/b121");

    private static int PageCount(byte[] pdf) => PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import).PageCount;

    [Fact]
    public void Default_options_are_the_plain_filing_set()
    {
        Assert.True(new OutputOptions().IsDefault);
        Assert.False(new OutputOptions { PrintDate = true }.IsDefault);
        Assert.False(new OutputOptions { Forms = ValueCollections.Create(["b101"]) }.IsDefault);
    }

    [Fact]
    public void Parse_fills_in_every_default_and_reads_every_field()
    {
        Assert.Equal(new OutputOptions(), FormOverlay.ParseOutputOptions(null, allowForms: true));
        var parsed = FormOverlay.ParseOutputOptions(JsonNode.Parse("""{"draftWatermark":true,"printDate":true,"signaturePages":"only","signElectronically":true,"forms":["b101","b106ab"]}"""), allowForms: true);

        Assert.Equal(new OutputOptions { DraftWatermark = true, PrintDate = true, SignaturePages = SignaturePagesMode.Only, SignElectronically = true, Forms = ValueCollections.Create(["b101", "b106ab"]) }, parsed);
    }

    [Theory]
    [InlineData("[]", "options")]
    [InlineData("""{"draftWatermark":"yes"}""", "draftWatermark")]
    [InlineData("""{"signaturePages":"some"}""", "signaturePages")]
    [InlineData("""{"forms":[]}""", "forms")]
    [InlineData("""{"forms":["B101"]}""", "forms")]
    [InlineData("""{"forms":["b101","b101"]}""", "forms")]
    [InlineData("""{"surprise":true}""", "options")]
    [InlineData("""{"amendedOnly":1}""", "amendedOnly")]
    [InlineData("""{"amendedOnly":true,"forms":["b101"]}""", "amendedOnly")]
    public void A_malformed_options_body_is_refused_by_field(string raw, string field)
    {
        var error = Assert.Throws<FieldValidationException>(() => FormOverlay.ParseOutputOptions(JsonNode.Parse(raw), allowForms: true));

        Assert.Contains(field, error.Fields.Keys);
    }

    [Fact]
    public void Forms_is_refused_when_the_caller_does_not_allow_it()
    {
        var error = Assert.Throws<FieldValidationException>(() => FormOverlay.ParseOutputOptions(JsonNode.Parse("""{"forms":["b101"]}"""), allowForms: false));

        Assert.Equal("this endpoint already names one form; omit it.", error.Fields["forms"]);
    }

    [Fact]
    public void Output_options_json_round_trips_and_always_carries_every_key()
    {
        var options = new OutputOptions { DraftWatermark = true, SignaturePages = SignaturePagesMode.Omit, Forms = ValueCollections.Create(["b121"]) };
        var json = FormOverlay.OutputOptionsJson(options);

        Assert.Equal(options, FormOverlay.ParseOutputOptions(json, allowForms: true));
        Assert.Equal(["amendedOnly", "draftWatermark", "printDate", "signElectronically", "signaturePages"], FormOverlay.OutputOptionsJson(new OutputOptions()).Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(" FALSE ")]
    public void The_query_adapter_reads_false_spellings(string falsy) =>
        Assert.False(FormOverlay.ParseOutputOptionsQuery(new Dictionary<string, string> { ["draftWatermark"] = falsy }).DraftWatermark);

    [Fact]
    public void The_query_adapter_reads_booleans_and_mode_and_refuses_forms()
    {
        var parsed = FormOverlay.ParseOutputOptionsQuery(new Dictionary<string, string> { ["printDate"] = "1", ["signaturePages"] = "omit", ["amendedOnly"] = "true" });

        Assert.Equal(new OutputOptions { PrintDate = true, SignaturePages = SignaturePagesMode.Omit, AmendedOnly = true }, parsed);
        Assert.Throws<FieldValidationException>(() => FormOverlay.ParseOutputOptionsQuery(new Dictionary<string, string> { ["forms"] = "b101" }));
    }

    [Fact]
    public void Amendment_options_tick_the_caption_by_its_own_spelling()
    {
        var values = new FieldValues();

        Assert.Same(values, FormOverlay.ApplyAmendmentOptions(B106AB, values, amended: false));
        Assert.Equal(new CheckFill(), FormOverlay.ApplyAmendmentOptions(B106AB, values, amended: true)["caption.amended_filing"]);
        var radio = FormOverlay.ApplyAmendmentOptions(B106I, values, amended: true)["caption.amended_or_supplement"];
        var amended = B106I.Field("caption.amended_or_supplement").Options.Single(option => option.MapsToValue == "case.is_amended").Value;
        Assert.Equal(new OptionFill(amended), radio);
        Assert.Same(values, FormOverlay.ApplyAmendmentOptions(B121, values, amended: true));
        Assert.Empty(values);
    }

    [Fact]
    public void Select_pages_keeps_omits_or_refuses_by_the_signature_pages()
    {
        var template = TemplatePdfs.For(B101);

        Assert.Same(template, FormOverlay.SelectPages(template, B101, SignaturePagesMode.All));
        Assert.Equal(3, B101.SignaturePages.Count);
        Assert.Equal(3, PageCount(FormOverlay.SelectPages(template, B101, SignaturePagesMode.Only)!));
        Assert.Equal(PageCount(template) - 3, PageCount(FormOverlay.SelectPages(template, B101, SignaturePagesMode.Omit)!));
        Assert.Empty(B106AB.SignaturePages);
        Assert.Null(FormOverlay.SelectPages(TemplatePdfs.For(B106AB), B106AB, SignaturePagesMode.Only));
        Assert.Equal([1], B106Dec.SignaturePages);
        Assert.Null(FormOverlay.SelectPages(TemplatePdfs.For(B106Dec), B106Dec, SignaturePagesMode.Omit));
    }

    [Fact]
    public void Flat_releases_have_no_signature_pages_and_omit_keeps_them_whole()
    {
        Assert.Empty(B2010.SignaturePages);
        Assert.Empty(B2030.SignaturePages);
        Assert.Null(FormOverlay.SelectPages(TemplatePdfs.For(B2030), B2030, SignaturePagesMode.Only));
        Assert.Equal(PageCount(TemplatePdfs.For(B2030)), PageCount(FormOverlay.SelectPages(TemplatePdfs.For(B2030), B2030, SignaturePagesMode.Omit)!));
    }

    [Fact]
    public void Stamps_are_a_no_op_when_off_and_draw_on_every_page_when_on()
    {
        var template = TemplatePdfs.For(B101);

        Assert.Same(template, FormOverlay.StampPages(template, draftWatermark: false, printDate: false, DateTimeOffset.UtcNow));
        var stamped = PdfReader.Open(new MemoryStream(FormOverlay.StampPages(template, true, true, new DateTimeOffset(2026, 9, 23, 14, 30, 0, TimeSpan.Zero))), PdfDocumentOpenMode.Import);
        foreach (var page in stamped.Pages.Cast<PdfPage>())
        {
            var contents = Encoding.Latin1.GetString(PdfDraw.ConcatenatedContents(page)!);
            Assert.Contains("(DRAFT) Tj", contents, StringComparison.Ordinal);
            Assert.Contains("(Printed 2026-09-23 14:30 UTC) Tj", contents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Stamps_leave_the_field_values_untouched()
    {
        var filled = FormFill.Fill(B101, Synthetic.ValuesFor(B101));
        var stamped = FormOverlay.StampPages(filled, true, true, new DateTimeOffset(2026, 9, 23, 14, 30, 0, TimeSpan.Zero));

        Assert.True(JsonNode.DeepEquals(FormReader.Read(B101, filled), FormReader.Read(B101, stamped)));
    }

    [Fact]
    public void Apply_output_stamps_is_the_identity_for_the_default_options_and_null_when_nothing_survives()
    {
        var template = TemplatePdfs.For(B101);

        Assert.Same(template, FormOverlay.ApplyOutputStamps(template, B101, new OutputOptions(), DateTimeOffset.UtcNow));
        Assert.Null(FormOverlay.ApplyOutputStamps(TemplatePdfs.For(B106AB), B106AB, new OutputOptions { SignaturePages = SignaturePagesMode.Only }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Signature_options_sign_both_debtors_and_leave_the_attorney_wet()
    {
        var file = CaseFiles.ReferenceCh7;
        var values = new FieldValues();
        var today = new DateOnly(2026, 9, 3);

        Assert.Same(values, FormOverlay.ApplySignatureOptions(B101, values, file, new OutputOptions(), today));
        var signed = FormOverlay.ApplySignatureOptions(B101, values, file, new OutputOptions { SignElectronically = true }, today);
        Assert.Equal(new TextFill("/s/ Ada Quinn Lovelace"), signed["sign.debtor1_signature"]);
        Assert.Equal(new TextFill("/s/ Ben Lovelace Jr."), signed["sign.debtor2_signature"]);
        Assert.False(signed.ContainsKey("sign.debtor1_executed_on"));
        Assert.False(signed.ContainsKey("attorney.signature"));
        var dated = FormOverlay.ApplySignatureOptions(B101, values, file, new OutputOptions { SignElectronically = true, PrintDate = true }, today);
        Assert.Equal(new TextFill("09/03/2026"), dated["sign.debtor1_executed_on"]);
        Assert.Equal(new TextFill("09/03/2026"), dated["sign.debtor2_executed_on"]);
    }

    [Fact]
    public void Signature_options_leave_an_absent_debtor_wet_and_never_sign_a_flat_forms_box()
    {
        var file = CaseFiles.ReferenceCh7;
        var solo = file with { Debtors = [.. file.Debtors.Take(1)] };
        var options = new OutputOptions { SignElectronically = true };
        var signed = FormOverlay.ApplySignatureOptions(B106Dec, new FieldValues(), solo, options, new DateOnly(2026, 9, 3));

        Assert.True(signed.ContainsKey("debtor1_signature"));
        Assert.False(signed.ContainsKey("debtor2_signature"));
        Assert.Empty(FormOverlay.ApplySignatureOptions(B2010, new FieldValues(), file, options, new DateOnly(2026, 9, 3)));
        Assert.DoesNotContain(FormOverlay.ApplySignatureOptions(B2030, new FieldValues(), file, options, new DateOnly(2026, 9, 3)), pair => pair.Value is TextFill { Value: var v } && v.StartsWith("/s/", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_ops_are_pythons_bytes_and_replace_what_cp1252_cannot_carry()
    {
        // pdf_draw.text_ops("DRAFT (José) \\ ☃", x=134.64, y=237.6, size=97.92, rotate_degrees=45.0, gray=0.75)
        var expected = Encoding.Latin1.GetBytes("q\n0.707107 0.707107 -0.707107 0.707107 134.64 237.60 cm\n0.75 g\nBT\n/InsolviaOverlay 97.9 Tf\n0 0 Td\n(DRAFT \\(Jos\u00e9\\) \\\\ ?) Tj\nET\nQ\n");

        Assert.Equal(expected, PdfDraw.TextOps("DRAFT (José) \\ ☃", 134.64, 237.6, 97.92, 45.0, 0.75));
        Assert.Equal("Printed 2026-10-27 12:05 UTC", FormOverlay.StampText(new DateTimeOffset(2026, 10, 27, 12, 5, 0, TimeSpan.Zero)));
    }
}
