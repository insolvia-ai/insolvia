// Spike #514 (plan 18.3): can PDFsharp reproduce what services/api's pypdf
// fill engine does? This console app reads the per-case instruction files
// that python/dump_and_fill.py wrote (the PDF-level instructions
// core/form_fill.fill_form derives from its logical values) and applies them
// to the same vendored court template with PDFsharp, writing
// <out>/dotnet/<case>.pdf and <out>/dotnet/timings.json.
//
// PDFsharp is used as a PDF OBJECT MODEL only: open, walk dictionaries, swap
// streams, save. Its high-level AcroForm API (PdfTextField.Text etc.) renders
// appearances through XGraphics with an embedded TrueType font, which neither
// looks like pypdf's output nor is deterministic without a font resolver — see
// the research note. The appearance-stream generator here is a port of
// pypdf 6.16.2's TextStreamAppearance (generic/_appearance_stream.py), the
// code path services/api runs with auto_regenerate=False.
//
// Usage: dotnet run -c Release -- <repo-root> <out-dir> [runs]

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length > 0 && args[0] == "--highlevel")
{
    HighLevel.Run(args[1], args[2]);
    return;
}

var repo = args.Length > 0 ? args[0] : ".";
var outDir = args.Length > 1 ? args[1] : Path.Combine(repo, "backend/spikes/pdf/out");
var runs = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 15;
Directory.CreateDirectory(Path.Combine(outDir, "dotnet"));

var timings = new SortedDictionary<string, double>();
foreach (var path in Directory.GetFiles(Path.Combine(outDir, "inputs"), "*.json").Order())
{
    var caseName = Path.GetFileNameWithoutExtension(path);
    var input = FillInput.Load(path);
    var template = File.ReadAllBytes(Path.Combine(repo, input.Template));
    var data = Engine.Fill(template, input);
    File.WriteAllBytes(Path.Combine(outDir, "dotnet", caseName + ".pdf"), data);
    var again = Engine.Fill(template, input);
    var deterministic = data.AsSpan().SequenceEqual(again);
    if (!deterministic) File.WriteAllBytes(Path.Combine(outDir, "dotnet", caseName + ".again.pdf"), again);
    var samples = new List<double>();
    for (var i = 0; i < runs; i++)
    {
        var sw = Stopwatch.StartNew();
        Engine.Fill(template, input);
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }
    samples.Sort();
    timings[caseName] = Math.Round(samples[samples.Count / 2], 2);
    Console.WriteLine($"{caseName,-22} {data.Length,8} bytes  deterministic={deterministic}  median {timings[caseName]} ms");
}
File.WriteAllText(
    Path.Combine(outDir, "dotnet", "timings.json"),
    JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }) + "\n");

// --- input ---------------------------------------------------------------------

sealed record OverlayDraw(string Name, int Page, double X, double Y, double W, double? H, double Size, string Kind, string? Value);
sealed record WidgetStateTarget(string Name, string[] States, int[] Indexes);
sealed record Stamps(bool DraftWatermark, bool PrintDate, string Text);

sealed record FillInput(
    string Template,
    List<(string Name, string Value)> Text,
    List<(string Name, string Export)> Buttons,
    List<WidgetStateTarget> WidgetStates,
    List<OverlayDraw> Overlay,
    Stamps? Stamps)
{
    public static FillInput Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        static List<(string, string)> Pairs(JsonElement e) =>
            e.EnumerateArray().Select(p => (p[0].GetString()!, p[1].GetString()!)).ToList();
        var states = root.GetProperty("widget_states").EnumerateArray().Select(w => new WidgetStateTarget(
            w.GetProperty("name").GetString()!,
            w.GetProperty("states").EnumerateArray().Select(s => s.GetString()!).ToArray(),
            w.GetProperty("indexes").EnumerateArray().Select(s => s.GetInt32()).ToArray())).ToList();
        var overlay = root.GetProperty("overlay").EnumerateArray().Select(o => new OverlayDraw(
            o.GetProperty("name").GetString()!,
            o.GetProperty("page").GetInt32(),
            o.GetProperty("x").GetDouble(),
            o.GetProperty("y").GetDouble(),
            o.GetProperty("w").GetDouble(),
            o.GetProperty("h").ValueKind == JsonValueKind.Null ? null : o.GetProperty("h").GetDouble(),
            o.GetProperty("size").GetDouble(),
            o.GetProperty("kind").GetString()!,
            o.TryGetProperty("value", out var v) ? v.GetString() : null)).ToList();
        var stampsEl = root.GetProperty("stamps");
        Stamps? stamps = stampsEl.ValueKind == JsonValueKind.Null ? null : new Stamps(
            stampsEl.GetProperty("draft_watermark").GetBoolean(),
            stampsEl.GetProperty("print_date").GetBoolean(),
            stampsEl.GetProperty("text").GetString()!);
        return new FillInput(
            root.GetProperty("template").GetString()!,
            Pairs(root.GetProperty("text")),
            Pairs(root.GetProperty("buttons")),
            states, overlay, stamps);
    }
}

// --- the engine ------------------------------------------------------------------

static class Engine
{
    public static byte[] Fill(byte[] template, FillInput input)
    {
        var nothingToDo = input.Text.Count == 0 && input.Buttons.Count == 0
            && input.WidgetStates.Count == 0 && input.Overlay.Count == 0 && input.Stamps is null;
        if (nothingToDo)
            return template; // a flat notice with nothing to draw ships verbatim, as in Python

        using var source = new MemoryStream(template);
        var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify);

        var updates = input.Text.Select(t => (t.Name, t.Value))
            .Concat(input.Buttons.Select(b => (b.Name, "/" + b.Export))).ToList();
        if (updates.Count > 0)
            FieldWriter.Apply(doc, updates);
        if (input.WidgetStates.Count > 0)
            FieldWriter.ApplyWidgetStates(doc, input.WidgetStates);
        if (input.Overlay.Count > 0)
            Overlay.Apply(doc, input.Overlay);
        if (input.Stamps is { } stamps)
            Overlay.Stamp(doc, stamps);

        Determinism.Pin(doc);
        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return Determinism.PinXmpUuids(output.ToArray(), template);
    }
}

static class Pdf
{
    public static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    public static PdfDictionary? Dict(PdfItem? item) => Resolve(item) as PdfDictionary;

    public static double Num(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfLongInteger l => l.Value,
        PdfIntegerObject i => i.Value,
        PdfRealObject r => r.Value,
        _ => throw new InvalidOperationException($"not a number: {item}"),
    };

    public static string? Str(PdfDictionary d, string key) => Resolve(d.Elements[key]) switch
    {
        PdfString s => s.Value,
        PdfStringObject s => s.Value,
        PdfName n => n.Value,
        PdfNameObject n => n.Value,
        _ => null,
    };

    public static string? Str(PdfItem? item) => Resolve(item) switch
    {
        PdfString s => s.Value,
        PdfStringObject s => s.Value,
        _ => null,
    };

    public static IEnumerable<PdfDictionary> Widgets(PdfPage page)
    {
        var annots = Resolve(page.Elements["/Annots"]) as PdfArray;
        if (annots is null) yield break;
        foreach (var item in annots.Elements)
            if (Dict(item) is { } a && Str(a, "/Subtype") == "/Widget")
                yield return a;
    }

    /// The fully qualified field name: every /T up the /Parent chain.
    public static string? QualifiedName(PdfDictionary node)
    {
        var parts = new List<string>();
        for (var n = node; n is not null; n = Dict(n.Elements["/Parent"]))
            if (Str(n, "/T") is { } t) parts.Add(t);
        if (parts.Count == 0) return null;
        parts.Reverse();
        return string.Join('.', parts);
    }

    /// The field node owning a widget: itself when it carries /FT and /T.
    public static PdfDictionary FieldOf(PdfDictionary widget) =>
        widget.Elements.ContainsKey("/FT") && widget.Elements.ContainsKey("/T")
            ? widget
            : Dict(widget.Elements["/Parent"]) ?? new PdfDictionary();

    public static PdfItem? Inherited(PdfDictionary node, string key)
    {
        for (var n = node; n is not null; n = Dict(n.Elements["/Parent"]))
            if (n.Elements.ContainsKey(key)) return n.Elements[key];
        return null;
    }
}

static class FieldWriter
{
    /// Every key set on exactly the field its QUALIFIED name addresses —
    /// what form_fill._apply_field_updates achieves with its rename shield
    /// around pypdf's update_page_form_field_values.
    public static void Apply(PdfDocument doc, List<(string Name, string Value)> updates)
    {
        var acro = Pdf.Dict(doc.Internals.Catalog.Elements["/AcroForm"])
            ?? throw new InvalidOperationException("no /AcroForm");
        // auto_regenerate=False: pypdf writes NeedAppearances false.
        acro.Elements["/NeedAppearances"] = new PdfBoolean(false);
        var byName = new Dictionary<string, List<(PdfPage Page, PdfDictionary Widget, PdfDictionary Field)>>();
        foreach (var page in doc.Pages)
            foreach (var w in Pdf.Widgets(page))
            {
                var field = Pdf.FieldOf(w);
                var name = Pdf.QualifiedName(field);
                if (name is null) continue;
                if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
                list.Add((page, w, field));
            }
        foreach (var (key, value) in updates)
        {
            if (!byName.TryGetValue(key, out var widgets))
                throw new InvalidOperationException($"no field {key}");
            foreach (var (page, widget, field) in widgets)
                SetOne(doc, acro, page, widget, field, value);
        }
    }

    static void SetOne(PdfDocument doc, PdfDictionary acro, PdfPage page, PdfDictionary widget, PdfDictionary field, string value)
    {
        var ft = Pdf.Str(field, "/FT");
        if (ft == "/Ch" && field.Elements.ContainsKey("/I"))
            field.Elements.Remove("/I");
        // pypdf writes the parent's /V as a TEXT STRING, even for a button
        // ("(/On)"); a single-widget button then has it overwritten by a name
        // below. Mirrored on purpose — the research note discusses it.
        field.Elements["/V"] = new PdfString(value);
        if (ft == "/Btn")
        {
            var normal = Pdf.Dict(Pdf.Dict(widget.Elements["/AP"])?.Elements["/N"])
                ?? throw new InvalidOperationException("button without /AP /N");
            var state = normal.Elements.ContainsKey(value) ? value : "/Off";
            widget.Elements["/AS"] = new PdfName(state);
            widget.Elements["/V"] = new PdfName(state);
        }
        else if (ft is "/Tx" or "/Ch")
        {
            TextAppearance.Write(doc, acro, page, widget, field);
        }
    }

    /// The broken-group route: /AS only, widgets indexed in page order.
    public static void ApplyWidgetStates(PdfDocument doc, List<WidgetStateTarget> targets)
    {
        var byName = targets.ToDictionary(t => t.Name);
        var seen = targets.ToDictionary(t => t.Name, _ => 0);
        foreach (var page in doc.Pages)
            foreach (var w in Pdf.Widgets(page))
            {
                var name = Pdf.QualifiedName(w);
                if (name is null || !seen.TryGetValue(name, out var index)) continue;
                seen[name] = index + 1;
                var normal = Pdf.Dict(Pdf.Dict(w.Elements["/AP"])?.Elements["/N"]);
                if (normal is null) continue;
                var on = normal.Elements.Keys.Where(k => k != "/Off").ToList();
                if (on.Count != 1) continue;
                var wanted = byName[name];
                if (wanted.Indexes.Contains(index) || wanted.States.Contains(on[0].TrimStart('/')))
                    w.Elements["/AS"] = new PdfName(on[0]);
            }
    }
}

/// A port of pypdf 6.16.2 TextStreamAppearance for the case the court forms
/// exercise: a non-embedded standard-14 Helvetica named in /DR, single-line,
/// multiline and comb text fields, and combo choice fields. Numbers are
/// printed the way Python's str(float) prints them, so the appearance
/// stream bytes come out identical to pypdf's.
static class TextAppearance
{
    const double Ascent = 718, BBoxTop = 931, BBoxBottom = -225, SpaceWidth = 278;
    const double LeadingFactor = (BBoxTop - BBoxBottom) / 1000.0;

    public static void Write(PdfDocument doc, PdfDictionary acro, PdfPage page, PdfDictionary widget, PdfDictionary field)
    {
        var rect = (PdfArray)Pdf.Resolve(widget.Elements["/Rect"])!;
        double x1 = Pdf.Num(rect.Elements[0]), y1 = Pdf.Num(rect.Elements[1]);
        double x2 = Pdf.Num(rect.Elements[2]), y2 = Pdf.Num(rect.Elements[3]);
        var rotate = Pdf.Inherited(page, "/Rotate") is { } r ? (int)Pdf.Num(r) : 0;
        double width = Math.Abs(x2 - x1), height = Math.Abs(y2 - y1);
        if (rotate is 90 or 270) (width, height) = (height, width);

        var da = Pdf.Inherited(widget, "/DA") is { } daItem
            ? Pdf.Str(daItem)!
            : (Pdf.Str(acro.Elements["/DA"]) ?? "/Helv 0 Tf 0 g");
        var tokens = da.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var fontName = tokens[tokens.IndexOf("Tf") - 2];
        tokens.RemoveAt(tokens.IndexOf("Tf") - 2);
        var fontSize = double.Parse(tokens[tokens.IndexOf("Tf") - 1], CultureInfo.InvariantCulture);
        tokens.RemoveAt(tokens.IndexOf("Tf") - 1);
        tokens.Remove("Tf");
        var fontColor = string.Join(' ', tokens);

        var flags = field.Elements.ContainsKey("/Ff") ? (long)Pdf.Num(field.Elements["/Ff"]) : 0;
        var ft = Pdf.Str(field, "/FT");
        if (ft == "/Ch" && (flags & (1 << 17)) == 0)
            throw new NotSupportedException("list-box choice fields (not on any court form)");
        var text = Pdf.Str(field, "/V") ?? "";

        // The font: the court PDFs' /DR /Helv is Type1 Helvetica, not embedded.
        var drFonts = Pdf.Dict(Pdf.Dict(acro.Elements["/DR"])?.Elements["/Font"])
            ?? throw new NotSupportedException("no /DR /Font");
        var fontItem = drFonts.Elements[fontName] ?? throw new NotSupportedException($"font {fontName} not in /DR");
        var fontDict = Pdf.Dict(fontItem)!;
        if (Pdf.Str(fontDict, "/BaseFont") != "/Helvetica")
            throw new NotSupportedException($"font {fontName} is {Pdf.Str(fontDict, "/BaseFont")}, only Helvetica is ported");

        var comb = (flags & (1 << 24)) != 0;
        int? maxLen = comb && widget.Elements.ContainsKey("/MaxLen") ? (int)Pdf.Num(widget.Elements["/MaxLen"]) : null;
        var multiline = (flags & (1 << 12)) != 0;
        var alignment = field.Elements.ContainsKey("/Q") ? (int)Pdf.Num(field.Elements["/Q"]) : 0;
        var borderWidth = 1.0;
        var borderIsInt = true;
        var borderStyle = "/S";
        if (Pdf.Dict(field.Elements["/BS"]) is { } bs)
        {
            if (bs.Elements.ContainsKey("/W"))
            {
                borderWidth = Pdf.Num(bs.Elements["/W"]);
                borderIsInt = Pdf.Resolve(bs.Elements["/W"]) is PdfInteger;
            }
            borderStyle = Pdf.Str(bs, "/S") ?? borderStyle;
        }
        if (Pdf.Dict(Pdf.Inherited(field, "/MK"))?.Elements["/R"] is { } rot && Pdf.Num(rot) % 360 != 0)
            throw new NotSupportedException("rotated widget appearance (/MK /R) — not on any court form");

        var data = Stream(text, fontName, fontSize, fontColor, multiline, alignment, comb, maxLen,
            width, height, borderWidth, borderIsInt, borderStyle);

        var stream = new PdfDictionary(doc);
        stream.Elements["/Type"] = new PdfName("/XObject");
        stream.Elements["/Subtype"] = new PdfName("/Form");
        stream.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(width), new PdfReal(height));
        var fonts = new PdfDictionary(doc);
        fonts.Elements[fontName] = fontItem; // the same indirect reference /DR holds
        var resources = new PdfDictionary(doc);
        resources.Elements["/Font"] = fonts;
        stream.Elements["/Resources"] = resources;
        var ap = Pdf.Dict(widget.Elements["/AP"]);
        if (Pdf.Dict(ap?.Elements["/N"]) is { } old)
        {
            string[] skip = ["/BBox", "/Length", "/Subtype", "/Type", "/Filter", "/Resources"];
            foreach (var key in old.Elements.Keys)
                if (!skip.Contains(key)) stream.Elements[key] = old.Elements[key];
        }
        if (ap?.Elements["/N"] is PdfReference oldRef && oldRef.Value is PdfDictionary target)
        {
            // pypdf replaces the existing /N OBJECT in place. The court PDFs
            // share one /N among a field's widgets (B106D's "Debtor 1" on
            // three pages), so the last widget's box wins for all of them —
            // reproduced here, see the research note.
            var elements = stream.Elements.Keys.Select(k => (k, stream.Elements[k])).ToList();
            target.Stream = null!;
            target.Elements.Clear();
            foreach (var (key, value) in elements) target.Elements[key] = value;
            target.CreateStream(data);
            return;
        }
        stream.CreateStream(data);
        doc.Internals.AddObject(stream);
        if (ap is null)
        {
            ap = new PdfDictionary(doc);
            widget.Elements["/AP"] = ap;
        }
        ap.Elements["/N"] = stream.Reference;
    }

    static double Width(string text) => text.Sum(c => (double)Helvetica.Width(c));

    record struct Line(double Width, string Text);

    static byte[] Stream(string text, string fontName, double fontSize, string fontColor, bool multiline,
        int alignment, bool comb, int? maxLen, double rw, double rh, double borderWidth, bool borderIsInt, string borderStyle)
    {
        var factor = borderStyle is "/B" or "/I" ? 2 : 1;
        var marginValue = Math.Max(borderWidth * factor, 1);
        var marginIsInt = borderIsInt || marginValue == 1 && borderWidth * factor < 1;
        var margin = marginValue;
        var fieldHeight = rh - 2 * margin;
        var fieldWidth = rw - 4 * margin;
        var sizeIsInt = false;
        List<Line> lines;
        if (fontSize == 0)
        {
            const double minFontSize = 4.0;
            if (multiline)
            {
                (lines, fontSize, sizeIsInt) = ScaleMultiline(text, 12, fieldWidth, fieldHeight, minFontSize);
            }
            else
            {
                var maxVertical = fieldHeight / LeadingFactor;
                var unscaled = Width(text) / 1000.0;
                var maxHorizontal = fieldWidth / (unscaled == 0 ? 1 : unscaled);
                fontSize = Py.Round1(Math.Max(Math.Min(maxVertical, maxHorizontal), minFontSize));
                lines = [new Line(unscaled * fontSize, text)];
            }
        }
        else if (comb)
        {
            lines = text.Select((c, i) => (c, i)).Where(p => p.i < (maxLen ?? text.Length))
                .Select(p => new Line(Helvetica.Width(p.c) * fontSize / 1000.0, p.c.ToString())).ToList();
        }
        else
        {
            lines = Py.SplitLines(text).Select(l => new Line(Width(l) * fontSize / 1000.0, l)).ToList();
        }

        var yOffset = multiline
            ? rh + margin - BBoxTop * fontSize / 1000.0
            : margin + (fieldHeight - Ascent * fontSize / 1000.0) / 2;
        var size = sizeIsInt ? Py.Int(fontSize) : Py.Float(fontSize);
        var m = marginIsInt ? Py.Int(margin) : Py.Float(margin);
        var m2 = marginIsInt ? Py.Int(2 * margin) : Py.Float(2 * margin);
        var da = $"{fontName} {size} Tf {fontColor}";
        var sb = new StringBuilder();
        sb.Append($"q\n/Tx BMC \nq\n{m2} {m} {Py.Float(fieldWidth)} {Py.Float(fieldHeight)} re\nW\nBT\n{da}\n");
        double currentX = 0;
        var currentXIsInt = true;
        for (var n = 0; n < lines.Count; n++)
        {
            var line = lines[n];
            double desired;
            var desiredIsInt = false;
            if (comb && maxLen is { } ml)
            {
                var cell = rw / ml;
                desired = n * cell + (cell - line.Width) / 2;
            }
            else if (alignment == 2) desired = rw - margin * 2 - line.Width;
            else if (alignment == 1) desired = (rw - line.Width) / 2;
            else { desired = margin * 2; desiredIsInt = marginIsInt; }
            var xRel = desired - currentX;
            var xRelText = desiredIsInt && currentXIsInt ? Py.Int(xRel) : Py.Float(xRel);
            string yRelText;
            if (n == 0) yRelText = Py.Float(yOffset);
            else if (comb) yRelText = "0.0";
            else yRelText = Py.Float(-fontSize * LeadingFactor);
            sb.Append($"{xRelText} {yRelText} Td\n");
            currentX = desired;
            currentXIsInt = desiredIsInt;
            sb.Append('(').Append(Escape(line.Text)).Append(") Tj\n");
        }
        sb.Append("ET\nQ\nEMC\nQ\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    static (List<Line>, double, bool) ScaleMultiline(string text, double fontSize, double fieldWidth, double fieldHeight, double min)
    {
        // pypdf's _scale_text: wrap by words, shrink by 0.2pt until it fits.
        var paragraphs = Py.SplitLines(text).Select(l => string.IsNullOrWhiteSpace(l)
            ? new List<(double W, string Word)> { (0, "") }
            : l.Split(' ').Select(w => (Width(w) / 1000.0, w)).ToList()).ToList();
        var isInt = true; // DEFAULT_FONT_SIZE_IN_MULTILINE is the int 12
        while (true)
        {
            var wrapped = new List<Line>();
            var space = SpaceWidth * fontSize / 1000.0;
            var current = new List<string>();
            double currentWidth = 0;
            foreach (var paragraph in paragraphs)
            {
                for (var i = 0; i < paragraph.Count; i++)
                {
                    var (w, word) = paragraph[i];
                    var wordWidth = w * fontSize;
                    var test = currentWidth + wordWidth + (i > 0 ? space : 0);
                    if (test > fieldWidth && current.Count > 0)
                    {
                        wrapped.Add(new Line(currentWidth, string.Join(' ', current)));
                        current = [word];
                        currentWidth = wordWidth;
                    }
                    else if (current.Count == 0 && wordWidth > fieldWidth)
                    {
                        wrapped.Add(new Line(wordWidth, word));
                        current = [];
                        currentWidth = 0;
                    }
                    else
                    {
                        if (current.Count > 0) currentWidth += space;
                        current.Add(word);
                        currentWidth += wordWidth;
                    }
                }
                if (current.Count > 0)
                {
                    wrapped.Add(new Line(currentWidth, string.Join(' ', current)));
                    current = [];
                    currentWidth = 0;
                }
            }
            var total = fontSize + (wrapped.Count - 1) * LeadingFactor * fontSize;
            if (total > fieldHeight && fontSize - 0.2 >= min)
            {
                fontSize -= 0.2;
                isInt = false;
                continue;
            }
            return (wrapped, isInt ? fontSize : Py.Round1(fontSize), isInt);
        }
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}

static class Helvetica
{
    // Adobe AFM advance widths, ASCII 32-126 (the same table form_fill.py carries).
    static readonly int[] Ascii = (
        "278 278 355 556 556 889 667 191 333 333 389 584 278 333 278 278 " +
        "556 556 556 556 556 556 556 556 556 556 278 278 584 584 584 556 " +
        "1015 667 667 722 722 667 611 778 722 278 500 667 556 833 722 778 " +
        "667 778 722 667 611 722 667 944 667 667 611 278 278 278 469 556 " +
        "333 556 556 500 556 556 278 556 556 222 222 500 222 833 556 556 " +
        "556 556 333 500 278 556 500 722 500 500 500 334 260 334 584")
        .Split(' ').Select(int.Parse).ToArray();

    public static int Width(char c) => c is >= ' ' and <= '~'
        ? Ascii[c - 32]
        : throw new NotSupportedException($"non-ASCII {c} — the cp1252 width table is a port gap");
}

/// Python's number printing, so streams compare byte for byte.
static class Py
{
    public static string Int(double d) => ((long)d).ToString(CultureInfo.InvariantCulture);

    /// str(float): shortest round-trip, ".0" on integral values, e-notation
    /// below 1e-4 / from 1e16 with a two-digit exponent.
    public static string Float(double d)
    {
        if (d == Math.Floor(d) && Math.Abs(d) < 1e16)
            return (d == 0 && double.IsNegative(d) ? "-0" : ((long)d).ToString(CultureInfo.InvariantCulture)) + ".0";
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        var e = s.IndexOf('E');
        if (e < 0) return s;
        var exp = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
        return s[..e] + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
    }

    /// round(x, 1): Python rounds the exact binary value half-to-even.
    public static double Round1(double d) => double.Parse(
        Math.Round((decimal)d, 1, MidpointRounding.ToEven).ToString(CultureInfo.InvariantCulture),
        CultureInfo.InvariantCulture);

    public static IEnumerable<string> SplitLines(string s) =>
        s.Replace("\r\n", "\n").Split('\n') is var parts && parts.Length > 0 && parts[^1] == "" ? parts[..^1] : parts;
}

/// Flat releases: form_fill's overlay stream and form_overlay's stamps.
static class Overlay
{
    static readonly Encoding Cp1252 = Encoding.GetEncoding(1252);

    static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    static string Literal(string value)
    {
        var sb = new StringBuilder("(");
        foreach (var b in Cp1252.GetBytes(value))
        {
            if (b is (byte)'\\' or (byte)'(' or (byte)')') sb.Append('\\').Append((char)b);
            else if (b is >= 32 and < 127) sb.Append((char)b);
            else sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
        }
        return sb.Append(')').ToString();
    }

    public static void Apply(PdfDocument doc, List<OverlayDraw> draws)
    {
        foreach (var group in draws.GroupBy(d => d.Page).OrderBy(g => g.Key))
        {
            var page = doc.Pages[group.Key - 1];
            EnsureFont(doc, page, "/Helv", indirect: false);
            var sb = new StringBuilder("% insolvia-overlay\n");
            foreach (var box in group.OrderBy(b => b.Name, StringComparer.Ordinal))
            {
                if (box.Kind == "check")
                {
                    const double inset = 2.5;
                    double ax = box.X + inset, ay = box.Y + inset, bx = box.X + box.W - inset, by = box.Y + box.H!.Value - inset;
                    sb.Append($"% box:{box.Name} check\nq 0 G 1 w {F2(ax)} {F2(ay)} m {F2(bx)} {F2(by)} l S {F2(ax)} {F2(by)} m {F2(bx)} {F2(ay)} l S Q\n");
                }
                else
                {
                    sb.Append($"% box:{box.Name}\nq 0 g 0 Tr 0 Tc 0 Tw 100 Tz 0 Ts BT /Helv {F2(box.Size)} Tf 1 0 0 1 {F2(box.X)} {F2(box.Y)} Tm {Literal(box.Value!)} Tj ET Q\n");
                }
            }
            var stream = new PdfDictionary(doc);
            stream.CreateStream(Encoding.Latin1.GetBytes(sb.ToString()));
            doc.Internals.AddObject(stream);
            var parts = new PdfArray(doc);
            var existing = page.Elements["/Contents"];
            if (Pdf.Resolve(existing) is PdfArray arr)
                foreach (var item in arr.Elements) parts.Elements.Add(item);
            else if (existing is not null)
                parts.Elements.Add(existing);
            parts.Elements.Add(stream.Reference!);
            page.Elements["/Contents"] = parts;
        }
    }

    static PdfDictionary FontDict(PdfDocument doc)
    {
        var font = new PdfDictionary(doc);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/Helvetica");
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        return font;
    }

    static PdfDictionary Fonts(PdfDocument doc, PdfPage page)
    {
        var resources = Pdf.Dict(page.Elements["/Resources"]);
        if (resources is null)
        {
            resources = new PdfDictionary(doc);
            page.Elements["/Resources"] = resources;
        }
        var fonts = Pdf.Dict(resources.Elements["/Font"]);
        if (fonts is null)
        {
            fonts = new PdfDictionary(doc);
            resources.Elements["/Font"] = fonts;
        }
        return fonts;
    }

    static void EnsureFont(PdfDocument doc, PdfPage page, string key, bool indirect)
    {
        var fonts = Fonts(doc, page);
        if (!fonts.Elements.ContainsKey(key))
            fonts.Elements[key] = FontDict(doc);
    }

    /// form_overlay.stamp_pages + pdf_draw: a DRAFT watermark and a
    /// print-date line, appended to each page's (concatenated) content.
    public static void Stamp(PdfDocument doc, Stamps stamps)
    {
        var font = FontDict(doc);
        doc.Internals.AddObject(font);
        foreach (var page in doc.Pages)
        {
            var width = page.MediaBox.Width;
            var height = page.MediaBox.Height;
            var ops = new StringBuilder();
            if (stamps.DraftWatermark)
                ops.Append(TextOps("DRAFT", width * 0.22, height * 0.30, Math.Min(width, height) * 0.16, 45.0, 0.75));
            if (stamps.PrintDate)
                ops.Append(TextOps(stamps.Text, 24.0, height - 24.0, 8.0, 0.0, 0.25));
            Fonts(doc, page).Elements["/InsolviaOverlay"] = font.Reference;
            var existing = ConcatenatedContents(page);
            var combined = existing is null
                ? Cp1252.GetBytes(ops.ToString())
                : [.. existing, (byte)'\n', .. Cp1252.GetBytes(ops.ToString())];
            var stream = new PdfDictionary(doc);
            stream.CreateStream(combined);
            doc.Internals.AddObject(stream);
            page.Elements["/Contents"] = stream.Reference;
        }
    }

    static string TextOps(string text, double x, double y, double size, double degrees, double gray)
    {
        var rad = degrees * Math.PI / 180.0;
        double a = Math.Cos(rad), b = Math.Sin(rad), c = -Math.Sin(rad), d = Math.Cos(rad);
        string F6(double v) => v.ToString("F6", CultureInfo.InvariantCulture);
        var escaped = text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        return $"q\n{F6(a)} {F6(b)} {F6(c)} {F6(d)} {F2(x)} {F2(y)} cm\n{gray.ToString("F2", CultureInfo.InvariantCulture)} g\nBT\n" +
               $"/InsolviaOverlay {size.ToString("F1", CultureInfo.InvariantCulture)} Tf\n0 0 Td\n({escaped}) Tj\nET\nQ\n";
    }

    /// pypdf's ContentStream over /Contents: an array's streams decoded and
    /// joined, each followed by a newline unless it already ends in one.
    static byte[]? ConcatenatedContents(PdfPage page)
    {
        var item = Pdf.Resolve(page.Elements["/Contents"]);
        if (item is PdfDictionary single) return Decoded(single);
        if (item is not PdfArray arr) return null;
        var data = new List<byte>();
        foreach (var part in arr.Elements)
        {
            if (Pdf.Dict(part) is { } s)
            {
                data.AddRange(Decoded(s));
                if (data.Count == 0 || data[^1] != (byte)'\n') data.Add((byte)'\n');
            }
        }
        return [.. data];
    }

    static byte[] Decoded(PdfDictionary stream) => stream.Stream.UnfilteredValue;
}

static class Determinism
{
    /// PDFsharp stamps /Producer, /ModDate and a fresh second /ID element on
    /// every save. Pin them so the same input always yields the same bytes —
    /// the property the Python goldens rely on.
    public static void Pin(PdfDocument doc)
    {
        doc.Info.ModificationDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // The second /ID element is "this revision" — derive it from the first
        // (the template's own) instead of a fresh GUID.
        doc.Internals.SecondDocumentID = doc.Internals.FirstDocumentID;
    }

    /// PDFsharp 6.2.4 regenerates the XMP packet on every save with fresh
    /// random xmpMM:DocumentID / InstanceID GUIDs and offers no switch to
    /// stop it. The packet is written uncompressed, so each "uuid:<36>" is
    /// overwritten in place with a GUID derived from the template's hash —
    /// same length, so the xref offsets stay valid.
    public static byte[] PinXmpUuids(byte[] pdf, byte[] template)
    {
        var seed = System.Security.Cryptography.SHA256.HashData(template);
        var marker = Encoding.ASCII.GetBytes("uuid:");
        var n = 0;
        var i = pdf.AsSpan().IndexOf(marker);
        while (i >= 0)
        {
            var at = i + marker.Length;
            if (at + 36 <= pdf.Length && pdf[at + 8] == '-' && pdf[at + 13] == '-')
            {
                var bytes = seed.AsSpan(0, 16).ToArray();
                bytes[15] ^= (byte)n++;
                Encoding.ASCII.GetBytes(new Guid(bytes).ToString("D")).CopyTo(pdf, at);
            }
            var next = pdf.AsSpan(at).IndexOf(marker);
            i = next < 0 ? -1 : at + next;
        }
        return pdf;
    }
}

/// The road not taken, kept runnable for the research note: PDFsharp's own
/// AcroForm API (PdfTextField.Text, PdfCheckBoxField.Checked, ...) fed B101's
/// instructions. Usage: dotnet run -c Release -- --highlevel <repo-root> <out-dir>
static class HighLevel
{
    public static void Run(string repo, string outDir)
    {
        var input = FillInput.Load(Path.Combine(outDir, "inputs", "b101.json"));
        Directory.CreateDirectory(Path.Combine(outDir, "highlevel"));
        var doc = PdfReader.Open(Path.Combine(repo, input.Template), PdfDocumentOpenMode.Modify);
        var form = doc.AcroForm ?? throw new InvalidOperationException("no AcroForm");
        var failures = new List<string>();
        foreach (var (name, value) in input.Text)
        {
            try
            {
                switch (form.Fields[name])
                {
                    case PdfSharp.Pdf.AcroForms.PdfTextField t: t.Text = value; break;
                    case PdfSharp.Pdf.AcroForms.PdfComboBoxField c: c.Value = new PdfString(value); break;
                    case var other: failures.Add($"{name}: {other?.GetType().Name ?? "not found"}"); break;
                }
            }
            catch (Exception e) { failures.Add($"{name}: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
        }
        foreach (var (name, export) in input.Buttons)
        {
            try
            {
                switch (form.Fields[name])
                {
                    case PdfSharp.Pdf.AcroForms.PdfCheckBoxField c: c.Checked = true; break;
                    case PdfSharp.Pdf.AcroForms.PdfRadioButtonField r:
                        failures.Add($"{name}: radio by export {export} has no API (SelectedIndex only)"); break;
                    case var other: failures.Add($"{name}: {other?.GetType().Name ?? "not found"}"); break;
                }
            }
            catch (Exception e) { failures.Add($"{name}: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
        }
        try
        {
            doc.Save(Path.Combine(outDir, "highlevel", "b101.pdf"));
        }
        catch (Exception e) { failures.Add($"save: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
        Console.WriteLine($"{failures.Count} failures of {input.Text.Count + input.Buttons.Count} fields");
        foreach (var f in failures.GroupBy(f => f[(f.IndexOf(": ") + 2)..]).Take(8))
            Console.WriteLine($"  {f.Count()} x {f.Key}   e.g. {f.First()[..f.First().IndexOf(": ")]}");
    }
}
