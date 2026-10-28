using System.Text;
using Insolvia.Engines;
using Insolvia.Engines.Projections;
using Insolvia.Forms.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static Insolvia.Forms.Pdf.PdfObjects;

namespace Insolvia.Forms;

/// <summary>
/// The deterministic fill engine (from <c>core/form_fill.py</c>): a release
/// and values keyed by the spec's logical field ids in, the official
/// template with exactly those AcroForm values set — and NOTHING else
/// changed — out. A flat release (no AcroForm) is drawn instead: one overlay
/// stream appended per page. Same input, same bytes.
/// </summary>
/// <remarks>
/// Values are verbatim; how a money line or a date looks is the projection's
/// decision. The engine's refusals are structural: an unknown field id, an
/// export state the PDF does not declare, a district not in the dropdown, a
/// value longer than the box's own character cap, and — new with this port —
/// text the form's own font cannot encode (Insolvia.Forms' README, quirk 3).
/// </remarks>
public static class FormFill
{
    /// <summary>The overlay's default point size.</summary>
    public const double OverlayFontSize = 10.0;

    private const string OverlayFontKey = "/Helv";
    private static readonly byte[] OverlayMarker = "% insolvia-overlay\n"u8.ToArray();

    private static readonly HashSet<string> TextTypes = new(StringComparer.Ordinal)
    {
        "text", "money", "integer", "date", "year", "state", "district", "ssn_itin", "ein", "phone", "email", "signature",
    };

    static FormFill() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>cp1252 — the WinAnsiEncoding the overlay and stamps draw in.</summary>
    internal static Encoding Cp1252 { get; } = InitCp1252();

    /// <summary>Fill the release's official template with <paramref name="values"/>; <see cref="FormFillException"/> lists every problem.</summary>
    public static byte[] Fill(FormRelease release, FieldValues values)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(values);
        var flat = PerWidget(release, values);

        var fieldUpdates = new List<(string Name, string FieldId, string Value, bool Button)>();
        var appearanceTargets = new List<(string Name, WidgetStatesFill Fill)>();
        var draws = new List<(OverlayBox Box, FieldFill Fill)>();
        foreach (var (name, (spec, fill)) in flat)
        {
            if (release.Boxes.TryGetValue(name, out var box))
            {
                draws.Add((box, fill));
                continue;
            }

            switch (fill)
            {
                case TextFill text:
                    fieldUpdates.Add((name, spec.Id, text.Value, false));
                    break;
                case CheckFill { On: true }:
                    fieldUpdates.Add((name, spec.Id, "/" + release.Widgets[name].States[0], true));
                    break;
                case OptionFill option:
                    fieldUpdates.Add((name, spec.Id, "/" + option.Export, true));
                    break;
                case WidgetStatesFill states:
                    appearanceTargets.Add((name, states));
                    break;
                default:
                    break;
            }
        }

        var template = TemplatePdfs.For(release);
        if (release.IsFlat && draws.Count == 0)
        {
            // A notice with nothing to draw: the court's own bytes, untouched.
            return template;
        }

        using var source = new MemoryStream(template, writable: false);
        var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
        if (fieldUpdates.Count > 0)
        {
            ApplyFieldUpdates(doc, fieldUpdates);
        }

        if (appearanceTargets.Count > 0)
        {
            ApplyWidgetStates(doc, appearanceTargets);
        }

        if (draws.Count > 0)
        {
            ApplyOverlay(doc, draws);
        }

        return Determinism.Save(doc, Seed(template, values));
    }

    /// <summary>
    /// Set field values on any AcroForm PDF by qualified name, with no spec
    /// to validate against — pypdf's <c>update_page_form_field_values</c>
    /// path alone. The appearance generator's own tests run it over
    /// synthetic PDFs carrying what no court form does.
    /// </summary>
    internal static byte[] FillFields(byte[] pdf, IReadOnlyList<(string Name, string Value)> values)
    {
        using var source = new MemoryStream(pdf, writable: false);
        var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
        ApplyFieldUpdates(doc, [.. values.Select(v => (v.Name, v.Name, v.Value, false))]);
        return Determinism.Save(doc, pdf);
    }

    /// <summary>The width, in points, <paramref name="value"/> takes in Helvetica (Python's <c>helvetica_width</c>).</summary>
    public static double HelveticaWidth(string value, double size = OverlayFontSize) => Helvetica.Width(value, size);

    private static byte[] Seed(byte[] template, FieldValues values)
    {
        var json = Encoding.UTF8.GetBytes(values.ToJson().ToJsonString());
        return [.. template, .. json];
    }

    private static Encoding InitCp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    // --- validation (_per_widget) ------------------------------------------------

    private static OrderedDictionary<string, (FieldSpec Spec, FieldFill Fill)> PerWidget(FormRelease release, FieldValues values)
    {
        var problems = new List<string>();
        var flat = new OrderedDictionary<string, (FieldSpec, FieldFill)>(StringComparer.Ordinal);
        foreach (var (fieldId, entry) in values)
        {
            FieldSpec spec;
            try
            {
                spec = release.Field(fieldId);
            }
            catch (EngineKeyException)
            {
                problems.Add($"{fieldId}: not a field of {release.ReleaseId}");
                continue;
            }

            List<KeyValuePair<string, FieldFill>> entries;
            if (entry is RowFills rows)
            {
                entries = [.. rows];
                var unknown = entries.Select(pair => pair.Key).Except(spec.PdfNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (unknown.Count > 0)
                {
                    problems.Add($"{fieldId}: not among this field's PDF fields: {PyList(unknown)}");
                    continue;
                }
            }
            else
            {
                if (spec.PdfNames.Count != 1)
                {
                    problems.Add($"{fieldId}: repeats across {spec.PdfNames.Count} PDF fields — address instances by PDF name");
                    continue;
                }

                entries = [new(spec.PdfNames[0], (FieldFill)entry)];
            }

            foreach (var (name, one) in entries)
            {
                if (release.Boxes.TryGetValue(name, out var box))
                {
                    ValidateBox(spec, box, one, problems);
                }
                else
                {
                    ValidateOne(spec, release.Widgets[name], one, problems);
                }

                flat[name] = (spec, one);
            }
        }

        if (problems.Count > 0)
        {
            throw new FormFillException([.. problems.Order(StringComparer.Ordinal)]);
        }

        return flat;
    }

    private static void ValidateOne(FieldSpec spec, Widget widget, FieldFill fill, List<string> problems)
    {
        var where = $"{spec.Id} -> {Engines.Py.Repr(widget.Name)}";
        switch (fill)
        {
            case TextFill text when !TextTypes.Contains(spec.Type):
                problems.Add($"{where}: Text on a {spec.Type} field");
                break;
            case TextFill text:
                CheckText(widget, text, where, problems);
                break;
            case CheckFill when spec.Type != "checkbox":
                problems.Add($"{where}: Check on a {spec.Type} field");
                break;
            case CheckFill when widget.States.Count != 1:
                problems.Add($"{where}: {widget.States.Count} export states — a group is selected with Option, not Check");
                break;
            case OptionFill when spec.Type is not ("radio" or "checkbox"):
                problems.Add($"{where}: Option on a {spec.Type} field");
                break;
            case OptionFill option when !widget.States.Contains(option.Export):
                problems.Add($"{where}: export {Engines.Py.Repr(option.Export)} is not one of the PDF's states {PyList(widget.States.Order(StringComparer.Ordinal))}");
                break;
            case WidgetStatesFill states:
                if (spec.Type is not ("radio" or "checkbox"))
                {
                    problems.Add($"{where}: WidgetStates on a {spec.Type} field");
                }
                else
                {
                    foreach (var state in states.States)
                    {
                        if (!widget.States.Contains(state))
                        {
                            problems.Add($"{where}: state {Engines.Py.Repr(state)} is not one of the PDF's states {PyList(widget.States.Order(StringComparer.Ordinal))}");
                        }
                    }
                }

                if (states.States.Count == 0 && states.Indexes.Count == 0)
                {
                    problems.Add($"{where}: WidgetStates selects nothing");
                }

                break;
            default:
                break;
        }
    }

    private static void CheckText(Widget widget, TextFill fill, string where, List<string> problems)
    {
        if (fill.Value.Length == 0)
        {
            problems.Add($"{where}: empty value — omit the field to leave it blank");
            return;
        }

        if (fill.Value.Contains('\r', StringComparison.Ordinal))
        {
            problems.Add($"{where}: value contains a carriage return");
        }

        if (widget.MaxLen is { } maxLen && Pdf.Py.Length(fill.Value) > maxLen)
        {
            problems.Add($"{where}: {Engines.Py.Repr(fill.Value)} is longer than the PDF's {maxLen}-character box — it would be truncated");
        }

        if (widget.Kind == "choice" && !widget.ChoiceOptions.Contains(fill.Value))
        {
            problems.Add($"{where}: {Engines.Py.Repr(fill.Value)} is not an option of the {Engines.Py.Repr(widget.Name)} dropdown");
        }
    }

    private static void ValidateBox(FieldSpec spec, OverlayBox box, FieldFill fill, List<string> problems)
    {
        var where = $"{spec.Id} -> {Engines.Py.Repr(box.Name)}";
        switch (fill)
        {
            case TextFill when !TextTypes.Contains(spec.Type):
                problems.Add($"{where}: Text on a {spec.Type} field");
                break;
            case TextFill text:
                CheckBoxText(box, text, where, problems);
                break;
            case CheckFill when !box.IsCheck:
                problems.Add($"{where}: Check on a {spec.Type} field");
                break;
            case CheckFill:
                break;
            default:
                problems.Add($"{where}: an overlay box takes Text or Check — a flat form has no export states to select");
                break;
        }
    }

    private static void CheckBoxText(OverlayBox box, TextFill fill, string where, List<string> problems)
    {
        if (fill.Value.Length == 0)
        {
            problems.Add($"{where}: empty value — omit the field to leave it blank");
            return;
        }

        if (fill.Value.Contains('\n', StringComparison.Ordinal) || fill.Value.Contains('\r', StringComparison.Ordinal))
        {
            problems.Add($"{where}: value contains a line break");
        }

        if (!CanEncodeCp1252(fill.Value))
        {
            problems.Add($"{where}: {Engines.Py.Repr(fill.Value)} has a character Helvetica cannot draw");
            return;
        }

        var width = HelveticaWidth(fill.Value, box.Size ?? OverlayFontSize);
        if (width > box.W + 0.01)
        {
            problems.Add($"{where}: {Engines.Py.Repr(fill.Value)} is {PyFormat0(width)} points wide; the printed space is {PyFormat0(box.W)}");
        }
    }

    /// <summary>Python's <c>f"{x:.0f}"</c>: round half to even on the exact value.</summary>
    internal static string PyFormat0(double value) => PyNum.Round(value, 0).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    internal static bool CanEncodeCp1252(string value)
    {
        try
        {
            Cp1252.GetBytes(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Python's <c>repr(list_of_str)</c>.</summary>
    internal static string PyList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Engines.Py.Repr)) + "]";

    // --- the AcroForm write ------------------------------------------------------

    private static void ApplyFieldUpdates(PdfDocument doc, List<(string Name, string FieldId, string Value, bool Button)> updates)
    {
        var acroForm = Dict(doc.Internals.Catalog.Elements["/AcroForm"]) ?? throw new InvalidOperationException("no /AcroForm");

        // auto_regenerate=False: pypdf writes NeedAppearances false.
        acroForm.Elements["/NeedAppearances"] = new PdfBoolean(false);
        var byName = new Dictionary<string, List<(PdfPage Page, PdfDictionary Widget, PdfDictionary Field)>>(StringComparer.Ordinal);
        foreach (var page in doc.Pages)
        {
            foreach (var widget in Widgets(page))
            {
                if (FieldOf(widget) is not { } field || QualifiedName(field) is not { } name)
                {
                    continue;
                }

                if (!byName.TryGetValue(name, out var list))
                {
                    byName[name] = list = [];
                }

                list.Add((page, widget, field));
            }
        }

        var problems = new List<string>();
        foreach (var (name, fieldId, value, _) in updates)
        {
            if (!byName.TryGetValue(name, out var widgets))
            {
                continue;
            }

            try
            {
                foreach (var (page, widget, field) in widgets)
                {
                    SetOne(doc, acroForm, page, widget, field, value);
                }
            }
            catch (UnencodableTextException error)
            {
                problems.Add($"{fieldId} -> {Engines.Py.Repr(name)}: {Engines.Py.Repr(value)} has a character the form's {error.FontName} font cannot draw ({Engines.Py.Repr(error.Characters)})");
            }
            catch (FontNotSupportedException error)
            {
                problems.Add($"{fieldId} -> {Engines.Py.Repr(name)}: {error.Message}");
            }
        }

        if (problems.Count > 0)
        {
            throw new FormFillException([.. problems.Order(StringComparer.Ordinal)]);
        }
    }

    private static void SetOne(PdfDocument doc, PdfDictionary acroForm, PdfPage page, PdfDictionary widget, PdfDictionary field, string value)
    {
        var fieldType = Str(field, "/FT");
        if (fieldType == "/Ch" && field.Elements.ContainsKey("/I"))
        {
            field.Elements.Remove("/I");
        }

        if (fieldType == "/Btn")
        {
            // pypdf writes a button parent's /V as a TEXT string "(/On)";
            // the spec (12.7.4.2) says a name. Corrected here on purpose —
            // Insolvia.Forms' README, quirk 1.
            field.Elements["/V"] = new PdfName(value);
            var normal = Dict(Dict(widget.Elements["/AP"])?.Elements["/N"])
                ?? throw new InvalidOperationException("button without /AP /N");
            var state = normal.Elements.ContainsKey(value) ? value : "/Off";
            widget.Elements["/AS"] = new PdfName(state);
            widget.Elements["/V"] = new PdfName(state);
            return;
        }

        field.Elements["/V"] = TextString(value);
        if (fieldType is "/Tx" or "/Ch")
        {
            TextAppearance.Write(doc, acroForm, page, field, widget);
        }
    }

    /// <summary>A text string as pypdf's <c>TextStringObject</c> writes it: PDFDocEncoding when it can, UTF-16BE otherwise.</summary>
    /// <remarks>
    /// The PDFDocEncoding bytes are computed here, from pypdf's own table,
    /// and handed to PDFsharp raw: PDFsharp 6.2.4's own PDFDocEncoding
    /// encoder lacks some of the table (it writes "Ł" as "?").
    /// </remarks>
    internal static PdfString TextString(string value)
    {
        var pdfDoc = FontData.Encodings["/PDFDocEncoding"];
        var codes = new List<char>();
        foreach (var ch in Pdf.Py.Chars(value))
        {
            var code = System.Array.IndexOf(pdfDoc, ch);
            if (code < 0)
            {
                return new PdfString(value, PdfStringEncoding.Unicode);
            }

            codes.Add((char)code);
        }

        return new PdfString(new string([.. codes]), PdfStringEncoding.RawEncoding);
    }

    /// <summary>The broken-group route: /AS only, widgets indexed in the order the pages list them.</summary>
    private static void ApplyWidgetStates(PdfDocument doc, List<(string Name, WidgetStatesFill Fill)> targets)
    {
        var wanted = targets.ToDictionary(t => t.Name, t => t.Fill, StringComparer.Ordinal);
        var seen = targets.ToDictionary(t => t.Name, _ => 0, StringComparer.Ordinal);
        foreach (var page in doc.Pages)
        {
            foreach (var widget in Widgets(page))
            {
                if (QualifiedName(widget) is not { } name || !seen.TryGetValue(name, out var index))
                {
                    continue;
                }

                seen[name] = index + 1;
                if (Dict(Dict(widget.Elements["/AP"])?.Elements["/N"]) is not { } normal)
                {
                    continue;
                }

                var on = normal.Elements.Keys.Select(key => key.TrimStart('/')).Where(key => key != "Off").ToList();
                if (on.Count != 1)
                {
                    continue;
                }

                var fill = wanted[name];
                if (fill.Indexes.Contains(index) || fill.States.Contains(on[0]))
                {
                    widget.Elements["/AS"] = new PdfName("/" + on[0]);
                }
            }
        }
    }

    // --- the overlay (flat releases) ----------------------------------------------

    private static void ApplyOverlay(PdfDocument doc, List<(OverlayBox Box, FieldFill Fill)> draws)
    {
        foreach (var group in draws.GroupBy(d => d.Box.Page).OrderBy(g => g.Key))
        {
            var page = doc.Pages[group.Key - 1];
            EnsureOverlayFont(doc, page);
            var stream = new PdfDictionary(doc);
            stream.CreateStream(OverlayStream([.. group]));
            doc.Internals.AddObject(stream);
            var parts = new PdfArray(doc);
            var existing = page.Elements["/Contents"];
            if (Resolve(existing) is PdfArray array)
            {
                foreach (var item in array.Elements)
                {
                    parts.Elements.Add(item);
                }
            }
            else if (existing is not null)
            {
                parts.Elements.Add(existing);
            }

            parts.Elements.Add(stream.Reference!);
            page.Elements["/Contents"] = parts;
        }
    }

    /// <summary>One page's overlay stream: the marker, then one named block per box, in box-name order.</summary>
    internal static byte[] OverlayStream(IReadOnlyList<(OverlayBox Box, FieldFill Fill)> draws)
    {
        var output = new List<byte>(OverlayMarker);
        void Ascii(string text) => output.AddRange(Encoding.ASCII.GetBytes(text));
        foreach (var (box, fill) in draws.OrderBy(d => d.Box.Name, StringComparer.Ordinal))
        {
            if (fill is CheckFill check)
            {
                if (!check.On)
                {
                    continue;
                }

                const double inset = 2.5;
                double x1 = box.X + inset, y1 = box.Y + inset;
                double x2 = box.X + box.W - inset, y2 = box.Y + box.H!.Value - inset;
                Ascii($"% box:{box.Name} check\nq 0 G 1 w {F2(x1)} {F2(y1)} m {F2(x2)} {F2(y2)} l S {F2(x1)} {F2(y2)} m {F2(x2)} {F2(y1)} l S Q\n");
            }
            else
            {
                var text = (TextFill)fill;

                // Each value draws in its own saved state — black fill, fill
                // mode, no spacing, 100% scaling, no rise — because a page's
                // own stream may end on any state (B113 page 5 ends white).
                Ascii($"% box:{box.Name}\nq 0 g 0 Tr 0 Tc 0 Tw 100 Tz 0 Ts BT {OverlayFontKey} {F2(box.Size ?? OverlayFontSize)} Tf 1 0 0 1 {F2(box.X)} {F2(box.Y)} Tm ");
                output.AddRange(Literal(text.Value));
                Ascii(" Tj ET Q\n");
            }
        }

        return [.. output];
    }

    /// <summary>Python's <c>f"{x:.2f}"</c>: half-to-even on the exact binary value.</summary>
    internal static string F2(double value) => FixedPoint(value, 2);

    /// <summary>Python's <c>f"{x:.{digits}f}"</c>.</summary>
    internal static string FixedPoint(double value, int digits)
    {
        var rounded = PyNum.Round(value, digits);
        var text = Math.Abs(rounded).ToString("F" + digits, System.Globalization.CultureInfo.InvariantCulture);
        return (double.IsNegative(rounded) && !(rounded == 0 && !double.IsNegative(value)) ? "-" : string.Empty) + text;
    }

    /// <summary><paramref name="value"/> as a PDF literal string in WinAnsiEncoding, escaped (Python's <c>_pdf_literal</c>).</summary>
    internal static byte[] Literal(string value)
    {
        var output = new List<byte> { (byte)'(' };
        foreach (var b in Cp1252.GetBytes(value))
        {
            if (b is (byte)'\\' or (byte)'(' or (byte)')')
            {
                output.Add((byte)'\\');
                output.Add(b);
            }
            else if (b is >= 32 and < 127)
            {
                output.Add(b);
            }
            else
            {
                output.AddRange(Encoding.ASCII.GetBytes("\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
            }
        }

        output.Add((byte)')');
        return [.. output];
    }

    private static void EnsureOverlayFont(PdfDocument doc, PdfPage page)
    {
        if (Dict(page.Elements["/Resources"]) is not { } resources)
        {
            resources = new PdfDictionary(doc);
            page.Elements["/Resources"] = resources;
        }

        if (Dict(resources.Elements["/Font"]) is not { } fonts)
        {
            fonts = new PdfDictionary(doc);
            resources.Elements["/Font"] = fonts;
        }

        if (fonts.Elements.ContainsKey(OverlayFontKey))
        {
            return;
        }

        fonts.Elements[OverlayFontKey] = PdfDraw.HelveticaDictionary(doc);
    }
}

/// <summary>A value that cannot land on the form as asked. Carries every problem found.</summary>
public sealed class FormFillException : Exception
{
    /// <summary>Creates the exception from the problems found.</summary>
    public FormFillException(IReadOnlyList<string> problems)
        : base(string.Join("; ", problems ?? []))
    {
        Problems = [.. problems ?? []];
    }

    /// <summary>Creates the exception with no problems.</summary>
    public FormFillException()
        : this([])
    {
    }

    /// <summary>Creates the exception with one problem.</summary>
    public FormFillException(string message)
        : this([message])
    {
    }

    /// <summary>Creates the exception with one problem and its cause.</summary>
    public FormFillException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [message];
    }

    /// <summary>Every problem, sorted.</summary>
    public IReadOnlyList<string> Problems { get; }
}
