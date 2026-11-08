using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Forms.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static Insolvia.Forms.Pdf.PdfObjects;

namespace Insolvia.Forms.Tests;

// The appearance generator against pypdf on what no court form exercises:
// Fixtures/edge.pdf (written by Parity/edge_fixture.py) carries list boxes,
// a rotated widget, Times-Roman and a font with its own /Widths in /DR, a
// /DA font missing from /DR, alignment, comb cells, multiline, borders and
// non-ASCII text; Fixtures/edge.expected.json is pypdf 6.16.2's fill of it.
// Every stream must come out byte for byte.
public sealed class AppearanceTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly JsonObject Expected = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "edge.expected.json")))!.AsObject();

    private static readonly Lazy<PdfDocument> Filled = new(() =>
    {
        var values = Expected["values"]!.AsObject().Select(pair => (pair.Key, (string)pair.Value!)).ToList();
        var data = FormFill.FillFields(File.ReadAllBytes(Path.Combine(Fixtures, "edge.pdf")), values);
        return PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Import);
    });

    public static TheoryData<string> WidgetNames() => [.. Expected["values"]!.AsObject().Select(pair => pair.Key)];

    [Theory]
    [MemberData(nameof(WidgetNames))]
    public void The_appearance_stream_is_pypdfs_byte_for_byte(string name)
    {
        var expected = Expected["appearances"]![name]!;
        var widget = PdfObjects.Widgets(Filled.Value.Pages[0]).Single(w => Str(w, "/T") == name);
        var normal = Dict(Dict(widget.Elements["/AP"])!.Elements["/N"])!;

        Assert.Equal((string)expected["stream"]!, Encoding.Latin1.GetString(Decoded(normal)));
        Assert.Equal(expected["bbox"]!.AsArray().Select(v => (double)v!), Array(normal.Elements["/BBox"])!.Elements.Select(Num));
        var matrix = Array(normal.Elements["/Matrix"])?.Elements.Select(Num).ToList();
        Assert.Equal(expected["matrix"] is JsonArray m ? m.Select(v => (double)v!).ToList() : null, matrix);
        Assert.Equal(expected["fonts"]!.AsArray().Select(v => (string)v!), Dict(Dict(normal.Elements["/Resources"])!.Elements["/Font"])!.Elements.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((string)expected["da"]!, Str(widget, "/DA"));
    }

    [Fact]
    public void A_font_missing_from_dr_is_added_to_it_as_pypdf_adds_it()
    {
        var acroForm = Dict(Filled.Value.Internals.Catalog.Elements["/AcroForm"])!;
        var fonts = Dict(Dict(acroForm.Elements["/DR"])!.Elements["/Font"])!;
        var actual = fonts.Elements.Keys.Order(StringComparer.Ordinal).ToDictionary(key => key, key => Str(Dict(fonts.Elements[key])!, "/BaseFont"));

        Assert.Equal(Expected["appearances"]!["$dr"]!.AsObject().ToDictionary(pair => pair.Key, pair => (string?)pair.Value), actual);
    }

    [Fact]
    public void Text_the_fields_font_cannot_encode_is_refused_not_drawn_as_a_question_mark()
    {
        var pdf = File.ReadAllBytes(Path.Combine(Fixtures, "edge.pdf"));

        var error = Assert.Throws<FormFillException>(() => FormFill.FillFields(pdf, [("plain", "Łódź Ćwierć ő")]));

        Assert.Contains("cannot draw", error.Message, StringComparison.Ordinal);
        Assert.Contains("ő", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(16.544000000000096, "16.544000000000096")]
    [InlineData(8.0, "8.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e16, "1e+16")]
    [InlineData(123456789012345.6, "123456789012345.6")]
    [InlineData(23.849999999999998, "23.849999999999998")]
    public void Floats_print_as_pythons_repr(double value, string text) => Assert.Equal(text, TypedNumber.FloatRepr(value));

    [Theory]
    [InlineData(0.15, 0.1)]
    [InlineData(0.25, 0.2)]
    [InlineData(0.35, 0.3)]
    [InlineData(2.675, 2.7)]
    [InlineData(15.649999999999999, 15.6)]
    [InlineData(-0.05, -0.1)]
    public void Round_1_rounds_the_exact_binary_value_half_to_even(double value, double rounded) => Assert.Equal(rounded, TypedNumber.Round(value, 1));

    [Fact]
    public void Python_ints_and_floats_keep_their_types_through_arithmetic()
    {
        var margin = TypedNumber.Max(TypedNumber.Int(1) * TypedNumber.Int(1), TypedNumber.Int(1));

        Assert.Equal("2", (TypedNumber.Int(2) * margin).ToString());
        Assert.Equal("1.0", TypedNumber.Max(TypedNumber.Float(1.0), TypedNumber.Int(1)).ToString());
        Assert.Equal("1", TypedNumber.Max(TypedNumber.Float(0.5), TypedNumber.Int(1)).ToString());
        Assert.Equal("0.5", (TypedNumber.Int(1) / TypedNumber.Int(2)).ToString());
        Assert.Equal("12", TypedNumber.Round1(TypedNumber.Int(12)).ToString());
    }
}
