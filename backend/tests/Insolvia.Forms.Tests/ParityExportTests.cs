using System.Globalization;
using System.Text.Json.Nodes;
using Insolvia.Engines.Projections;
using Insolvia.Forms.Tests.Support;

namespace Insolvia.Forms.Tests;

// The parity harness's .NET half. Parity/dump.py writes the cases (form
// values, packet inputs) and pypdf's renders under $INSOLVIA_FORMS_PARITY_DIR;
// this renders the same inputs into <dir>/dotnet/, and Parity/compare.py
// diffs fields, widget states, appearance streams, official pages, text runs
// and PDFium pixels. backend-pr.yml's "Forms parity" step runs all three;
// without the variable this test has nothing to do and skips.
public sealed class ParityExportTests
{
    private static string? Directory => Environment.GetEnvironmentVariable("INSOLVIA_FORMS_PARITY_DIR");

    [Fact]
    public void Renders_every_parity_input_the_python_half_wrote()
    {
        if (Directory is not { } root)
        {
            Assert.Skip("INSOLVIA_FORMS_PARITY_DIR is not set — Parity/run.sh sets it");
            return;
        }

        var inputs = System.IO.Directory.GetFiles(Path.Combine(root, "inputs"), "*.json").Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(inputs);
        var output = Path.Combine(root, "dotnet");
        System.IO.Directory.CreateDirectory(output);
        foreach (var path in inputs)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var input = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            switch ((string)input["kind"]!)
            {
                case "form":
                    var data = RenderForm(input);
                    Assert.Equal(data, RenderForm(input)); // deterministic
                    File.WriteAllBytes(Path.Combine(output, name + ".pdf"), data);
                    break;
                case "packet":
                    Parity.WritePacket(input, Path.Combine(output, name));
                    break;
                default:
                    throw new InvalidDataException(name);
            }
        }
    }

    internal static byte[] RenderForm(JsonObject input)
    {
        var release = FormTemplates.Get((string)input["series"]!, (string)input["release"]!);
        var data = FormFill.Fill(release, Values.FromJson(input["values"]!.AsObject()));
        if (input["stamps"] is JsonObject stamps)
        {
            var printedAt = DateTimeOffset.Parse((string)stamps["printedAt"]!, CultureInfo.InvariantCulture);
            data = FormOverlay.StampPages(data, draftWatermark: true, printDate: true, printedAt);
        }

        return data;
    }
}
