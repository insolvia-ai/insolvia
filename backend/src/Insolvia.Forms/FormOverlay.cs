using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Core.Debtors;
using Insolvia.Engines.Projections;
using Insolvia.Forms.Pdf;
using PdfSharp.Pdf.IO;

namespace Insolvia.Forms;

/// <summary>Which pages of a form an output keeps: every page, only the signature pages, or every page but them.</summary>
public enum SignaturePagesMode
{
    /// <summary>Every page.</summary>
    All,

    /// <summary>Only the signature pages.</summary>
    Only,

    /// <summary>Every page except the signature pages.</summary>
    Omit,
}

/// <summary>
/// One set of output options (from <c>core/form_overlay.py</c>, issue
/// 13.11), shared by packet assembly and the single-form preview. Every
/// member defaults to "the plain filing set".
/// </summary>
public sealed record OutputOptions
{
    /// <summary>The "Draft" watermark.</summary>
    public bool DraftWatermark { get; init; }

    /// <summary>The top-margin date/time stamp (and, with <see cref="SignElectronically"/>, today's signature date).</summary>
    public bool PrintDate { get; init; }

    /// <summary>Signature-page selection.</summary>
    public SignaturePagesMode SignaturePages { get; init; } = SignaturePagesMode.All;

    /// <summary><c>/s/ Name</c> on every debtor signature line.</summary>
    public bool SignElectronically { get; init; }

    /// <summary>The subset of the case's filed forms to render, by short key (<c>b101</c>); null is every form.</summary>
    public ValueCollection<string>? Forms { get; init; }

    /// <summary>An amendment: only the schedules carrying an amended item, plus B106Sum/B106Dec and a cover sheet.</summary>
    public bool AmendedOnly { get; init; }

    /// <summary>The plain filing set — no stamp, no page filter, no subset.</summary>
    public bool IsDefault => this == FormOverlay.DefaultOutputOptions;
}

/// <summary>
/// Output options (from <c>core/form_overlay.py</c>): <c>/s/</c> signatures
/// and the "amended filing" caption as real field values applied before the
/// fill; signature-page selection and the DRAFT/print-date stamps as a pass
/// over the filled bytes after it. Pure over its inputs; the stamp's clock
/// reading is the caller's.
/// </summary>
public static partial class FormOverlay
{
    /// <summary>The plain filing set.</summary>
    public static readonly OutputOptions DefaultOutputOptions = new();

    /// <summary>The three signature-page modes, by their wire names.</summary>
    public static readonly IReadOnlyList<string> SignaturePagesModes = ["all", "only", "omit"];

    private const string WatermarkText = "DRAFT";
    private const double WatermarkGray = 0.75;
    private const double WatermarkRotateDegrees = 45.0;
    private const double StampGray = 0.25;
    private const double StampFontSize = 8.0;
    private const double StampMarginPoints = 24.0;

    private static readonly string[] AmendedCaptionFieldIds = ["caption.amended_filing", "caption.amended_or_supplement"];

    /// <summary>The wire name of a signature-pages mode.</summary>
    public static string Wire(SignaturePagesMode mode) => mode switch
    {
        SignaturePagesMode.Only => "only",
        SignaturePagesMode.Omit => "omit",
        _ => "all",
    };

    /// <summary>The wire shape recorded on a packet and echoed by the preview route: always the full shape.</summary>
    public static JsonObject OutputOptionsJson(OutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var body = new JsonObject
        {
            ["draftWatermark"] = options.DraftWatermark,
            ["printDate"] = options.PrintDate,
            ["signaturePages"] = Wire(options.SignaturePages),
            ["signElectronically"] = options.SignElectronically,
            ["amendedOnly"] = options.AmendedOnly,
        };
        if (options.Forms is { } forms)
        {
            body["forms"] = new JsonArray([.. forms.Select(form => (JsonNode)form)]);
        }

        return body;
    }

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex FormKey();

    /// <summary>
    /// Validate the JSON shape <see cref="OutputOptionsJson"/> produces; every
    /// key optional. <see cref="FieldValidationException"/> carries one message
    /// per bad field. <paramref name="allowForms"/> false is the preview
    /// route's rule: the form is already named in the URL.
    /// </summary>
    public static OutputOptions ParseOutputOptions(JsonNode? raw, bool allowForms)
    {
        raw ??= new JsonObject();
        if (raw is not JsonObject body)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["options"] = "options must be a JSON object." });
        }

        var errors = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        string[] known = ["draftWatermark", "printDate", "signaturePages", "signElectronically", "forms", "amendedOnly"];
        var unknown = body.Select(pair => pair.Key).Where(key => !known.Contains(key)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            errors["options"] = $"unknown option(s): {string.Join(", ", unknown)}.";
        }

        bool Bool(string key)
        {
            if (!body.TryGetPropertyValue(key, out var value))
            {
                return false;
            }

            if (value is JsonValue v && v.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            errors[key] = "must be a boolean.";
            return false;
        }

        var draftWatermark = Bool("draftWatermark");
        var printDate = Bool("printDate");
        var signElectronically = Bool("signElectronically");
        var amendedOnly = Bool("amendedOnly");

        var signaturePages = SignaturePagesMode.All;
        if (body.TryGetPropertyValue("signaturePages", out var pagesNode))
        {
            var text = pagesNode is JsonValue pv && pv.TryGetValue<string>(out var s) ? s : null;
            var index = text is null ? -1 : SignaturePagesModes.ToList().IndexOf(text);
            if (index < 0)
            {
                errors["signaturePages"] = "must be one of " + string.Join(", ", SignaturePagesModes) + ".";
            }
            else
            {
                signaturePages = (SignaturePagesMode)index;
            }
        }

        ValueCollection<string>? forms = null;
        if (allowForms && body.TryGetPropertyValue("forms", out var formsNode))
        {
            var list = formsNode as JsonArray;
            var keys = list?.Select(item => item is JsonValue iv && iv.TryGetValue<string>(out var key) ? key : null).ToList();
            if (list is null || list.Count == 0 || keys!.Any(key => key is null || !FormKey().IsMatch(key)))
            {
                errors["forms"] = "must be a non-empty list of lower-case form keys (e.g. \"b101\").";
            }
            else if (keys!.Distinct(StringComparer.Ordinal).Count() != keys!.Count)
            {
                errors["forms"] = "names the same form more than once.";
            }
            else
            {
                forms = keys!.Select(key => key!).ToValueCollection();
            }
        }
        else if (!allowForms && body.ContainsKey("forms"))
        {
            errors["forms"] = "this endpoint already names one form; omit it.";
        }

        if (amendedOnly && forms is not null)
        {
            errors["amendedOnly"] = "cannot be combined with an explicit forms subset.";
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return new OutputOptions
        {
            DraftWatermark = draftWatermark,
            PrintDate = printDate,
            SignaturePages = signaturePages,
            SignElectronically = signElectronically,
            Forms = forms,
            AmendedOnly = amendedOnly,
        };
    }

    /// <summary>The preview route's adapter: query-string spellings turned into the JSON shape <see cref="ParseOutputOptions"/> validates.</summary>
    public static OutputOptions ParseOutputOptionsQuery(IReadOnlyDictionary<string, string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var raw = new JsonObject();
        foreach (var key in new[] { "draftWatermark", "printDate", "signElectronically", "amendedOnly" })
        {
            if (args.TryGetValue(key, out var value))
            {
                raw[key] = value.Trim().ToLowerInvariant() is not ("" or "0" or "false");
            }
        }

        if (args.TryGetValue("signaturePages", out var pages))
        {
            raw["signaturePages"] = pages;
        }

        if (args.TryGetValue("forms", out var forms))
        {
            raw["forms"] = new JsonArray([.. forms.Split(',').Select(form => (JsonNode)form)]);
        }

        return ParseOutputOptions(raw, allowForms: false);
    }

    // ── /s/ electronic signatures ──────────────────────────────────────────

    [GeneratedRegex("[._](signature_date|executed_on|date_signed|signature|date)$")]
    private static partial Regex IdSuffix();

    [GeneratedRegex("debtor(1|2)", RegexOptions.IgnoreCase)]
    private static partial Regex DebtorId();

    private static string SignatureStem(string fieldId) => IdSuffix().Replace(fieldId, string.Empty, 1);

    private static FieldSpec? PairedDateField(FormRelease release, FieldSpec signature)
    {
        var stem = SignatureStem(signature.Id);
        return release.Fields.FirstOrDefault(candidate => candidate.Type == "date" && candidate.Id != signature.Id && SignatureStem(candidate.Id) == stem);
    }

    /// <summary>
    /// The projection's values with <c>/s/ name</c> (and, with
    /// <see cref="OutputOptions.PrintDate"/>, today's date) on every DEBTOR
    /// signature line — <paramref name="values"/> itself when
    /// <see cref="OutputOptions.SignElectronically"/> is off. An attorney's
    /// line stays wet.
    /// </summary>
    public static FieldValues ApplySignatureOptions(FormRelease release, FieldValues values, CaseFile caseFile, OutputOptions options, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(caseFile);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.SignElectronically)
        {
            return values;
        }

        var augmented = Copy(values);
        foreach (var field in release.Fields)
        {
            if (field.Type != "signature" || field.PdfNames.Count != 1)
            {
                continue;
            }

            var match = DebtorId().Match(field.Id);
            if (!match.Success)
            {
                continue;
            }

            var role = match.Groups[1].Value == "1" ? FilingRole.Debtor1 : FilingRole.Debtor2;
            if (ProjectionHelpers.FullName(caseFile.Debtor(role)) is not { Length: > 0 } name)
            {
                continue;
            }

            augmented[field.Id] = new TextFill($"/s/ {name}");
            if (options.PrintDate && PairedDateField(release, field) is { PdfNames.Count: 1 } dateField)
            {
                augmented[dateField.Id] = new TextFill(ProjectionHelpers.FormatDate(today));
            }
        }

        return augmented;
    }

    /// <summary>
    /// Tick this release's own "amended filing" caption — <paramref name="values"/>
    /// itself when <paramref name="amended"/> is false or the release prints
    /// no such caption. The two-way radio takes the option the spec maps from
    /// <c>case.is_amended</c>.
    /// </summary>
    public static FieldValues ApplyAmendmentOptions(FormRelease release, FieldValues values, bool amended)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(values);
        if (!amended)
        {
            return values;
        }

        var ids = release.Fields.Select(field => field.Id).ToHashSet(StringComparer.Ordinal);
        var fieldId = AmendedCaptionFieldIds.FirstOrDefault(ids.Contains);
        if (fieldId is null)
        {
            return values;
        }

        var spec = release.Field(fieldId);
        var augmented = Copy(values);
        augmented[fieldId] = spec.Type == "radio"
            ? new OptionFill(spec.Options.First(option => option.MapsToValue == "case.is_amended").Value)
            : new CheckFill();
        return augmented;
    }

    private static FieldValues Copy(FieldValues values)
    {
        var copy = new FieldValues();
        foreach (var (fieldId, entry) in values)
        {
            copy[fieldId] = entry;
        }

        return copy;
    }

    // ── Page selection ──────────────────────────────────────────────────────

    /// <summary>
    /// Keep every page, only the signature pages, or every page but them;
    /// null when the mode leaves no page at all.
    /// </summary>
    public static byte[]? SelectPages(byte[] pdf, FormRelease release, SignaturePagesMode mode)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(release);
        if (mode == SignaturePagesMode.All)
        {
            return pdf;
        }

        using var source = new MemoryStream(pdf, writable: false);
        var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
        var total = doc.PageCount;
        var signature = release.SignaturePages.ToHashSet();
        var keep = mode == SignaturePagesMode.Only
            ? [.. signature.Where(p => p >= 1 && p <= total).Order()]
            : Enumerable.Range(1, total).Where(p => !signature.Contains(p)).ToList();
        if (keep.Count == 0)
        {
            return null;
        }

        var keepZeroBased = keep.Select(p => p - 1).ToHashSet();
        for (var index = total - 1; index >= 0; index--)
        {
            if (!keepZeroBased.Contains(index))
            {
                doc.Pages.RemoveAt(index);
            }
        }

        return Determinism.Save(doc, pdf);
    }

    // ── Stamps ──────────────────────────────────────────────────────────────

    /// <summary>The top-margin mark: date and time, UTC, stated as such.</summary>
    public static string StampText(DateTimeOffset printedAt) =>
        "Printed " + printedAt.DateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>
    /// Draw the requested marks on every page — <paramref name="pdf"/> itself,
    /// never re-serialised, when both are off.
    /// </summary>
    public static byte[] StampPages(byte[] pdf, bool draftWatermark, bool printDate, DateTimeOffset printedAt)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!draftWatermark && !printDate)
        {
            return pdf;
        }

        using var source = new MemoryStream(pdf, writable: false);
        var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
        var font = PdfDraw.AddHelvetica(doc);
        var stamp = printDate ? StampText(printedAt) : null;
        foreach (var page in doc.Pages)
        {
            var (width, height) = PdfObjects.MediaBoxSize(page);
            var ops = new List<byte>();
            if (draftWatermark)
            {
                ops.AddRange(PdfDraw.TextOps(WatermarkText, width * 0.22, height * 0.30, Math.Min(width, height) * 0.16, WatermarkRotateDegrees, WatermarkGray));
            }

            if (stamp is not null)
            {
                ops.AddRange(PdfDraw.TextOps(stamp, StampMarginPoints, height - StampMarginPoints, StampFontSize, 0.0, StampGray));
            }

            PdfDraw.DrawOnPage(doc, page, font, [.. ops]);
        }

        return Determinism.Save(doc, [.. pdf, .. System.Text.Encoding.UTF8.GetBytes(stamp ?? "draft")]);
    }

    /// <summary>Page selection, then stamps — the whole post-fill pass for one form; null when the page mode leaves nothing.</summary>
    public static byte[]? ApplyOutputStamps(byte[] pdf, FormRelease release, OutputOptions options, DateTimeOffset printedAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        var selected = SelectPages(pdf, release, options.SignaturePages);
        return selected is null ? null : StampPages(selected, options.DraftWatermark, options.PrintDate, printedAt);
    }
}
