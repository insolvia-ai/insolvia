using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Insolvia.UstTool;

/// <summary>
/// An XLSX worksheet as rows of cell strings. An XLSX is a zip of XML, so the
/// BCL reads it with no package. Enough for the UST's flat tables; anything
/// this cannot read is a format change a human must look at anyway.
/// </summary>
internal static partial class Xlsx
{
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [GeneratedRegex(@"\Axl/worksheets/sheet\d+\.xml\z")]
    private static partial Regex WorksheetName();

    [GeneratedRegex(@"\A[A-Z]+")]
    private static partial Regex ColumnLetters();

    /// <summary>
    /// The worksheet at <paramref name="sheetIndex"/> in the order of its file
    /// names, sorted as text (<c>sheet1</c>, <c>sheet10</c>, <c>sheet2</c>…) —
    /// which is how the Python read them, and the order the committed releases
    /// were ingested in. Each row is trimmed cell text, gaps filled with "".
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadSheetRows(byte[] data, int sheetIndex = 0)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            var root = Load(sharedEntry);
            foreach (var si in root.Elements(M + "si"))
            {
                shared.Add(string.Concat(si.Descendants(M + "t").Select(LeadingText)));
            }
        }

        var sheets = zip.Entries.Select(entry => entry.FullName).Where(name => WorksheetName().IsMatch(name)).Order(StringComparer.Ordinal).ToList();
        var sheet = Load(zip.GetEntry(sheets[sheetIndex])!);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in sheet.Descendants(M + "row"))
        {
            var cells = new Dictionary<int, string>();
            foreach (var cell in row.Elements(M + "c"))
            {
                var columns = ColumnLetters().Match((string?)cell.Attribute("r") ?? "");
                if (!columns.Success)
                {
                    continue;
                }

                var index = columns.Value.Aggregate(0, (sum, letter) => (sum * 26) + (letter - 'A' + 1));
                string value;
                if (cell.Element(M + "v") is { } v)
                {
                    value = LeadingText(v);
                    if ((string?)cell.Attribute("t") == "s")
                    {
                        value = shared[int.Parse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture)];
                    }
                }
                else
                {
                    value = cell.Element(M + "is") is { } inline ? string.Concat(inline.DescendantsAndSelf().Select(LeadingText)) : "";
                }

                cells[index - 1] = value;
            }

            var width = cells.Count == 0 ? 0 : cells.Keys.Max() + 1;
            rows.Add([.. Enumerable.Range(0, width).Select(i => cells.TryGetValue(i, out var text) ? text.Trim() : "")]);
        }

        return rows;
    }

    private static XElement Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        // Whitespace kept, as ElementTree keeps it: a cell of spaces is a cell.
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace).Root!;
    }

    /// <summary>ElementTree's <c>.text</c>: the text before an element's first child.</summary>
    private static string LeadingText(XElement element) =>
        string.Concat(element.Nodes().TakeWhile(node => node is not XElement).OfType<XText>().Select(text => text.Value));
}
