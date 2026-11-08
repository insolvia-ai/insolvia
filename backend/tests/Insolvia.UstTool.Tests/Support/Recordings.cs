using System.IO.Compression;
using System.Text;

namespace Insolvia.UstTool.Tests.Support;

/// <summary>
/// The recorded UST spreadsheets (Fixtures/&lt;period&gt;/, byte for byte as
/// justice.gov served them, so their SHA-256 is the one the committed
/// manifests record) and the committed registry they were ingested into.
/// The one exception is the housing workbook: 570 KB as published, kept here
/// trimmed to its header and eleven county rows, each row's XML verbatim.
/// </summary>
internal static class Recordings
{
    public static string FixturesDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>backend/src/Insolvia.Domain/Engines/Regulatory in this checkout.</summary>
    public static string RegistryRoot { get; } = FindRegistryRoot();

    public static string Accessed => "2026-09-04";

    /// <summary>A fetcher that serves <c>…/bapcpa/&lt;period&gt;/bci_data/docs/&lt;name&gt;.xlsx</c> from the fixtures.</summary>
    public static Fetcher Fetcher(IReadOnlyDictionary<string, string>? renamed = null) => url =>
    {
        var segments = new Uri(url).Segments;
        var period = segments[^4].TrimEnd('/');
        var name = segments[^1];
        if (renamed is not null && renamed.TryGetValue(name, out var other))
        {
            name = other;
        }

        return Task.FromResult(File.ReadAllBytes(Path.Combine(FixturesDirectory, period, name)));
    };

    public static byte[] Bytes(string period, string name) => File.ReadAllBytes(Path.Combine(FixturesDirectory, period, name));

    public static string Committed(string relative) => File.ReadAllText(Path.Combine(RegistryRoot, relative));

    /// <summary>
    /// A committed release as the tool writes it today: the Python wrote the
    /// committed ones, and their <c>notes</c> name it as the converter.
    /// </summary>
    public static string CommittedAsIngestedNow(string relative) =>
        Committed(relative).Replace(Ingest.FormerConverter, Ingest.ConvertedBy, StringComparison.Ordinal);

    /// <summary>
    /// A minimal workbook for the reader's edge cases: each sheet is its rows'
    /// XML, the shared strings are written as given (rich text runs and all).
    /// </summary>
    public static byte[] Workbook(IReadOnlyDictionary<string, string> sheets, IReadOnlyList<string>? sharedStrings = null)
    {
        const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }

            foreach (var (name, rows) in sheets)
            {
                Add($"xl/worksheets/{name}.xml", $"<worksheet xmlns=\"{Ns}\"><sheetData>{rows}</sheetData></worksheet>");
            }

            if (sharedStrings is not null)
            {
                Add("xl/sharedStrings.xml", $"<sst xmlns=\"{Ns}\">{string.Concat(sharedStrings.Select(si => $"<si>{si}</si>"))}</sst>");
            }

            // A worksheet-shaped name the reader must not take for a sheet.
            Add("xl/worksheets/_rels/sheet1.xml.rels", "<Relationships/>");
        }

        return buffer.ToArray();
    }

    private static string FindRegistryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Insolvia.Domain", "Engines", "Regulatory");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"no src/Insolvia.Domain/Engines/Regulatory above {AppContext.BaseDirectory}");
    }
}
