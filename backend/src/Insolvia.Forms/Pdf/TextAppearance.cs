using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static Insolvia.Forms.Pdf.PdfObjects;

namespace Insolvia.Forms.Pdf;

/// <summary>
/// The text appearance stream of a text or choice widget — a port of pypdf
/// 6.16.2's <c>TextStreamAppearance.from_text_annotation</c>
/// (<c>pypdf/generic/_appearance_stream.py</c>), the path services/api runs
/// with <c>auto_regenerate=False</c>. Numbers print the way Python's
/// f-strings print them (<see cref="PyNum"/>), so the streams are byte-equal
/// to pypdf's.
/// </summary>
/// <remarks>
/// Covered, as pypdf does them: the /DA parse and its inherited fallback,
/// margins from /BS, /Q alignment, multiline wrap-and-shrink, comb cells,
/// auto-size at size 0 (which the court PDFs' comb fields take — their /DA
/// says <c>0 Tf</c>), list boxes (the options joined, the selection boxed),
/// page /Rotate, widget rotation (/MK /R, as a /Matrix), any simple font in
/// /DR (the 14 core fonts by their metrics, any other by its /Widths), a /DA
/// font missing from /DR (a core font by name, else Helvetica, added to /DR
/// and the /DA rewritten), and non-ASCII text the font's encoding carries.
/// Refused, where pypdf's answer is not one to copy: text the font's own
/// encoding cannot draw — pypdf then tries other code pages in a Python set's
/// hash order (so the same input can render differently in two processes) or
/// prints "?" — and composite or Type3 fonts. See Insolvia.Forms' README.
/// </remarks>
internal static class TextAppearance
{
    private const double TextSpaceToGlyphSpace = 1000;
    private static readonly PyNum MultilineDefaultSize = PyNum.Int(12);

    /// <summary>Generate and install the widget's appearance (pypdf's write path, with the shared /N replaced in place).</summary>
    public static void Write(PdfDocument doc, PdfDictionary acroForm, PdfPage page, PdfDictionary field, PdfDictionary widget)
    {
        var rect = Array(widget.Elements["/Rect"]) ?? throw new InvalidOperationException("widget without /Rect");
        double r0 = Num(rect.Elements[0]), r1 = Num(rect.Elements[1]), r2 = Num(rect.Elements[2]), r3 = Num(rect.Elements[3]);
        var pageRotate = Inherited(page, "/Rotate") is { } rotateItem && IsNumber(rotateItem) ? Num(rotateItem) : 0;
        var (width, height) = pageRotate is 90 or 270
            ? (Math.Abs(r3 - r1), Math.Abs(r2 - r0))
            : (Math.Abs(r2 - r0), Math.Abs(r3 - r1));

        var daItem = Inherited(widget, "/DA") ?? acroForm.Elements["/DA"];
        var defaultAppearance = Str(daItem) is { Length: > 0 } daText ? daText : "/Helv 0 Tf 0 g";

        var flags = field.Elements.ContainsKey("/Ff") ? (long)Num(field.Elements["/Ff"]) : 0;
        var fieldType = Str(field, "/FT") ?? "/Tx";
        string text;
        List<string> selection;
        if (fieldType == "/Ch" && (flags & (1 << 17)) == 0)
        {
            var options = Array(Inherited(widget, "/Opt")) ?? new PdfArray();
            text = string.Join('\n', options.Elements.Select(option => TextValue(option)
                ?? throw new FontNotSupportedException("a list box whose options are [export, display] pairs is not supported (pypdf cannot join them either)")));
            var value = Resolve(field.Elements["/V"]);
            selection = value is PdfArray many
                ? [.. many.Elements.Select(item => TextValue(item) ?? string.Empty)]
                : value is null ? [] : [TextValue(value) ?? string.Empty];
        }
        else
        {
            text = TextValue(field.Elements["/V"]) ?? string.Empty;
            selection = [];
        }

        var properties = defaultAppearance.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var tf = properties.IndexOf("Tf");
        var daFontName = properties[tf - 2];
        properties.RemoveAt(tf - 2);
        tf = properties.IndexOf("Tf");
        var fontSize = PyNum.Float(double.Parse(properties[tf - 1], System.Globalization.CultureInfo.InvariantCulture));
        properties.RemoveAt(tf - 1);
        properties.Remove("Tf");
        var fontColor = string.Join(' ', properties);

        var (fontName, font) = FindFont(daFontName, widget, acroForm, text);
        if (fontName != daFontName)
        {
            widget.Elements["/DA"] = new PdfString(defaultAppearance.Replace(daFontName, fontName, StringComparison.Ordinal));
        }

        var fontReference = SyncFontResource(doc, acroForm, fontName, font);

        var isComb = (flags & (1 << 24)) != 0;
        int? maxLength = isComb && widget.Elements.ContainsKey("/MaxLen") ? (int)Num(widget.Elements["/MaxLen"]) : null;
        var isMultiline = (flags & (1 << 12)) != 0;
        var alignment = field.Elements.ContainsKey("/Q") ? (int)Num(field.Elements["/Q"]) : 0;
        var borderWidth = PyNum.Int(1);
        var borderStyle = "/S";
        if (Dict(field.Elements["/BS"]) is { } bs)
        {
            if (bs.Elements.ContainsKey("/W"))
            {
                borderWidth = PyNumber(bs.Elements["/W"]);
            }

            borderStyle = Str(bs, "/S") ?? borderStyle;
        }

        var rotation = 0;
        if (Dict(Inherited(field, "/MK")) is { } mk && mk.Elements.ContainsKey("/R"))
        {
            rotation = (int)Num(mk.Elements["/R"]);
        }

        var data = Stream(text, selection, font, fontName, fontSize, fontColor, isMultiline, alignment, isComb, maxLength, width, height, borderWidth, borderStyle);

        var stream = new PdfDictionary(doc);
        stream.Elements["/Type"] = new PdfName("/XObject");
        stream.Elements["/Subtype"] = new PdfName("/Form");
        stream.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(width), new PdfReal(height));
        if (rotation % 360 is var turned && turned != 0)
        {
            stream.Elements["/Matrix"] = RotationMatrix(doc, ((turned % 360) + 360) % 360, width, height);
        }

        var fonts = new PdfDictionary(doc);
        fonts.Elements[fontName] = fontReference;
        var resources = new PdfDictionary(doc);
        resources.Elements["/Font"] = fonts;
        stream.Elements["/Resources"] = resources;

        var ap = Dict(widget.Elements["/AP"]);
        if (Dict(ap?.Elements["/N"]) is { } old)
        {
            foreach (var key in old.Elements.Keys.ToList())
            {
                if (key is "/BBox" or "/Length" or "/Subtype" or "/Type" or "/Filter")
                {
                    continue;
                }

                if (key == "/Resources")
                {
                    // pypdf adds the font to the OLD resources dictionary too
                    // (it mutates it), and keeps its own.
                    if (Dict(old.Elements["/Resources"]) is { } oldResources)
                    {
                        if (Dict(oldResources.Elements["/Font"]) is not { } oldFonts)
                        {
                            oldFonts = new PdfDictionary(doc);
                            oldResources.Elements["/Font"] = oldFonts;
                        }

                        oldFonts.Elements[fontName] = fontReference;
                    }

                    continue;
                }

                stream.Elements[key] = old.Elements[key];
            }
        }

        if (ap?.Elements["/N"] is PdfReference oldReference && oldReference.Value is PdfDictionary target)
        {
            // pypdf replaces the existing /N OBJECT in place. The court PDFs
            // share one /N among a field's widgets (B106D's "Debtor 1" on
            // three pages), so the last widget's box wins for all of them —
            // kept on purpose (Insolvia.Forms' README, quirk 2).
            var elements = stream.Elements.Keys.Select(key => (key, stream.Elements[key])).ToList();
            target.Stream = null!;
            target.Elements.Clear();
            foreach (var (key, value) in elements)
            {
                target.Elements[key] = value;
            }

            target.CreateStream(data);
            return;
        }

        if (ap?.Elements["/N"] is { } direct)
        {
            throw new FontNotSupportedException($"a direct (not indirect) /AP /N on widget {QualifiedName(field)} is not supported (pypdf cannot replace one either)");
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

    /// <summary>
    /// The font the widget draws with, by pypdf's <c>_find_annotation_font_resource</c>:
    /// /DR (the widget's inherited one, else the AcroForm's), else a core
    /// font by the /DA name, else Helvetica. Text the font cannot encode is
    /// refused (see the class remarks).
    /// </summary>
    private static (string FontName, Font Font) FindFont(string fontName, PdfDictionary widget, PdfDictionary acroForm, string text)
    {
        var resources = Dict(Inherited(widget, "/DR")) ?? Dict(acroForm.Elements["/DR"]);
        var fonts = Dict(resources?.Elements["/Font"]);
        Font font;
        if (Dict(fonts?.Elements[fontName]) is { } resource)
        {
            font = Font.FromResource(resource);
        }
        else
        {
            if (!FontData.Core.ContainsKey(fontName.TrimStart('/')))
            {
                fontName = "/Helvetica";
            }

            font = Font.FromCoreName(fontName);
        }

        if (!font.CanEncode(text))
        {
            var missing = string.Concat(Py.Chars(text).Where(ch => !font.Encoding.Values.Contains(ch)).Distinct(StringComparer.Ordinal));
            throw new UnencodableTextException(font.Name, missing);
        }

        return (fontName, font);
    }

    /// <summary>pypdf's <c>_sync_appearance_stream_font_resources</c>: the /DR /Font entry, added when missing.</summary>
    private static PdfItem SyncFontResource(PdfDocument doc, PdfDictionary acroForm, string fontName, Font font)
    {
        if (Dict(acroForm.Elements["/DR"]) is not { } dr)
        {
            dr = new PdfDictionary(doc);
            acroForm.Elements["/DR"] = dr;
        }

        if (Dict(dr.Elements["/Font"]) is not { } fonts)
        {
            fonts = new PdfDictionary(doc);
            dr.Elements["/Font"] = fonts;
        }

        if (fonts.Elements.ContainsKey(fontName))
        {
            var existing = fonts.Elements[fontName];
            if (existing is PdfReference)
            {
                return existing;
            }

            throw new FontNotSupportedException($"font {fontName} is a direct object in /DR; pypdf would write a null reference for it");
        }

        // Font.as_font_resource for a simple font in WinAnsiEncoding: the only
        // kind this path creates (a core font by name, or the Helvetica fallback).
        var differs = Enumerable.Range(0, 256).Any(code =>
            FontData.Cp1252[code] is { } winAnsi
            && font.Encoding.TryGetValue(code, out var own)
            && own.Length > 0
            && own != winAnsi);
        if (differs)
        {
            throw new FontNotSupportedException($"font {fontName} would need a /Differences encoding to be added to /DR");
        }

        var resource = new PdfDictionary(doc);
        resource.Elements["/Type"] = new PdfName("/Font");
        resource.Elements["/Subtype"] = new PdfName("/Type1");
        resource.Elements["/Name"] = new PdfName("/" + font.Name);
        resource.Elements["/BaseFont"] = new PdfName("/" + font.Name);
        resource.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        doc.Internals.AddObject(resource);
        fonts.Elements[fontName] = resource.Reference;
        return resource.Reference!;
    }

    /// <summary>pypdf's <c>_add_matrix</c>: rotate the box about the origin, then translate it back into the positive quadrant; rounded to 3 places.</summary>
    private static PdfArray RotationMatrix(PdfDocument doc, int degrees, double width, double height)
    {
        var radians = degrees * Math.PI / 180.0;
        double a = Math.Cos(radians), b = Math.Sin(radians), c = -Math.Sin(radians), d = Math.Cos(radians);
        (double X, double Y) Apply(double x, double y) => ((a * x) + (c * y), (b * x) + (d * y));
        var bottomRight = Apply(width, 0);
        var topLeft = Apply(0, height);
        var topRight = Apply(width, height);
        var tx = -Math.Min(Math.Min(0.0, bottomRight.X), Math.Min(topLeft.X, topRight.X));
        var ty = -Math.Min(Math.Min(0.0, bottomRight.Y), Math.Min(topLeft.Y, topRight.Y));
        double[] ctm = [a, b, c, d, tx, ty];
        return new PdfArray(doc, [.. ctm.Select(v => (PdfItem)new PdfReal(PyNum.Round(v, 3)))]);
    }

    private sealed record Line(PyNum Width, string Word, string Glyphs);

    /// <summary>pypdf's <c>_generate_appearance_stream_data</c>.</summary>
    internal static byte[] Stream(
        string text,
        IReadOnlyList<string> selection,
        Font font,
        string fontName,
        PyNum fontSize,
        string fontColor,
        bool isMultiline,
        int alignment,
        bool isComb,
        int? maxLength,
        double rectWidth,
        double rectHeight,
        PyNum borderWidth,
        string borderStyle)
    {
        var width = PyNum.Float(rectWidth);
        var height = PyNum.Float(rectHeight);
        var bbox = font.Descriptor.BBox;
        var leadingFactor = PyNum.Float((bbox[3] - bbox[1]) / TextSpaceToGlyphSpace);
        var factor = PyNum.Int(borderStyle is "/B" or "/I" ? 2 : 1);
        var margin = PyNum.Max(borderWidth * factor, PyNum.Int(1));
        var fieldHeight = height - (PyNum.Int(2) * margin);
        var fieldWidth = width - (PyNum.Int(4) * margin);
        var thousand = PyNum.Int(1000);

        List<Line> lines;
        if (fontSize.Value == 0)
        {
            var minFontSize = PyNum.Float(4.0);
            if (selection.Count > 0)
            {
                isMultiline = false;
            }

            if (isMultiline)
            {
                var paragraphs = new List<List<Line>>();
                foreach (var line in Py.SplitLines(text))
                {
                    if (Py.IsBlank(line))
                    {
                        paragraphs.Add([new Line(PyNum.Float(0.0), string.Empty, string.Empty)]);
                        continue;
                    }

                    paragraphs.Add([.. line.Split(' ').Select(word => new Line(PyNum.Float(font.TextWidth(word)) / thousand, word, font.ToGlyphs(word)))]);
                }

                (lines, fontSize) = ScaleText(font, MultilineDefaultSize, leadingFactor, fieldWidth, fieldHeight, paragraphs, minFontSize);
            }
            else
            {
                var maxVertical = fieldHeight / leadingFactor;
                var glyphs = font.ToGlyphs(text);
                var unscaled = PyNum.Float(font.TextWidth(glyphs)) / thousand;
                var maxHorizontal = fieldWidth / (unscaled.Value == 0 ? PyNum.Int(1) : unscaled);
                fontSize = PyNum.Round1(PyNum.Max(PyNum.Min(maxVertical, maxHorizontal), minFontSize));
                lines = [new Line(unscaled * fontSize, text, glyphs)];
            }
        }
        else if (isComb)
        {
            lines = [];
            var index = 0;
            var limit = maxLength is > 0 ? maxLength.Value : Py.Length(text);
            foreach (var ch in Py.Chars(text))
            {
                if (index < limit)
                {
                    var glyphs = font.ToGlyphs(ch);
                    lines.Add(new Line(PyNum.Float(font.TextWidth(glyphs)) * fontSize / thousand, ch, glyphs));
                }

                index++;
            }
        }
        else
        {
            lines = [.. Py.SplitLines(text).Select(line =>
            {
                var glyphs = font.ToGlyphs(line);
                return new Line(PyNum.Float(font.TextWidth(glyphs)) * fontSize / thousand, line, glyphs);
            })];
        }

        var yOffset = isMultiline
            ? height + margin - (PyNum.Float(bbox[3]) * fontSize / thousand)
            : margin + ((fieldHeight - (PyNum.Float(font.Descriptor.Ascent) * fontSize / thousand)) / PyNum.Int(2));
        var defaultAppearance = $"{fontName} {fontSize} Tf {fontColor}";

        var output = new List<byte>();
        void Emit(string ascii) => output.AddRange(Encoding.UTF8.GetBytes(ascii));

        Emit($"q\n/Tx BMC \nq\n{PyNum.Int(2) * margin} {margin} {fieldWidth} {fieldHeight} re\nW\nBT\n{defaultAppearance}\n");
        var currentX = PyNum.Int(0);
        var selectedGlyphs = selection.Count > 0 ? font.ToGlyphs(string.Concat(selection)) : string.Empty;
        for (var lineNumber = 0; lineNumber < lines.Count; lineNumber++)
        {
            var line = lines[lineNumber];
            if (selection.Count > 0 && selectedGlyphs.Contains(line.Glyphs, StringComparison.Ordinal))
            {
                var top = yOffset - (PyNum.Int(lineNumber) * fontSize * leadingFactor) - PyNum.Int(1);
                Emit($"1 {top} {width - PyNum.Int(2)} {fontSize + PyNum.Int(2)} re\n0.5 0.5 0.5 rg s\n{defaultAppearance}\n");
            }

            PyNum desired;
            if (isComb && maxLength is > 0)
            {
                var cell = width / PyNum.Int(maxLength.Value);
                desired = (PyNum.Int(lineNumber) * cell) + ((cell - line.Width) / PyNum.Int(2));
            }
            else if (alignment == 2)
            {
                desired = width - (margin * PyNum.Int(2)) - line.Width;
            }
            else if (alignment == 1)
            {
                desired = (width - line.Width) / PyNum.Int(2);
            }
            else
            {
                desired = margin * PyNum.Int(2);
            }

            var xRel = desired - currentX;
            var yRel = lineNumber == 0
                ? yOffset
                : isComb ? PyNum.Float(0.0) : -fontSize * leadingFactor;
            Emit($"{xRel} {yRel} Td\n");
            currentX = desired;

            var shown = Font.GlyphBytes(line.Glyphs);
            output.Add((byte)'(');
            foreach (var b in shown)
            {
                if (b is (byte)'\\' or (byte)'(' or (byte)')')
                {
                    output.Add((byte)'\\');
                }

                output.Add(b);
            }

            Emit(") Tj\n");
        }

        Emit("ET\nQ\nEMC\nQ\n");
        return [.. output];
    }

    /// <summary>pypdf's <c>_scale_text</c>: wrap by words, and shrink by 0.2 pt until the lines fit.</summary>
    private static (List<Line> Lines, PyNum FontSize) ScaleText(Font font, PyNum fontSize, PyNum leadingFactor, PyNum fieldWidth, PyNum fieldHeight, List<List<Line>> paragraphs, PyNum minFontSize)
    {
        var step = PyNum.Float(0.2);
        while (true)
        {
            var wrapped = new List<Line>();
            var current = new List<Line>();
            var currentWidth = PyNum.Int(0);
            var spaceWidth = PyNum.Float(font.SpaceWidth) * fontSize / PyNum.Int(1000);
            foreach (var paragraph in paragraphs)
            {
                for (var i = 0; i < paragraph.Count; i++)
                {
                    var word = paragraph[i];
                    var wordWidth = word.Width * fontSize;
                    var testWidth = currentWidth + wordWidth + (i > 0 ? spaceWidth : PyNum.Int(0));
                    if (testWidth.Value > fieldWidth.Value && current.Count > 0)
                    {
                        wrapped.Add(Joined(current, currentWidth, font));
                        current = [word];
                        currentWidth = wordWidth;
                    }
                    else if (current.Count == 0 && wordWidth.Value > fieldWidth.Value)
                    {
                        wrapped.Add(new Line(wordWidth, word.Word, word.Glyphs));
                        current = [];
                        currentWidth = PyNum.Int(0);
                    }
                    else
                    {
                        if (current.Count > 0)
                        {
                            currentWidth += spaceWidth;
                        }

                        current.Add(word);
                        currentWidth += wordWidth;
                    }
                }

                if (current.Count > 0)
                {
                    wrapped.Add(Joined(current, currentWidth, font));
                    current = [];
                    currentWidth = PyNum.Int(0);
                }
            }

            var estimated = fontSize + (PyNum.Int(wrapped.Count - 1) * leadingFactor * fontSize);
            if (estimated.Value > fieldHeight.Value)
            {
                var smaller = fontSize - step;
                if (smaller.Value >= minFontSize.Value)
                {
                    fontSize = smaller;
                    continue;
                }
            }

            return (wrapped, PyNum.Round1(fontSize));
        }
    }

    private static Line Joined(List<Line> words, PyNum width, Font font) =>
        new(width, string.Join(' ', words.Select(w => w.Word)), string.Join(font.SpaceChar, words.Select(w => w.Glyphs)));
}

/// <summary>Text a widget's font cannot encode — refused rather than drawn as "?" or re-encoded nondeterministically.</summary>
public sealed class UnencodableTextException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UnencodableTextException(string fontName, string characters)
        : base($"{characters} cannot be drawn in the form's {fontName} font")
    {
        FontName = fontName;
        Characters = characters;
    }

    /// <summary>Creates the exception.</summary>
    public UnencodableTextException()
    {
        FontName = string.Empty;
        Characters = string.Empty;
    }

    /// <summary>Creates the exception.</summary>
    public UnencodableTextException(string message)
        : base(message)
    {
        FontName = string.Empty;
        Characters = string.Empty;
    }

    /// <summary>Creates the exception.</summary>
    public UnencodableTextException(string message, Exception innerException)
        : base(message, innerException)
    {
        FontName = string.Empty;
        Characters = string.Empty;
    }

    /// <summary>The font's name.</summary>
    public string FontName { get; }

    /// <summary>The characters it lacks.</summary>
    public string Characters { get; }
}
