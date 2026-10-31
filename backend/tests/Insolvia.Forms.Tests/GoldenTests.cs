using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Engines.Projections;
using Insolvia.Forms.Tests.Support;
using PdfSharp.Pdf.IO;

namespace Insolvia.Forms.Tests;

// The fill engine's goldens (test_form_fill.py's, re-baselined): every form
// filled with the full-coverage synthetic values and pinned two ways —
//
//   1. template-exact: every page's official content is the court's own;
//   2. field- and byte-exact against the .NET goldens (Goldens/<form>.json):
//      the read-back, and the output's sha256 re-baselined over the .NET
//      bytes as ADR 0028 accepts. Their field read-backs were checked equal
//      to the Python goldens' (pypdf's) before 18.23 deleted those.
//
// REGENERATING: UPDATE_FORM_GOLDENS=1 dotnet test --filter GoldenTests
// rewrites Goldens/*.json in the source tree; review the diff field by field.
// A sha change with no field diff means serialisation moved (a PDFsharp
// change — which, pinned at 6.2.4, should never happen).
public sealed class GoldenTests
{
    public static TheoryData<string> Series() => [.. Synthetic.FormSeries];

    private static string GoldenPath(string form) => Path.Combine(AppContext.BaseDirectory, "Goldens", $"{form}.json");

    private static string SourceGoldenPath(string form) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Goldens", $"{form}.json"));

    private static byte[] Filled(string series, out FormRelease release)
    {
        release = FormTemplates.Latest(series);
        return FormFill.Fill(release, Synthetic.ValuesFor(release));
    }

    [Theory]
    [MemberData(nameof(Series))]
    public void The_filled_form_matches_its_dotnet_golden(string series)
    {
        var data = Filled(series, out var release);
        var observed = new JsonObject
        {
            ["fields"] = FormReader.Read(release, data),
            ["release"] = release.ReleaseId,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(data)),
        };
        if (Environment.GetEnvironmentVariable("UPDATE_FORM_GOLDENS") == "1")
        {
            File.WriteAllText(SourceGoldenPath(release.Form), Sorted(observed)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
            return;
        }

        var golden = JsonNode.Parse(File.ReadAllText(GoldenPath(release.Form)))!;
        Assert.Equal(release.ReleaseId, (string)golden["release"]!);

        // Field-exact first: on failure this names the box.
        Assert.True(JsonNode.DeepEquals(golden["fields"], observed["fields"]), $"{release.Form}: fields differ from the .NET golden");

        // Byte-exact second: alone, this means serialisation moved.
        Assert.Equal((string)golden["sha256"]!, (string)observed["sha256"]!);
    }

    [Theory]
    [MemberData(nameof(Series))]
    public void The_fill_is_deterministic_to_the_byte(string series)
    {
        var release = FormTemplates.Latest(series);
        var values = Synthetic.ValuesFor(release);

        Assert.Equal(FormFill.Fill(release, values), FormFill.Fill(release, values));
    }

    [Theory]
    [MemberData(nameof(Series))]
    public void The_fill_leaves_the_official_pages_untouched(string series)
    {
        var data = Filled(series, out var release);
        using var templateStream = new MemoryStream(TemplatePdfs.For(release));
        using var filledStream = new MemoryStream(data);
        var template = PdfReader.Open(templateStream, PdfDocumentOpenMode.Import);
        var filled = PdfReader.Open(filledStream, PdfDocumentOpenMode.Import);

        Assert.Equal(template.PageCount, filled.PageCount);
        for (var i = 0; i < template.PageCount; i++)
        {
            var before = FormReader.PageStreams(template.Pages[i]).Official.SelectMany(b => b).ToArray();
            var after = FormReader.PageStreams(filled.Pages[i]).Official.SelectMany(b => b).ToArray();
            Assert.Equal(before, after);
        }
    }

    [Theory]
    [MemberData(nameof(Series))]
    public void Every_fillable_box_is_exercised_and_filled(string series)
    {
        var data = Filled(series, out var release);
        var empty = FormReader.Read(release, data)
            .Where(pair => pair.Value!["value"] is null
                && (pair.Value["widget_states"] as JsonArray ?? []).All(state => (string)state! == "/Off"))
            .Select(pair => pair.Key)
            .ToList();

        Assert.Empty(empty);
    }

    [Fact]
    public void Every_dotnet_golden_names_a_form_series()
    {
        var goldens = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Goldens"), "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);

        Assert.Equal(Synthetic.FormSeries.Select(s => s["form/".Length..]).Order(StringComparer.Ordinal), goldens);
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject map => new JsonObject(map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Sorted(pair.Value)))),
        JsonArray list => new JsonArray([.. list.Select(Sorted)]),
        _ => node?.DeepClone(),
    };
}
