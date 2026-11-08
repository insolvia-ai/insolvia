using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Insolvia.Core;
using Insolvia.Core.Cases;
using Insolvia.Core.Courts;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;
using Insolvia.Core.Petitions;
using Insolvia.Core.Ports;
using Insolvia.Engines;
using Insolvia.Engines.MeansTest;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Projections.Forms;

namespace Insolvia.Forms;

/// <summary>
/// The built file (Python's <c>CaseUploadFile</c>). Its bytes stay out of
/// <see cref="ToString"/> — they hold the SSN.
/// </summary>
/// <param name="FileName"><c>Debtor.txt</c>.</param>
/// <param name="SpecRelease">The spec release it was built and validated against.</param>
/// <param name="Content">The file, ASCII.</param>
public sealed record CaseUploadFile(string FileName, string SpecRelease, byte[] Content)
{
    /// <summary>The content's SHA-256, lowercase hex.</summary>
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Content));

    /// <inheritdoc />
    public override string ToString() => $"CaseUploadFile {{ FileName = {FileName}, SpecRelease = {SpecRelease} }}";
}

/// <summary>
/// THE CASE UPLOAD FILE (ADR 0024 build PR 9; from services/api's
/// <c>core/case_upload.py</c>): <c>Debtor.txt</c>, the pipe-delimited file
/// CM/ECF's Case Upload opens the case from — one <c>stat</c> record (80
/// statistics fields), one <c>debt</c> record per debtor (name, the FULL tax
/// identifier, address, the office code, the county's FIPS code), one
/// <c>alas</c> record per name the debtor used.
/// </summary>
/// <remarks>
/// <para><b>Why it lives in Forms.</b> It is a filing artifact composed from
/// what the forms print — B106Sum, B106I, B106J and B122A's figures, read off
/// the SAME projections (Engines) through <see cref="FormsHub.ResolveCaseForm"/>
/// and <see cref="CaseData"/>, both Forms' — beside the filing set and the
/// approval digest it is filed with. Engines cannot see either; the filing
/// worker (a host) composes it and nothing else does.</para>
/// <para><b>The figures come from the forms, not a second calculation</b>, so
/// the court's statistics cannot disagree with the forms the attorney signed.
/// The office code is the case division's <c>Division.OfficeCode</c>; the
/// county is the debtor's residence county looked up by name among the
/// district's divisions.</para>
/// <para><b>The SSN.</b> Debt field 8 is the full nine digits, opened through
/// <see cref="CaseDataReads.DiscloseTaxIdsAsync"/> with purpose
/// <see cref="TaxIdPurpose"/> — a <c>taxid.read</c> row per debtor BEFORE the
/// envelope opens. The file is GENERATED ON DEMAND and STREAMED: built in the
/// filing worker's memory at upload time, handed to the driver and dropped,
/// never stored. No problem, exception, log line or <c>ToString</c> carries a
/// field's VALUE.</para>
/// </remarks>
public static class CaseUploads
{
    /// <summary>The <c>taxid.read</c> row's purpose for this file — distinct from B121's.</summary>
    public const string TaxIdPurpose = "case_upload";

    /// <summary>The upload's key and handling in the filing set: first, before the petition (FLMB's guide, ADR 0024 source S19).</summary>
    public const string DocumentKey = "case_upload";

    private static readonly Dictionary<string, string> CreditorCodes = new(StringComparer.Ordinal)
    {
        ["1_49"] = "A",
        ["50_99"] = "B",
        ["100_199"] = "C",
        ["200_999"] = "D",
        ["1000_5000"] = "E",
        ["5001_10000"] = "F",
        ["10001_25000"] = "G",
        ["25001_50000"] = "H",
        ["50001_100000"] = "I",
        ["more_than_100000"] = "J",
    };

    // B101 lines 19-20's twelve brackets onto the spec's scale; J is retired.
    private static readonly Dictionary<string, string> DollarCodes = new(StringComparer.Ordinal)
    {
        ["0_50000"] = "A",
        ["50001_100000"] = "B",
        ["100001_500000"] = "C",
        ["500001_1000000"] = "D",
        ["1000001_10000000"] = "E",
        ["10000001_50000000"] = "F",
        ["50000001_100000000"] = "G",
        ["100000001_500000000"] = "H",
        ["500000001_1000000000"] = "I",
        ["1000000001_10000000000"] = "K",
        ["10000000001_50000000000"] = "L",
        ["more_than_50000000000"] = "M",
    };

    private static readonly Dictionary<string, string> FeeCodes = new(StringComparer.Ordinal) { ["full"] = "p", ["installments"] = "i", ["waiver"] = "w" };

    private static readonly Dictionary<string, string> DebtCodes = new(StringComparer.Ordinal) { ["consumer"] = "c", ["business"] = "b", ["other"] = "o" };

    private static readonly Dictionary<string, string> BusinessCodes = new(StringComparer.Ordinal)
    {
        ["health_care_business"] = "h",
        ["single_asset_real_estate"] = "r",
        ["stockbroker"] = "s",
        ["commodity_broker"] = "b",
        ["none_of_the_above"] = "o",
    };

    private static readonly Dictionary<string, string> MaritalCodes = new(StringComparer.Ordinal)
    {
        ["not_married"] = "a",
        ["married_not_filing_separated"] = "b",
        ["married_not_filing_same_household"] = "c",
        ["married_filing_jointly"] = "d",
    };

    // stat field -> the B122A-2 engine line it copies (spec.json's `form`).
    private static readonly (int Field, string Line)[] B122A2Fields =
    [
        (48, "4"), (49, "6"), (50, "7c"), (51, "7f"), (52, "8"), (53, "9c"), (56, "15"), (58, "13c"), (59, "13f"), (60, "24"),
        (61, "29"), (62, "30"), (63, "32"), (64, "37"), (65, "38"), (66, "39c"), (67, "39d"), (69, "41a"), (70, "41b"),
    ];

    /// <summary>
    /// THE ONE COMPOSITION, for the filing worker at upload time (Python's
    /// <c>case_upload_file</c>): read the case, open each debtor's sealed tax
    /// id under purpose <see cref="TaxIdPurpose"/> (a logged <c>taxid.read</c>
    /// row per debtor, naming <paramref name="principal"/>), build and
    /// validate. The result lives in the caller's memory for the length of the
    /// upload and is never stored. No route calls this.
    /// </summary>
    public static async Task<CaseUploadFile> ForFilingAsync(
        Case @case,
        string principal,
        DateOnly asOf,
        IDebtorStore debtors,
        ICaseEntityStore entities,
        ITaxIdStore taxIdStore,
        ITaxIdCipher taxIdCipher,
        IAccessLog accessLog,
        CourtRegistry? courts = null,
        CancellationToken cancellationToken = default)
    {
        var data = await CaseDataReads.ReadAsync(@case, debtors, entities, cancellationToken).ConfigureAwait(false);
        var disclosed = await CaseDataReads.DiscloseTaxIdsAsync(data, principal, TaxIdPurpose, taxIdStore, taxIdCipher, accessLog, cancellationToken).ConfigureAwait(false);
        return Build(disclosed, asOf, (courts ?? CourtRegistry.Shipped).Resolve(asOf), CaseUploadSpecs.Resolve(asOf));
    }

    /// <summary>
    /// The case's Debtor.txt (Python's <c>build_debtor_txt</c>), from the case
    /// data with its tax ids DISCLOSED, validated against <paramref name="spec"/>
    /// before it is returned. Throws <see cref="CaseUploadException"/> naming
    /// every problem — what cannot be built and what does not validate — and
    /// never a value.
    /// </summary>
    public static CaseUploadFile Build(CaseData data, DateOnly asOf, CourtsRelease registry, CaseUploadSpec spec)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(spec);
        var problems = new Problems();
        var @case = data.Case;
        var district = @case.Court is { Length: > 0 } court ? registry.District(court) : null;
        var division = district is not null && @case.Division is { Length: > 0 } code ? district.Division(code) : null;
        if (district is null || division is null)
        {
            problems.Add("debt", 10, "court", "the case has no court and division");
            throw new CaseUploadException(problems.Items);
        }

        var officeCode = division.OfficeCode.Value;
        if (string.IsNullOrEmpty(officeCode))
        {
            problems.Add("debt", 10, "office", "the division has no CM/ECF office code");
            throw new CaseUploadException(problems.Items);
        }

        var caseFile = data.ToCaseFile();
        var filers = data.Debtors
            .Where(d => d.FilingRole is FilingRole.Debtor1 or FilingRole.Debtor2)
            .OrderBy(d => d.FilingRole.ToWire(), StringComparer.Ordinal)
            .ToList();
        if (filers.Count == 0 || filers[0].FilingRole != FilingRole.Debtor1)
        {
            problems.Add("debt", 0, "debtor", "the case has no debtor 1");
            throw new CaseUploadException(problems.Items);
        }

        var debtSpec = spec.Records["debt"];
        var alasSpec = spec.Records["alas"];
        var rows = new List<List<string>> { Statistics(caseFile, asOf, problems) };
        string[] partyRoles = ["db", "jdb"];
        foreach (var (debtor, partyRole) in filers.Zip(partyRoles))
        {
            rows.Add(DebtorRecord(debtor, partyRole, officeCode, district, data.TaxIds, problems, debtSpec.TrailingDelimiter));
        }

        foreach (var (debtor, partyRole) in filers.Zip(partyRoles))
        {
            rows.AddRange(AliasRecords(debtor, partyRole, problems, alasSpec.TrailingDelimiter));
        }

        if (problems.Items.Count > 0)
        {
            throw new CaseUploadException(problems.Items);
        }

        var text = string.Concat(rows.Select(row => string.Join(spec.Delimiter, row) + spec.RecordSeparator));
        var found = CaseUploadSpecs.Validate(text, spec);
        if (found.Count > 0)
        {
            throw new CaseUploadException(found);
        }

        return new CaseUploadFile(spec.FileName, spec.ReleaseId, Encoding.ASCII.GetBytes(text));
    }

    // ── the statistics record ──

    private static List<string> Statistics(CaseFile caseFile, DateOnly asOf, Problems problems)
    {
        var stat = Enumerable.Repeat(string.Empty, 80).ToList();
        void Put(int number, string value) => stat[number - 1] = value;

        var @case = caseFile.Case;
        var petition = caseFile.Petition;
        Put(1, "stat");
        Put(5, "i"); // Insolvia files individual cases only (B101).
        if (caseFile.SoleProprietorships.Count > 0)
        {
            Put(6, Code(BusinessCodes, caseFile.SoleProprietorships[0].BusinessType?.ToWire()));
        }

        Put(8, @case.Chapter.ToString(CultureInfo.InvariantCulture));
        Put(11, "v");
        Put(17, "n"); // Chapter 11 small business only; never a case filed here.
        if (petition is null)
        {
            problems.Add("stat", 0, "petition", "the case has no petition record");
            return stat;
        }

        Put(9, Code(FeeCodes, petition.FeeHandling?.ToWire()));
        Put(10, Code(DebtCodes, petition.DebtCharacter?.ToWire()));
        if (@case.Chapter == 7)
        {
            var funds = petition.Ch7FundsAvailableForCreditors;
            Put(13, funds is null ? string.Empty : funds.Value ? "y" : "n");
        }
        else
        {
            Put(13, "y");
        }

        Put(14, Code(CreditorCodes, petition.EstimatedCreditors?.ToWire()));
        Put(15, Code(DollarCodes, petition.EstimatedAssets?.ToWire()));
        Put(16, Code(DollarCodes, petition.EstimatedLiabilities?.ToWire()));

        // B101 line 9: the prior cases the debtor declared (the last 8 years).
        Put(24, caseFile.PriorCases.Count > 0 ? "y" : "n");

        if (Projected(caseFile, "form/b106sum", asOf, problems) is { } summary)
        {
            foreach (var (number, fieldId) in new[]
            {
                (25, "line_1a_total_real_estate"),
                (26, "line_1b_total_personal_property"),
                (27, "line_2_secured_claims_total"),
                (28, "line_3a_priority_unsecured_total"),
                (29, "line_3b_nonpriority_unsecured_total"),
                (30, "line_4_combined_monthly_income"),
                (31, "line_5_monthly_expenses"),
                (33, "line_9g_total"),
            })
            {
                Put(number, Money(ParseMoney(Get(summary.Values, fieldId))));
            }
        }

        if (caseFile.Exemptions.Count > 0)
        {
            Put(35, Money(caseFile.Exemptions.Aggregate(0m, (sum, exemption) => sum + B106C.ClaimedAmount(exemption, caseFile))));
        }

        if (Projected(caseFile, "form/b106i", asOf, problems) is { } income)
        {
            foreach (var (number, fieldId, digit) in new[]
            {
                (36, "line_2_gross_wages", "1"),
                (37, "line_2_gross_wages", "2"),
                (38, "line_6_total_deductions", "1"),
                (39, "line_6_total_deductions", "2"),
            })
            {
                Put(number, Money(ParseMoney(Column(income.Values, income.Release, fieldId, digit))));
            }
        }

        if (Projected(caseFile, "form/b106j", asOf, problems) is { } expenses)
        {
            Put(40, Money(ParseMoney(Get(expenses.Values, "line_23c_net_income"))));
        }

        MeansTest(stat, caseFile, problems);
        return stat;
    }

    private static void MeansTest(List<string> stat, CaseFile caseFile, Problems problems)
    {
        void Put(int number, string value) => stat[number - 1] = value;

        var chapter = caseFile.Case.Chapter;
        var trace = MeansTestTraces.Trace(caseFile);
        if (trace.Case.Cmi.Problems.Count == 0)
        {
            Put(32, Money(Exact.Dec(trace.Case.Cmi.CombinedMonthlyTotal)));
        }

        var result = trace.Result;
        var consumer = caseFile.Petition?.DebtCharacter == DebtCharacter.Consumer;
        if (chapter != 7)
        {
            Put(21, "n"); // Chapter 7 voluntary individual consumer cases only.
            return;
        }

        if (result is null)
        {
            if (consumer)
            {
                problems.Add("stat", 21, "means_test", "the means test has not reached a result for this case");
            }
            else
            {
                Put(21, "n");
            }

            return;
        }

        var abuse = result.Outcome == "presumption_of_abuse";
        Put(21, abuse ? "y" : "n");
        var inputs = trace.Case.Inputs;
        if (inputs.DisabledVeteran == true)
        {
            Put(41, "y");
        }

        if (inputs.NonConsumerDebts == true)
        {
            Put(42, "y");
        }

        Put(43, Code(MaritalCodes, trace.MaritalFilingStatus));
        var columns = new Dictionary<string, CmiColumn>(StringComparer.Ordinal);
        foreach (var column in trace.Case.Cmi.Columns)
        {
            columns[column.Column] = column;
        }

        if (columns.TryGetValue("A", out var a))
        {
            Put(44, Money(Exact.Dec(a.MonthlyTotal)));
        }

        if (columns.TryGetValue("B", out var b))
        {
            Put(45, Money(Exact.Dec(b.MonthlyTotal)));
        }

        if (result.Comparison is { } comparison)
        {
            Put(46, Money(Exact.Dec(comparison.AnnualMedian)));
            Put(47, comparison.HouseholdSize.ToString(CultureInfo.InvariantCulture));
        }

        if (result.Outcome is not ("no_presumption" or "presumption_of_abuse"))
        {
            return; // below the median or exempt: 122A-2 is not filed.
        }

        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.Lines)
        {
            lines[line.Line] = line.Amount;
        }

        foreach (var (number, line) in B122A2Fields)
        {
            if (lines.TryGetValue(line, out var amount))
            {
                Put(number, Money(Exact.Dec(amount)));
            }
        }

        Put(55, Money(Exact.Dec(lines.TryGetValue("12", out var vehicle) ? vehicle : lines.GetValueOrDefault("14", "0"))));
        if (lines.TryGetValue("11", out var eleven))
        {
            Put(57, Math.Min((long)decimal.Truncate(Exact.Dec(eleven)), 2).ToString(CultureInfo.InvariantCulture));
        }

        if (result.DeterminedBy == "unsecured_ratio")
        {
            Put(68, "u");
            Put(71, abuse ? "y" : "n");
        }
        else
        {
            Put(68, abuse ? "y" : "n");
        }
    }

    private static (FieldValues Values, FormRelease Release)? Projected(CaseFile caseFile, string series, DateOnly asOf, Problems problems)
    {
        try
        {
            var release = FormsHub.ResolveCaseForm(caseFile.Case, series, asOf);
            return (FormProjections.Project(release, caseFile), release);
        }
        catch (Exception error) when (error is FormProjectionException or EngineLookupException or KeyNotFoundException)
        {
            problems.Add("stat", 0, "projection", $"{series["form/".Length..]} cannot be projected for this case");
            return null;
        }
    }

    private static FieldEntry? Get(FieldValues values, string fieldId) => values.TryGetValue(fieldId, out var entry) ? entry : null;

    private static FieldEntry? Column(FieldValues values, FormRelease release, string fieldId, string digit)
    {
        var entry = Get(values, fieldId);
        if (entry is RowFills rows)
        {
            return rows.TryGetValue(B106I.ColumnWidget(release, fieldId, digit), out var fill) ? fill : null;
        }

        return digit == "1" ? entry : null;
    }

    /// <summary>Python's <c>Decimal(fill.value.replace(",", ""))</c>, or null for anything but a parseable text fill.</summary>
    private static decimal? ParseMoney(FieldEntry? fill) =>
        fill is TextFill text && decimal.TryParse(text.Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Python's <c>f"{value:.2f}"</c> — the context's half-even rounding — or blank.</summary>
    private static string Money(decimal? value) => value is { } amount ? Exact.Money(amount, Rounding.HalfEven) : string.Empty;

    private static string Code(Dictionary<string, string> codes, string? key) => codes.GetValueOrDefault(key ?? string.Empty, string.Empty);

    // ── the debtor and alias records ──

    private static string CountyCode(CourtDistrict district, Address address, string role, Problems problems)
    {
        static string Norm(string name)
        {
            var cleaned = name.ToLowerInvariant().Replace("saint ", "st. ", StringComparison.Ordinal);
            if (cleaned.EndsWith(" county", StringComparison.Ordinal))
            {
                cleaned = cleaned[..^" county".Length];
            }

            return string.Join(' ', SplitWhitespace(cleaned));
        }

        var county = address.County;
        if (string.IsNullOrEmpty(county))
        {
            problems.Add("debt", 17, "county", $"{role} has no residence county");
            return string.Empty;
        }

        // Python's `state.upper() != district.state`; the registry's states are upper-case ASCII.
        if (!string.IsNullOrEmpty(address.State) && !string.Equals(address.State, district.State, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("debt", 17, "county", $"{role}'s county is outside the district");
            return string.Empty;
        }

        foreach (var division in district.Divisions)
        {
            foreach (var candidate in division.Counties)
            {
                if (Norm(candidate.Name) == Norm(county))
                {
                    return candidate.Fips;
                }
            }
        }

        problems.Add("debt", 17, "county", $"{role}'s county is not one the district serves");
        return string.Empty;
    }

    private static List<string> DebtorRecord(
        Debtor debtor,
        string partyRole,
        string officeCode,
        CourtDistrict district,
        IReadOnlyDictionary<string, string> taxIds,
        Problems problems,
        bool trailing)
    {
        var role = debtor.FilingRole.ToWire();
        var record = Enumerable.Repeat(string.Empty, 21).ToList();
        var body = debtor.Body;
        var name = body.Name;
        record[0] = "debt";
        record[1] = partyRole;
        record[2] = Ascii(name.Given, "debt", 3, problems);
        record[3] = Ascii(name.Middle, "debt", 4, problems);
        record[4] = Ascii(name.Surname, "debt", 5, problems);
        record[6] = Ascii(name.Suffix, "debt", 7, problems);
        if (!taxIds.TryGetValue(role, out var digits))
        {
            problems.Add("debt", 8, "tax_id", $"{role}'s tax id is not available");
        }
        else if (digits.Length != 9 || !digits.All(char.IsAsciiDigit))
        {
            problems.Add("debt", 8, "tax_id", $"{role}'s tax id is not nine digits");
        }
        else
        {
            record[7] = $"{digits[..3]}-{digits[3..5]}-{digits[5..]}";
        }

        record[8] = string.Join(',', body.EmployerIds);
        record[9] = officeCode;
        var address = !string.IsNullOrEmpty(body.MailingAddress.Line1) ? body.MailingAddress : body.ResidenceAddress;
        record[10] = Ascii(address.Line1, "debt", 11, problems);
        record[11] = Ascii(address.Line2, "debt", 12, problems);
        record[13] = Ascii(address.City, "debt", 14, problems);
        record[14] = Ascii(string.IsNullOrEmpty(address.State) ? null : address.State.ToUpperInvariant(), "debt", 15, problems);
        record[15] = Ascii(address.PostalCode, "debt", 16, problems);
        record[16] = CountyCode(district, body.ResidenceAddress, role, problems);
        if (trailing)
        {
            record.Add(string.Empty);
        }

        return record;
    }

    private static List<List<string>> AliasRecords(Debtor debtor, string partyRole, Problems problems, bool trailing)
    {
        var records = new List<List<string>>();
        foreach (var alias in debtor.Body.OtherNamesUsed)
        {
            var record = Enumerable.Repeat(string.Empty, 8).ToList();
            record[0] = "alas";
            record[1] = partyRole;
            record[2] = "aka";
            if (!string.IsNullOrEmpty(alias.BusinessName))
            {
                record[5] = Ascii(alias.BusinessName, "alas", 6, problems);
            }
            else
            {
                record[3] = Ascii(alias.Given, "alas", 4, problems);
                record[4] = Ascii(alias.Middle, "alas", 5, problems);
                record[5] = Ascii(alias.Surname, "alas", 6, problems);
            }

            if (trailing)
            {
                record.Add(string.Empty);
            }

            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// The value as printable ASCII (Python's <c>_ascii</c>): NFKD, combining
    /// marks dropped, whitespace collapsed, the delimiter refused. Never echoes
    /// the value in a problem.
    /// </summary>
    internal static string Ascii(string? value, string record, int number, Problems problems)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var kept = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            // unicodedata.combining(c) != 0: the marks NFKD splits off a
            // letter. .NET exposes no canonical combining class; every mark
            // with one is a nonspacing, spacing-combining or enclosing mark,
            // and a mark of class 0 left in would be refused below anyway.
            if (CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                kept.Append(c);
            }
        }

        var folded = string.Join(' ', SplitWhitespace(kept.ToString()));
        if (!CaseUploadSpecs.Printable(folded) || folded.Contains('|', StringComparison.Ordinal))
        {
            problems.Add(record, number, "charset", $"{record} field {number} holds a character the file cannot carry");
            return string.Empty;
        }

        return folded;
    }

    /// <summary>Python's <c>str.split()</c>: on runs of whitespace (Python's set, which adds U+001C-U+001F to .NET's), no empty parts.</summary>
    private static string[] SplitWhitespace(string text) =>
        text.Split(text.Where(c => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f').Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries);

    internal sealed class Problems
    {
        public List<CaseUploadProblem> Items { get; } = [];

        public void Add(string record, int number, string code, string message) => Items.Add(new CaseUploadProblem(0, record, number, code, message));
    }
}
