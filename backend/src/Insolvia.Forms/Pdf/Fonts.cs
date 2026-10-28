using System.Text.Json;
using PdfSharp.Pdf;
using static Insolvia.Forms.Pdf.PdfObjects;

namespace Insolvia.Forms.Pdf;

/// <summary>A font's metrics, as pypdf 6.16.2's <c>FontDescriptor</c> holds them.</summary>
internal sealed record FontDescriptor(string Name, double Ascent, double Descent, int Flags, IReadOnlyList<double> BBox)
{
    /// <summary>pypdf's defaults: the mean of the 14 core fonts, rounded to 100.</summary>
    public static FontDescriptor Default(string name) => new(name, 700, -200, 32, [-100, -200, 1000, 900]);

    public bool FixedPitch => (Flags & 1) == 1;
}

/// <summary>
/// The parts of pypdf's <c>Font</c> the appearance generator reads (from
/// <c>pypdf/_font.py</c>): a simple 8-bit font's encoding (code to Unicode),
/// its widths (keyed by the code's character, as pypdf keys them), its
/// descriptor and its space. Composite (Type0) and Type3 fonts, and a
/// /ToUnicode map, are refused rather than guessed (<see cref="FontNotSupportedException"/>):
/// no court form carries one.
/// </summary>
internal sealed class Font
{
    private Dictionary<string, string>? _reverse;

    private Font(string name, string subType, IReadOnlyDictionary<int, string> encoding, Dictionary<string, double> widths, FontDescriptor descriptor, double spaceWidth, string spaceChar)
    {
        Name = name;
        SubType = subType;
        Encoding = encoding;
        Widths = widths;
        Descriptor = descriptor;
        SpaceWidth = spaceWidth;
        SpaceChar = spaceChar;
    }

    public string Name { get; }

    public string SubType { get; }

    /// <summary>Character code to the Unicode text it draws, in code order.</summary>
    public IReadOnlyDictionary<int, string> Encoding { get; }

    /// <summary>Width by the code's character (<c>chr(code)</c>), plus <c>"default"</c>.</summary>
    public Dictionary<string, double> Widths { get; }

    public FontDescriptor Descriptor { get; }

    public double SpaceWidth { get; }

    public string SpaceChar { get; }

    private double DefaultWidth => Widths["default"];

    /// <summary>pypdf's <c>Font.from_font_resource</c> for a simple font.</summary>
    public static Font FromResource(PdfDictionary font)
    {
        var name = (Str(font, "/BaseFont") ?? "/Unknown").TrimStart('/');
        var subType = (Str(font, "/Subtype") ?? "/Unknown").TrimStart('/');
        if (subType is not ("Type1" or "MMType1" or "TrueType"))
        {
            throw new FontNotSupportedException($"font {name} is a {subType} font; only simple Type1/TrueType fonts are supported");
        }

        if (font.Elements.ContainsKey("/ToUnicode"))
        {
            throw new FontNotSupportedException($"font {name} carries a /ToUnicode map, which is not supported");
        }

        var encoding = ParseEncoding(font);
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        FontDescriptor? descriptor = null;
        if (font.Elements.ContainsKey("/Widths"))
        {
            var array = Array(font.Elements["/Widths"])!;
            var first = font.Elements.ContainsKey("/FirstChar") ? (int)Num(font.Elements["/FirstChar"]) : 0;
            for (var i = 0; i < array.Elements.Count; i++)
            {
                widths[char.ConvertFromUtf32(i + first)] = Math.Truncate(Num(array.Elements[i]));
            }
        }
        else if (FontData.Core.TryGetValue(name, out var core))
        {
            descriptor = core.Descriptor;
            foreach (var (code, character) in encoding)
            {
                if (core.Widths.TryGetValue(character, out var width))
                {
                    widths[char.ConvertFromUtf32(code)] = width;
                }
            }
        }

        if (Dict(font.Elements["/FontDescriptor"]) is { } fd)
        {
            if (fd.Elements.ContainsKey("/MissingWidth"))
            {
                widths["default"] = Num(fd.Elements["/MissingWidth"]);
            }

            var bbox = Array(fd.Elements["/FontBBox"]) is { Elements.Count: 4 } box
                ? box.Elements.Select(Num).ToList()
                : null;
            var basic = FontDescriptor.Default(name);
            descriptor = new FontDescriptor(
                Str(fd, "/FontName")?.TrimStart('/') ?? basic.Name,
                fd.Elements.ContainsKey("/Ascent") ? Num(fd.Elements["/Ascent"]) : basic.Ascent,
                fd.Elements.ContainsKey("/Descent") ? Num(fd.Elements["/Descent"]) : basic.Descent,
                fd.Elements.ContainsKey("/Flags") ? (int)Num(fd.Elements["/Flags"]) : basic.Flags,
                bbox ?? basic.BBox);
        }

        descriptor ??= FontDescriptor.Default(name);
        var spaceChar = " ";
        foreach (var (code, character) in encoding)
        {
            if (character == " ")
            {
                spaceChar = char.ConvertFromUtf32(code);
                break;
            }
        }

        if (widths.GetValueOrDefault("default") == 0)
        {
            AddDefaultWidth(widths, descriptor.FixedPitch, spaceChar);
        }

        var space = widths.GetValueOrDefault(spaceChar);
        if (space == 0)
        {
            space = descriptor.FixedPitch ? widths["default"] : Math.Floor(widths["default"] / 2);
        }

        return new Font(name, subType, encoding, widths, descriptor, space, spaceChar);
    }

    /// <summary>pypdf's <c>Font.from_core_font_name</c>: a standard-14 font in WinAnsiEncoding.</summary>
    public static Font FromCoreName(string coreFontName)
    {
        var name = coreFontName.TrimStart('/');
        var core = FontData.Core[name];
        var encoding = new SortedDictionary<int, string>();
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var code = 0; code < 256; code++)
        {
            if (FontData.Cp1252[code] is not { } character)
            {
                continue;
            }

            encoding[code] = character;

            // pypdf's own test: the CODE's character must be in the metrics,
            // but the width read is the encoded character's — so codes
            // 128-159 (whose chr() is a control) get no width. Reproduced.
            if (core.Widths.ContainsKey(char.ConvertFromUtf32(code)) && core.Widths.TryGetValue(character, out var width))
            {
                widths[char.ConvertFromUtf32(code)] = width;
            }
        }

        widths["default"] = core.Widths["default"];

        // The dataclass defaults: from_core_font_name sets neither.
        return new Font(name, "Type1", encoding, widths, core.Descriptor, 250, " ");
    }

    /// <summary>Whether every character of the text is in the encoding (pypdf's <c>can_encode</c>).</summary>
    public bool CanEncode(string text)
    {
        var supported = Encoding.Values.ToHashSet(StringComparer.Ordinal);
        return Py.Chars(text).All(supported.Contains);
    }

    /// <summary>Sum of the characters' widths (pypdf's <c>get_text_width</c>), keyed by character.</summary>
    public double TextWidth(string text)
    {
        var total = 0.0;
        foreach (var ch in Py.Chars(text))
        {
            total += Widths.TryGetValue(ch, out var width) ? width : DefaultWidth;
        }

        return total;
    }

    /// <summary>Unicode text to the font's codes, as characters (pypdf's <c>_unicode_to_glyph_id</c>, no RTL shaping).</summary>
    public string ToGlyphs(string text)
    {
        _reverse ??= BuildReverse();
        return string.Concat(Py.Chars(text).Select(ch => _reverse.TryGetValue(ch, out var glyph) ? glyph : ch));
    }

    /// <summary>Glyph characters to the bytes a simple font shows (pypdf's <c>_glyph_id_to_bytes</c>).</summary>
    public static byte[] GlyphBytes(string glyphs)
    {
        var bytes = new List<byte>();
        foreach (var ch in Py.Chars(glyphs))
        {
            var code = char.ConvertToUtf32(ch, 0);
            bytes.Add(code < 256 ? (byte)code : (byte)'?');
        }

        return [.. bytes];
    }

    private Dictionary<string, string> BuildReverse()
    {
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, character) in Encoding)
        {
            reverse[character] = char.ConvertFromUtf32(code);
        }

        return reverse;
    }

    private static void AddDefaultWidth(Dictionary<string, double> widths, bool fixedPitch, string spaceChar)
    {
        if (widths.Count == 0)
        {
            widths["default"] = 500;
            return;
        }

        if (widths.TryGetValue(spaceChar, out var space) && space != 0)
        {
            widths["default"] = fixedPitch ? space : Math.Truncate(2 * space);
            return;
        }

        var valid = widths.Values.Where(width => width > 0).ToList();
        widths["default"] = valid.Count > 0 ? Math.Floor(valid.Sum() / valid.Count) : 500;
    }

    /// <summary>pypdf's <c>_parse_encoding</c> for a simple font: a named base, then /Differences by glyph name.</summary>
    private static SortedDictionary<int, string> ParseEncoding(PdfDictionary font)
    {
        string[] table;
        var raw = Resolve(font.Elements["/Encoding"]);
        if (raw is null)
        {
            var baseFont = Str(font, "/BaseFont");
            table = baseFont is not null && FontData.Encodings.TryGetValue(baseFont, out var own)
                ? own
                : FontData.Encodings["/StandardEncoding"];
        }
        else if (Str(raw) is { } named)
        {
            table = FontData.Encodings.TryGetValue(named, out var found)
                ? found
                : throw new FontNotSupportedException($"encoding {named} is not supported");
        }
        else if (raw is PdfDictionary dict && dict.Elements.ContainsKey("/BaseEncoding"))
        {
            table = FontData.Encodings.TryGetValue(Str(dict, "/BaseEncoding") ?? string.Empty, out var found)
                ? found
                : FontData.Encodings["/StandardEncoding"];
        }
        else
        {
            table = FontData.Encodings["/StandardEncoding"];
        }

        var encoding = new SortedDictionary<int, string>();
        for (var code = 0; code < table.Length; code++)
        {
            encoding[code] = table[code];
        }

        if (raw is PdfDictionary differences && Array(differences.Elements["/Differences"]) is { } list)
        {
            var at = 0;
            foreach (var item in list.Elements)
            {
                if (IsNumber(item))
                {
                    at = (int)Num(item);
                    continue;
                }

                var glyph = Str(item) ?? string.Empty;
                if (at < table.Length)
                {
                    encoding[at] = FontData.Glyphs.TryGetValue(glyph, out var unicode) ? unicode : glyph;
                }

                at++;
            }
        }

        return encoding;
    }
}

/// <summary>A font the appearance generator does not port (composite, Type3, a /ToUnicode map, an unknown encoding).</summary>
public sealed class FontNotSupportedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public FontNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public FontNotSupportedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public FontNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>pypdf's data tables (core-font metrics, named encodings, the Adobe glyph list), embedded as <c>FontData.json</c>.</summary>
internal static class FontData
{
    private static readonly Lazy<Loaded> Data = new(Load);

    public static IReadOnlyDictionary<string, CoreFont> Core => Data.Value.Core;

    public static IReadOnlyDictionary<string, string[]> Encodings => Data.Value.Encodings;

    /// <summary>cp1252 by code (pypdf's <c>encoding_dict_from_named_encoding("cp1252")</c>); null where undefined.</summary>
    public static IReadOnlyList<string?> Cp1252 => Data.Value.Cp1252;

    public static IReadOnlyDictionary<string, string> Glyphs => Data.Value.Glyphs;

    private static Loaded Load()
    {
        using var stream = typeof(FontData).Assembly.GetManifestResourceStream("Insolvia.Forms.FontData.json")
            ?? throw new InvalidOperationException("FontData.json is not embedded");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        var encodings = root.GetProperty("encodings").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray(), StringComparer.Ordinal);
        var cp1252 = root.GetProperty("cp1252").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? null : v.GetString()).ToList();
        var glyphs = root.GetProperty("glyphs").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var core = new Dictionary<string, CoreFont>(StringComparer.Ordinal);
        foreach (var font in root.GetProperty("fonts").EnumerateObject())
        {
            var d = font.Value.GetProperty("descriptor");
            var descriptor = new FontDescriptor(
                d.GetProperty("name").GetString()!,
                d.GetProperty("ascent").GetDouble(),
                d.GetProperty("descent").GetDouble(),
                d.GetProperty("flags").GetInt32(),
                [.. d.GetProperty("bbox").EnumerateArray().Select(v => v.GetDouble())]);
            var widths = font.Value.GetProperty("widths").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal);
            core[font.Name] = new CoreFont(descriptor, widths);
        }

        return new Loaded(core, encodings, cp1252, glyphs);
    }

    internal sealed record CoreFont(FontDescriptor Descriptor, IReadOnlyDictionary<string, double> Widths);

    private sealed record Loaded(Dictionary<string, CoreFont> Core, Dictionary<string, string[]> Encodings, List<string?> Cp1252, Dictionary<string, string> Glyphs);
}

/// <summary>Python string semantics where they differ from .NET's UTF-16 ones.</summary>
internal static class Py
{
    /// <summary>The string's code points as strings — what iterating a Python <c>str</c> yields.</summary>
    public static IEnumerable<string> Chars(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            yield return rune.ToString();
        }
    }

    /// <summary>Python's <c>len(str)</c>.</summary>
    public static int Length(string text) => text.EnumerateRunes().Count();

    /// <summary>Python's <c>str.splitlines()</c>.</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var breakLength = c switch
            {
                '\r' when i + 1 < text.Length && text[i + 1] == '\n' => 2,
                '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029' => 1,
                _ => 0,
            };
            if (breakLength == 0)
            {
                i++;
                continue;
            }

            lines.Add(text[start..i]);
            i += breakLength;
            start = i;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>Python's <c>str.strip()</c> being empty: whitespace only.</summary>
    public static bool IsBlank(string text) => text.All(c => char.IsWhiteSpace(c) || c is '\x1c' or '\x1d' or '\x1e' or '\x1f');
}
