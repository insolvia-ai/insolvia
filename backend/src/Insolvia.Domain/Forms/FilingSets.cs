using Insolvia.Core;
using Insolvia.Core.Cases;
using Insolvia.Core.Courts;
using Insolvia.Engines;
using Insolvia.Engines.CreditorMatrix;

namespace Insolvia.Forms;

/// <summary>One PDF check of a file: size, text layer or page size, and its outcome (<c>pass</c>, <c>fail</c>, <c>warn</c>, <c>unmeasured</c>).</summary>
public sealed record FilingCheck(string Check, string Outcome, string Message);

/// <summary>One document of the filing set, in docket order (<c>handling</c>: file, own_event, restricted, not_filed; <c>source</c>: packet, outside).</summary>
public sealed record FilingDocument(string Key, string Title, string FileName, string Source, string Handling, IReadOnlyList<FilingCheck> Checks, string Note = "", PacketPart? Part = null);

/// <summary>One line of the hand-off checklist (<c>status</c>: ready, missing, action, confirm); <c>link</c> is an app segment, or null when the step is outside Insolvia.</summary>
public sealed record ChecklistItem(string Id, string Status, string Title, string Detail, string? Link = null);

/// <summary>The filing set and its checklist (<c>basis</c> values: court, court_unverified, default).</summary>
public sealed record FilingSet(
    string? CourtCode,
    string? CourtName,
    string? DivisionName,
    string RegistryRelease,
    string FilingMethod,
    Packet? Packet,
    string OrderBasis,
    string NamesBasis,
    long MaxBytes,
    string MaxBytesBasis,
    IReadOnlyList<FilingDocument> Documents,
    IReadOnlyList<ChecklistItem> Checklist);

/// <summary>
/// The filing set and its checklist (from <c>core/filing_set.py</c>, ADR
/// 0024 build PR 3): the documents in the order the court dockets them,
/// under the names it wants, each judged against the court's PDF rules by the
/// packet's measured parts, and a checklist of what is ready, missing and
/// why. Per-district facts come from the court registry; an unknown one
/// falls back to a documented default and says so. Files nothing, prints
/// nothing, and never sees B121's number.
/// </summary>
public static class FilingSets
{
    /// <summary>The SSN statement's series.</summary>
    public const string B121Series = "form/b121";

    /// <summary>The creditor matrix's document key.</summary>
    public const string MatrixKey = "creditor_matrix";

    /// <summary>The court's signature instrument's document key.</summary>
    public const string SignatureKey = "signature_instrument";

    /// <summary>The matrix certification's document key.</summary>
    public const string CertificationKey = "matrix_certification";

    private const long Megabyte = 1024 * 1024;

    private static readonly Dictionary<string, string> ProblemLinks = new(StringComparer.Ordinal)
    {
        ["case"] = string.Empty,
        ["debtors"] = "intake",
        ["petitions"] = "petition",
        ["employments"] = "income",
        ["income_summaries"] = "income",
        ["pay_period_records"] = "income",
        ["other_income_records"] = "income",
        ["means_test_inputs"] = "means-test",
        ["plans"] = "plan",
        ["creditors"] = "creditor-matrix",
    };

    /// <summary>The strictest size cap any district of the release records — the default for an unknown court.</summary>
    public static long DefaultMaxBytes(CourtsRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var caps = release.Districts.Select(d => d.Pdf.MaxBytes.Value).OfType<long>().ToList();
        return caps.Count == 0
            ? throw new EngineLookupException($"{release.ReleaseId} records no PDF size cap at all")
            : caps.Min();
    }

    /// <summary>The newest packet a court could be handed, from a newest-first listing.</summary>
    public static Packet? LatestFilingSetPacket(IEnumerable<Packet> packets) =>
        packets.FirstOrDefault(packet => Packets.IsFilingSet(packet.Options));

    private static string Basis<T>(Fact<T> fact) => fact.Value is null ? "default" : fact.Verified ? "court" : "court_unverified";

    private static string Megabytes(long bytes) => FormFill.FixedPoint(bytes / (double)Megabyte, 1) + " MB";

    private static List<FilingCheck> Checks(PacketPart? part, Packet? packet, long maxBytes, bool? textRequired)
    {
        if (part is null)
        {
            var why = packet is null
                ? "No filing-set packet has been assembled yet — assemble it so the file can be checked."
                : "This file is not in the latest packet, or that packet was assembled before files were measured — assemble again.";
            return [new("size", "unmeasured", why)];
        }

        var size = part.ByteSize <= maxBytes
            ? new FilingCheck("size", "pass", $"{Megabytes(part.ByteSize)}, under the {Megabytes(maxBytes)} limit.")
            : new FilingCheck(
                "size",
                "fail",
                $"{Megabytes(part.ByteSize)} is over the {Megabytes(maxBytes)} limit — split it into {(part.ByteSize + maxBytes - 1) / maxBytes} files under the limit before uploading.");
        if (part.PageCount is null)
        {
            return [size];
        }

        if (part.Unreadable)
        {
            return [size, new("text_layer", "fail", "The PDF could not be read."), new("page_size", "fail", "The PDF could not be read.")];
        }

        FilingCheck text;
        if (part.PagesWithoutText is > 0)
        {
            var rule = textRequired == true
                ? "the court requires text-searchable PDFs"
                : "this court's rule is not recorded; two launch courts require text-searchable PDFs";
            text = new FilingCheck("text_layer", textRequired == true ? "fail" : "warn", $"{part.PagesWithoutText} of {part.PageCount} pages have no text layer — {rule}.");
        }
        else
        {
            text = new FilingCheck("text_layer", "pass", "Every page has a text layer.");
        }

        var page = part.NonLetterPages is > 0
            ? new FilingCheck("page_size", "fail", $"{part.NonLetterPages} of {part.PageCount} pages are not US letter (8.5 x 11 in).")
            : new FilingCheck("page_size", "pass", "Every page is US letter.");
        return [size, text, page];
    }

    private static List<FilingCheck> OutsideChecks(long maxBytes) =>
        [new("size", "unmeasured", $"Prepared outside Insolvia — before uploading, check it is a text-searchable, letter-size PDF under {Megabytes(maxBytes)}.")];

    private static (string Title, string Form) FormTitle(CaseData data, string seriesId, DateOnly asOf)
    {
        try
        {
            var release = FormsHub.ResolveCaseForm(data.Case, seriesId, asOf);
            var label = release.OfficialNumber.Length > 0 ? release.OfficialNumber : release.Title;
            return (release.OfficialNumber.Length > 0 ? $"{label} — {release.Title}" : label, release.Form);
        }
        catch (EngineLookupException)
        {
            var shortKey = seriesId.StartsWith("form/", StringComparison.Ordinal) ? seriesId["form/".Length..] : seriesId;
            return (shortKey.ToUpperInvariant(), shortKey);
        }
    }

    private static List<FilingDocument> Ordered(List<FilingDocument> documents, ValueCollection<string>? order)
    {
        if (order is null)
        {
            return documents;
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            rank[order[i]] = i;
        }

        // Python's sorted() is stable; OrderBy is too.
        return [.. documents.OrderBy(d => rank.TryGetValue(d.Key, out var r) ? r : rank.Count)];
    }

    /// <summary>
    /// The filing set and checklist for a case against one registry release;
    /// <paramref name="packets"/> is the case's listing, newest first.
    /// </summary>
    public static FilingSet Build(CaseData data, IReadOnlyList<Packet> packets, CourtsRelease release, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(release);
        var @case = data.Case;
        var district = @case.Court is { } court ? release.District(court) : null;
        var division = district is not null && @case.Division is { Length: > 0 } divisionCode ? district.Division(divisionCode) : null;
        var packet = LatestFilingSetPacket(packets);
        var measured = new Dictionary<string, PacketPart>(StringComparer.Ordinal);
        foreach (var part in packet?.Parts ?? [])
        {
            measured[part.Name] = part;
        }

        var fallbackCap = DefaultMaxBytes(release);
        long maxBytes;
        string maxBytesBasis;
        if (district?.Pdf.MaxBytes.Value is { } cap)
        {
            maxBytes = cap;
            maxBytesBasis = Basis(district.Pdf.MaxBytes);
        }
        else
        {
            (maxBytes, maxBytesBasis) = (fallbackCap, "default");
        }

        var textRequired = district?.Pdf.TextSearchableRequired.Value;
        var ssnHandling = district?.Opening.SsnStatement.Value;
        IReadOnlyDictionary<string, string> fileNames = district?.Opening.FileNames.Value is { } names ? names : new Dictionary<string, string>(StringComparer.Ordinal);

        var documents = new List<FilingDocument>();
        var series = PacketAssembly.PacketFormSeriesFor(data);
        for (var index = 0; index < series.Count; index++)
        {
            var seriesId = series[index];
            var (title, form) = FormTitle(data, seriesId, asOf);
            var defaultName = PacketAssembly.PartFileName(index + 1, form);
            var handling = "file";
            var note = string.Empty;
            if (seriesId == B121Series)
            {
                if (ssnHandling == SsnStatementHandling.NotFiled)
                {
                    handling = "not_filed";
                    note = "This court does not take B121: the debtor's full Social Security number is entered on the court's case-opening screen instead. Do not upload this file.";
                }
                else
                {
                    handling = "restricted";
                    note = "Carries the full Social Security number. Submit it only under the court's Statement of Social Security Number event, which keeps it off the public docket — never inside the petition PDF.";
                }
            }

            var part = measured.GetValueOrDefault(defaultName);
            documents.Add(new FilingDocument(
                seriesId,
                title,
                fileNames.TryGetValue(seriesId, out var courtName) ? courtName : defaultName,
                "packet",
                handling,
                Checks(part, packet, maxBytes, textRequired),
                note,
                part));
        }

        var matrixPart = measured.GetValueOrDefault(CreditorMatrices.MatrixFileName);
        documents.Add(new FilingDocument(
            MatrixKey,
            "Creditor matrix (list of creditors, text file)",
            fileNames.TryGetValue(MatrixKey, out var matrixName) ? matrixName : CreditorMatrices.MatrixFileName,
            "packet",
            "file",
            Checks(matrixPart, packet, maxBytes, null),
            Part: matrixPart));
        if (district is not null)
        {
            var instrument = district.Opening.SignatureInstrument.Value;
            if (instrument is { OwnDocketEvent: true })
            {
                documents.Add(new FilingDocument(
                    SignatureKey,
                    instrument.Title,
                    fileNames.TryGetValue(SignatureKey, out var signatureName) ? signatureName : "signature-instrument.pdf",
                    "outside",
                    "own_event",
                    OutsideChecks(maxBytes),
                    "Signed by the debtor in wet ink; scan it and file it as its own docket event."));
            }

            if (instrument is { Kind: "matrix_certification" } || district.Matrix.CertificationRequired.Value == true)
            {
                documents.Add(new FilingDocument(
                    CertificationKey,
                    "Certification of the creditor mailing matrix",
                    fileNames.TryGetValue(CertificationKey, out var certificationName) ? certificationName : "matrix-certification.pdf",
                    "outside",
                    "own_event",
                    OutsideChecks(maxBytes),
                    "The court's signed certification that the matrix lists every creditor — prepared on the court's own form."));
            }
        }

        var orderFact = district?.Opening.DocketOrder;
        var ordered = Ordered(documents, orderFact?.Value);
        return new FilingSet(
            district?.Code,
            district?.Name,
            division?.Name,
            release.ReleaseId,
            "hand_off",
            packet,
            orderFact is not null ? Basis(orderFact) : "default",
            district is not null ? Basis(district.Opening.FileNames) : "default",
            maxBytes,
            maxBytesBasis,
            ordered,
            Checklist(data, district, division, packet, ordered, maxBytesBasis));
    }

    // ── the checklist ─────────────────────────────────────────────────────────

    private static List<ChecklistItem> ProblemItems(IReadOnlyList<PacketProblem> problems)
    {
        var bySource = new OrderedDictionary<string, List<PacketProblem>>(StringComparer.Ordinal);
        foreach (var problem in problems)
        {
            if (!bySource.TryGetValue(problem.Source, out var group))
            {
                bySource[problem.Source] = group = [];
            }

            group.Add(problem);
        }

        return [.. bySource.Select(pair => new ChecklistItem(
            $"case_data:{pair.Key}",
            "missing",
            $"Case data: {pair.Key.Replace('_', ' ')}",
            pair.Value[0].Message + (pair.Value.Count > 1 ? $" (and {pair.Value.Count - 1} more)" : string.Empty),
            ProblemLinks.TryGetValue(pair.Key, out var link) ? link : "forms"))];
    }

    private static ChecklistItem FiledItem(CaseData data)
    {
        var @case = data.Case;
        string[] facts =
        [
            @case.CaseNumber is { Length: > 0 } number ? $"Case number {number}." : "No case number is recorded yet.",
            @case.FiledAt is { } filed ? $"Filed {Engines.Exact.Iso(filed)}." : "No filing date is recorded yet.",
        ];
        return new ChecklistItem(
            "filed",
            "ready",
            "This case is filed",
            string.Join(' ', facts)
            + " No further filing is possible from this filing set: the documents above are the record of what was prepared for the court. To change a filed schedule, mark the changed items amended and assemble an amendment.");
    }

    private static string ConfirmOr<T>(Fact<T> fact, string status) => fact.Verified ? status : "confirm";

    private static List<ChecklistItem> Checklist(CaseData data, CourtDistrict? district, Division? division, Packet? packet, IReadOnlyList<FilingDocument> documents, string maxBytesBasis)
    {
        var @case = data.Case;
        var items = new List<ChecklistItem>();
        if (CaseRules.IsFiled(@case.Status))
        {
            return [FiledItem(data)];
        }

        items.Add(district is null || division is null
            ? new ChecklistItem(
                "court",
                "missing",
                "Court and division",
                "The case does not name a court and division from the court registry, so nothing below can follow that court's rules — the common defaults are shown instead.",
                string.Empty)
            : new ChecklistItem("court", "ready", "Court and division", $"{district.Name}, {division.Name}."));

        var problems = PacketAssembly.CompletenessProblems(data);
        if (problems.Count > 0)
        {
            items.AddRange(ProblemItems(problems));
        }
        else
        {
            items.Add(new ChecklistItem("case_data", "ready", "Case data", "The completeness check finds nothing missing."));
        }

        items.Add(packet is null
            ? new ChecklistItem("packet", "missing", "Filing packet", "No filing-set packet has been assembled (a draft, partial, signature-page or amendment print does not count).", "packet")
            : new ChecklistItem("packet", "ready", "Filing packet", $"Assembled {packet.CreatedAt[..Math.Min(10, packet.CreatedAt.Length)]}. Assemble again after any change to the case.", "packet"));

        var inPacket = documents.Where(d => d.Source == "packet").ToList();
        var failing = inPacket.Where(d => d.Checks.Any(c => c.Outcome == "fail")).ToList();
        var unmeasured = inPacket.Where(d => d.Checks.Any(c => c.Outcome == "unmeasured")).ToList();
        var warned = inPacket.Where(d => d.Checks.Any(c => c.Outcome == "warn")).ToList();
        if (failing.Count > 0 || unmeasured.Count > 0)
        {
            var names = string.Join(", ", (failing.Count > 0 ? failing : unmeasured).Select(d => d.FileName));
            items.Add(new ChecklistItem(
                "pdf_checks",
                "missing",
                "File checks",
                failing.Count > 0 ? $"Failing the court's file rules: {names}." : $"Not checked yet: {names}. Assemble the packet.",
                "packet"));
        }
        else
        {
            items.Add(new ChecklistItem(
                "pdf_checks",
                warned.Count > 0 ? "confirm" : "ready",
                "File checks",
                warned.Count > 0
                    ? "Pages without a text layer: " + string.Join(", ", warned.Select(d => d.FileName)) + "."
                    : "Every file is within the size limit, text-searchable and US letter."));
        }

        if (maxBytesBasis != "court")
        {
            var note = district is not null ? district.Pdf.MaxBytes.Note : "No court is set on the case.";
            items.Add(new ChecklistItem(
                "size_limit",
                "confirm",
                "Size limit",
                ("This court's file-size limit is not verified; the filing set checks against "
                + (maxBytesBasis == "court_unverified" ? "the figure on record" : "the strictest limit any launch court records")
                + $". {note}").Trim()));
        }

        if (district is null)
        {
            return items;
        }

        var opening = district.Opening;
        foreach (var (factId, title, verified, hasValue, factNote) in new[]
        {
            ("docket_order", "Docket order", opening.DocketOrder.Verified, opening.DocketOrder.Value is not null, opening.DocketOrder.Note),
            ("file_names", "File names", opening.FileNames.Verified, opening.FileNames.Value is not null, opening.FileNames.Note),
        })
        {
            if (!verified)
            {
                items.Add(new ChecklistItem(
                    factId,
                    "confirm",
                    title,
                    (hasValue ? "Recorded for this court but not verified." : "Not recorded for this court; the packet's own is used.")
                    + (factNote.Length > 0 ? $" {factNote}" : string.Empty)));
            }
        }

        var ssn = opening.SsnStatement;
        var ssnDetail = ssn.Value == SsnStatementHandling.NotFiled
            ? "This court does not take B121: the debtor's full Social Security number is entered on the court's case-opening screen. Insolvia does not display it; read it from the debtor's documents."
            : "Submit B121 only under the court's Statement of Social Security Number event, which keeps it off the public docket.";
        items.Add(new ChecklistItem(
            "ssn_statement",
            ConfirmOr(ssn, "action"),
            "Statement About Your Social Security Numbers (B121)",
            ssnDetail + (ssn.Verified ? string.Empty : $" {ssn.Note}".TrimEnd())));

        var instrumentFact = opening.SignatureInstrument;
        var instrument = instrumentFact.Value;
        if (instrument is null || instrument.Kind == "other")
        {
            items.Add(new ChecklistItem(
                "signature",
                "confirm",
                "The debtor's signature",
                "How this court takes the debtor's signature on an electronic filing is not recorded. " + instrumentFact.Note));
        }
        else
        {
            var parts = new List<string> { instrument.Title + "." };
            if (instrument.WetInkRequired == true)
            {
                parts.Add("The debtor signs in wet ink.");
            }

            if (instrument.Retention is { Length: > 0 } retention)
            {
                parts.Add($"Keep the originals {retention}.");
            }

            items.Add(new ChecklistItem(
                "signature",
                ConfirmOr(instrumentFact, "action"),
                "The debtor's signature",
                string.Join(' ', parts) + (instrumentFact.Verified ? string.Empty : $" {instrumentFact.Note}")));
        }

        foreach (var form in opening.LocalForms)
        {
            items.Add(new ChecklistItem(
                $"local_form:{form.Id}",
                "confirm",
                $"Local form {form.Id}: {form.Title}",
                $"When required: {form.WhenRequired}" + (form.Url is { Length: > 0 } url ? $" Form: {url}" : string.Empty)));
        }

        var fee = opening.FeeRule;
        items.Add(new ChecklistItem(
            "fee",
            ConfirmOr(fee, "action"),
            "Filing fee",
            (fee.Value is not null ? fee.Value.Deadline : "This court's fee procedure is not recorded — check it before filing.")
            + " The fee is paid in the court's own session; Insolvia does not take payment details."));

        var training = district.Registration.TrainingRequired;
        var notes = district.Registration.Notes;
        items.Add(new ChecklistItem(
            "registration",
            ConfirmOr(training, "action"),
            "E-filing registration",
            $"The filing attorney must be registered to e-file in the {district.Name}"
            + (training.Value == true ? " (the court requires training first)" : string.Empty)
            + "."
            + (notes.Length > 0 ? $" {notes}" : string.Empty)));

        var live = district.Cmecf.LiveUrl.Value;
        items.Add(new ChecklistItem(
            "filing_method",
            "action",
            "Open the case on the court's screens",
            "Automated filing is not available for this court yet. Sign in to the court's CM/ECF"
            + (live is { Length: > 0 } ? $" ({live})" : string.Empty)
            + ", open the case, and upload the documents above in this order. Case Upload is not verified for this court, so enter the opening screens from the case record."));
        return items;
    }
}
