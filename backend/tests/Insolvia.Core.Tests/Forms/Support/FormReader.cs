using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Engines.Projections;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static Insolvia.Forms.Pdf.PdfObjects;

namespace Insolvia.Forms.Tests.Support;

/// <summary>
/// The goldens' reader (test_form_fill.py's <c>read_form</c>), on PDFsharp:
/// every fillable field's /V, every button widget's /AS in page order, and
/// on a flat release every overlay box read back out of the overlay stream.
/// </summary>
internal static partial class FormReader
{
    private static readonly byte[] OverlayMarker = "% insolvia-overlay\n"u8.ToArray();

    [GeneratedRegex(@"% box:(?<name>\S+)(?<check> check)?\n(?:q 0 g 0 Tr 0 Tc 0 Tw 100 Tz 0 Ts BT /Helv [\d.]+ Tf 1 0 0 1 [\d.]+ [\d.]+ Tm \((?<text>(?:[^()\\]|\\.)*)\) Tj ET Q)?", RegexOptions.ECMAScript)]
    private static partial Regex OverlayBox();

    [GeneratedRegex(@"\\([0-7]{3}|.)", RegexOptions.Singleline)]
    private static partial Regex Escape();

    public static JsonObject Read(FormRelease release, byte[] data)
    {
        using var source = new MemoryStream(data, writable: false);
        var doc = PdfReader.Open(source, PdfDocumentOpenMode.Import);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var page in doc.Pages)
        {
            foreach (var widget in Widgets(page))
            {
                if (FieldOf(widget) is { } field && QualifiedName(field) is { } name && !values.ContainsKey(name))
                {
                    values[name] = field.Elements.ContainsKey("/V") ? TextValue(field.Elements["/V"]) : null;
                }
            }
        }

        var output = new JsonObject();
        foreach (var (name, widget) in release.Widgets)
        {
            if (widget.Kind == "pushbutton")
            {
                continue;
            }

            output[name] = new JsonObject { ["value"] = values.GetValueOrDefault(name) };
        }

        foreach (var page in doc.Pages)
        {
            foreach (var widget in Widgets(page))
            {
                if (QualifiedName(widget) is not { } name || output[name] is not JsonObject entry)
                {
                    continue;
                }

                if (release.Widgets[name].Kind is not ("checkbox" or "radio"))
                {
                    continue;
                }

                if (entry["widget_states"] is not JsonArray states)
                {
                    entry["widget_states"] = states = [];
                }

                states.Add(Str(widget.Elements["/AS"]) ?? "/Off");
            }
        }

        foreach (var name in release.Boxes.Keys)
        {
            output[name] = new JsonObject { ["value"] = null };
        }

        foreach (var page in doc.Pages)
        {
            var (_, overlay) = PageStreams(page);
            if (overlay is null)
            {
                continue;
            }

            foreach (Match match in OverlayBox().Matches(Encoding.Latin1.GetString(overlay)))
            {
                var name = match.Groups["name"].Value;
                if (match.Groups["check"].Success)
                {
                    output[name] = new JsonObject { ["value"] = "X" };
                }
                else if (match.Groups["text"].Success)
                {
                    output[name] = new JsonObject { ["value"] = Unescape(match.Groups["text"].Value) };
                }
            }
        }

        return output;
    }

    /// <summary>A page's official content streams, and the engine's overlay (the last stream, when it carries the marker).</summary>
    public static (List<byte[]> Official, byte[]? Overlay) PageStreams(PdfPage page)
    {
        var contents = Resolve(page.Elements["/Contents"]);
        var parts = contents is PdfArray array
            ? array.Elements.Select(item => Decoded(Dict(item)!)).ToList()
            : contents is PdfDictionary single ? [Decoded(single)] : [];
        if (parts.Count > 0 && parts[^1].AsSpan().StartsWith(OverlayMarker))
        {
            return (parts[..^1], parts[^1]);
        }

        return (parts, null);
    }

    private static string Unescape(string raw)
    {
        var bytes = new List<byte>();
        var text = Escape().Replace(raw, match =>
        {
            var body = match.Groups[1].Value;
            return body.All(char.IsAsciiDigit) ? ((char)Convert.ToInt32(body, 8)).ToString() : body;
        });
        foreach (var c in text)
        {
            bytes.Add((byte)c);
        }

        return FormFill.Cp1252.GetString([.. bytes]);
    }
}
