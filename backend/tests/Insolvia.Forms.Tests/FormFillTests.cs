using System.Security.Cryptography;
using Insolvia.Engines.Projections;
using Insolvia.Forms.Tests.Support;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static Insolvia.Forms.Pdf.PdfObjects;

namespace Insolvia.Forms.Tests;

// test_form_fill.py's behaviour tests, translated: partial fills, the
// broken-group escape hatch, every structural refusal, the flat forms'
// overlay — plus the two decisions this port made (quirk 1: a button's /V is
// a name; quirk 3: text the font cannot encode is refused).
public sealed class FormFillTests
{
    private static FormRelease Latest(string series) => FormTemplates.Latest(series);

    [Fact]
    public void A_partial_fill_is_allowed_and_touches_only_its_fields()
    {
        var release = Latest("form/b106i");
        var data = FormFill.Fill(release, Values.Of(
            ("caption.debtor1_name", new TextFill("Ada Q Lovelace")),
            ("line_13_change_expected", new OptionFill("no"))));
        var result = FormReader.Read(release, data);

        Assert.Equal("Ada Q Lovelace", (string)result["Debtor 1"]!["value"]!);
        Assert.Equal("/no", (string)result["Check increase"]!["value"]!);
        var baseline = FormReader.Read(release, TemplatePdfs.For(release));
        var changed = result.Where(pair => !System.Text.Json.Nodes.JsonNode.DeepEquals(pair.Value, baseline[pair.Key])).Select(pair => pair.Key).Order(StringComparer.Ordinal);
        Assert.Equal(["Check increase", "Debtor 1"], changed);
    }

    [Fact]
    public void Widget_states_by_index_tick_boxes_without_a_field_value()
    {
        var release = Latest("form/b101");
        var data = FormFill.Fill(release, Values.Of(("line_15_debtor1_credit_counseling", WidgetStatesFill.ByIndexes(2))));
        var entry = FormReader.Read(release, data)["Check Box16"]!;

        Assert.Null(entry["value"]);
        Assert.Equal(["/Off", "/Off", "/On", "/Off"], entry["widget_states"]!.AsArray().Select(s => (string)s!));
    }

    [Fact]
    public void Widget_states_by_state_name()
    {
        var release = Latest("form/b101");
        var data = FormFill.Fill(release, Values.Of(("line_15_debtor1_credit_counseling", WidgetStatesFill.ByStates("2", "4"))));

        Assert.Equal(["/Off", "/2", "/Off", "/4"], FormReader.Read(release, data)["Check Box16"]!["widget_states"]!.AsArray().Select(s => (string)s!));
    }

    public static TheoryData<string, string, string> Refusals() => new()
    {
        { "form/b106i", "no_such_field|text|x", "not a field of" },
        { "form/b106i", "line_13_change_expected|option|Maybe", "not one of the PDF's states" },
        { "form/b106i", "caption.district|text|The Moon District", "not an option of" },
        { "form/b101", "line_3_debtor1_ssn_last4|text|123456789", "longer than the PDF's 4-character box" },
        { "form/b106i", "line_2_gross_wages|text|4,321.00", "address instances by PDF name" },
        { "form/b106i", "line_2_gross_wages|row:No such column|4,321.00", "not among this field's PDF fields" },
        { "form/b106i", "line_13_change_expected|text|No", "Text on a radio field" },
        { "form/b106i", "caption.amended_or_supplement|check|", "Check on a radio field" },
        { "form/b106i", "caption.debtor1_name|text|", "omit the field" },
        { "form/b101", "line_15_debtor1_credit_counseling|states|", "selects nothing" },
        { "form/b101", "line_15_debtor1_credit_counseling|states|5", "not one of the PDF's states" },
        { "form/b101", "caption.header_debtor1_name|states|On", "WidgetStates on a text field" },
        { "form/b2030", "line_1_fee_agreed|text|1,500,000,000,000.00", "points wide" },
        { "form/b2030", "line_1_fee_agreed|check|", "Check on a money field" },
        { "form/b2030", "line_2_source_paid_debtor|text|yes", "Text on a checkbox field" },
        { "form/b2030", "line_2_source_paid_debtor|option|On", "takes Text or Check" },
        { "form/b2030", "certification.firm_name|text|Counsel ☃ Counsel", "cannot draw" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Values_that_cannot_land_are_refused(string series, string spec, string problem)
    {
        var parts = spec.Split('|');
        FieldFill fill = parts[1] switch
        {
            "text" or "row:No such column" => new TextFill(parts[2]),
            "option" => new OptionFill(parts[2]),
            "check" => new CheckFill(),
            _ => parts[2].Length == 0 ? WidgetStatesFill.ByStates() : WidgetStatesFill.ByStates(parts[2]),
        };
        var values = new FieldValues();
        if (parts[1].StartsWith("row:", StringComparison.Ordinal))
        {
            values.Rows(parts[0])[parts[1]["row:".Length..]] = fill;
        }
        else
        {
            values[parts[0]] = fill;
        }

        var error = Assert.Throws<FormFillException>(() => FormFill.Fill(Latest(series), values));

        Assert.Contains(problem, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var error = Assert.Throws<FormFillException>(() => FormFill.Fill(Latest("form/b106i"), Values.Of(
            ("no_such_field", new TextFill("x")),
            ("line_13_change_expected", new OptionFill("Maybe")))));

        Assert.Equal(2, error.Problems.Count);
        Assert.Equal(error.Problems.Order(StringComparer.Ordinal), error.Problems);
    }

    [Fact]
    public void A_flat_notice_with_nothing_to_draw_is_the_template_verbatim()
    {
        var release = Latest("form/b2010");

        Assert.Equal(TemplatePdfs.For(release), FormFill.Fill(release, new FieldValues()));
    }

    [Fact]
    public void The_overlay_draws_text_and_ticks_into_an_appended_stream()
    {
        var release = Latest("form/b2030");
        var data = FormFill.Fill(release, Values.Of(
            ("line_1_fee_agreed", new TextFill("1,500.00")),
            ("line_2_source_paid_debtor", new CheckFill()),
            ("certification.firm_name", new TextFill("Counsel & Counsel (PA)"))));
        var fields = FormReader.Read(release, data);

        Assert.Equal("1,500.00", (string)fields["fee.agreed"]!["value"]!);
        Assert.Equal("X", (string)fields["source_paid.debtor"]!["value"]!);
        Assert.Equal("Counsel & Counsel (PA)", (string)fields["certification.firm_name"]!["value"]!);
        Assert.Null(fields["fee.received"]!["value"]);

        var filled = Open(data);
        var template = Open(TemplatePdfs.For(release));
        var (official, overlay) = FormReader.PageStreams(filled.Pages[0]);
        Assert.Equal(FormReader.PageStreams(template.Pages[0]).Official, official);
        Assert.NotNull(overlay);
        Assert.StartsWith("% insolvia-overlay\n", System.Text.Encoding.Latin1.GetString(overlay), StringComparison.Ordinal);
        Assert.DoesNotContain("certification.firm_name", System.Text.Encoding.Latin1.GetString(overlay), StringComparison.Ordinal);
        Assert.Contains("certification.firm_name", System.Text.Encoding.Latin1.GetString(FormReader.PageStreams(filled.Pages[1]).Overlay!), StringComparison.Ordinal);
        var fonts = Dict(Dict(filled.Pages[0].Elements["/Resources"])!.Elements["/Font"])!;
        Assert.Equal("/Helvetica", Str(Dict(fonts.Elements["/Helv"])!, "/BaseFont"));
    }

    [Fact]
    public void Quirk_1_a_button_fields_value_is_a_name_not_pypdfs_text_string()
    {
        var release = Latest("form/b101");
        var chapter = release.Fields.First(field => field.Type == "radio" && field.PdfNames.Count == 1);
        var export = release.Widgets[chapter.PdfNames[0]].States[0];
        var doc = Open(FormFill.Fill(release, Values.Of((chapter.Id, new OptionFill(export)))));
        var node = doc.Pages.Cast<PdfPage>().SelectMany(Widgets).Select(FieldOf).First(field => QualifiedName(field!) == chapter.PdfNames[0])!;

        Assert.IsType<PdfName>(Resolve(node.Elements["/V"]));
        Assert.Equal("/" + export, Str(node.Elements["/V"]));
    }

    [Fact]
    public void Non_ascii_text_in_the_court_fonts_encoding_fills()
    {
        var release = Latest("form/b106i");
        var data = FormFill.Fill(release, Values.Of(("caption.debtor1_name", new TextFill("José Núñez — Zoë Ångström, “Ł” €"))));

        Assert.Equal("José Núñez — Zoë Ångström, “Ł” €", (string)FormReader.Read(release, data)["Debtor 1"]!["value"]!);
    }

    [Fact]
    public void Quirk_3_text_the_court_font_cannot_encode_is_refused_naming_the_field()
    {
        var error = Assert.Throws<FormFillException>(() => FormFill.Fill(Latest("form/b106i"), Values.Of(("caption.debtor1_name", new TextFill("Gergő Ștefan")))));

        Assert.Single(error.Problems);
        Assert.StartsWith("caption.debtor1_name -> 'Debtor 1': 'Gergő Ștefan' has a character the form's Helvetica font cannot draw", error.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Two_fills_of_the_same_values_are_the_same_bytes_and_different_values_are_not()
    {
        var release = Latest("form/b106i");
        var a = FormFill.Fill(release, Values.Of(("caption.debtor1_name", new TextFill("Ada"))));
        var b = FormFill.Fill(release, Values.Of(("caption.debtor1_name", new TextFill("Ada"))));
        var c = FormFill.Fill(release, Values.Of(("caption.debtor1_name", new TextFill("Bea"))));

        Assert.Equal(SHA256.HashData(a), SHA256.HashData(b));
        Assert.NotEqual(SHA256.HashData(a), SHA256.HashData(c));
    }

    private static PdfDocument Open(byte[] data) => PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Import);
}
