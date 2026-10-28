using System.Text.Json.Serialization;

namespace Insolvia.Core.Courts;

// The court registry's records (from courts.py; issue #360 / 14.8): what a
// district record needs for a filing — identity, divisions with their CM/ECF
// office code and counties (venue), PDF and creditor-matrix rules, how the
// signature and the SSN statement reach the docket, Case Upload's standing —
// and a dated source for every one of those facts. EVERY FACT IS WRAPPED in
// a Fact: a value, verified/unverified, the source and the date it was read.
// "We do not know" is a legal answer (an unverified null value).

/// <summary>The provenance half of a <see cref="Fact{T}"/>, without its value.</summary>
public interface IFact
{
    /// <summary>Whether it was read from a dated source.</summary>
    FactStatus Status { get; }

    /// <summary>The id of the record's source it was read from.</summary>
    string? Source { get; }

    /// <summary>The day it was read.</summary>
    DateOnly? VerifiedAt { get; }

    /// <summary>Free text about the fact; empty when none.</summary>
    string Note { get; }

    /// <summary>Whether <see cref="Status"/> is verified.</summary>
    bool Verified { get; }
}

/// <summary>One court fact with its provenance. A verified fact may still carry a null value — a verified absence.</summary>
/// <typeparam name="T">The value's type.</typeparam>
public sealed record Fact<T>(T? Value, FactStatus Status, string? Source, DateOnly? VerifiedAt, string Note = "") : IFact
{
    /// <inheritdoc />
    public bool Verified => Status == FactStatus.Verified;
}

/// <summary>One page a record's facts were read from, and when.</summary>
public sealed record Source(string Id, string Title, string Url, DateOnly Read);

/// <summary>A county a division serves, by its FIPS-5 code.</summary>
public sealed record County(string Name, string Fips);

/// <summary>A courthouse's postal address.</summary>
public sealed record Courthouse(string Name, string Line1, string? Line2, string City, string State, string PostalCode);

/// <summary>One division of a district: where a case is opened, and which counties send their debtors there.</summary>
public sealed record Division(
    string Code,
    string Name,
    Fact<string> OfficeCode,
    Fact<Courthouse> Courthouse,
    ValueCollection<County> Counties,
    string CountiesSource);

/// <summary>The court's CM/ECF endpoints.</summary>
public sealed record CmEcf(Fact<string> LiveUrl, Fact<string> TrainingUrl, Fact<string> HelpDesk, Fact<string> Release)
{
    /// <summary>Every fact of the section (Python's <c>vars(section)</c>).</summary>
    public IEnumerable<IFact> Facts() => [LiveUrl, TrainingUrl, HelpDesk, Release];
}

/// <summary>The court's PDF rules.</summary>
public sealed record PdfRules(Fact<long?> MaxBytes, Fact<bool?> TextSearchableRequired, Fact<bool?> FlattenRequired, Fact<bool?> PdfARequired)
{
    /// <summary>Every fact of the section.</summary>
    public IEnumerable<IFact> Facts() => [MaxBytes, TextSearchableRequired, FlattenRequired, PdfARequired];
}

/// <summary>The creditor-matrix knobs the matrix renderer reads.</summary>
public sealed record MatrixRules(
    Fact<long?> MaxLineChars,
    Fact<long?> MaxCreditorLines,
    Fact<long?> BlankLinesBetween,
    Fact<long?> PadToLines,
    Fact<long?> NameLineChars,
    Fact<bool?> CommaAfterCity,
    Fact<bool?> CaseNumberHeaderWhenSeparate,
    Fact<bool?> CertificationRequired)
{
    /// <summary>Every fact of the section.</summary>
    public IEnumerable<IFact> Facts() =>
        [MaxLineChars, MaxCreditorLines, BlankLinesBetween, PadToLines, NameLineChars, CommaAfterCity, CaseNumberHeaderWhenSeparate, CertificationRequired];
}

/// <summary>How the debtor's wet-ink signature reaches an electronic filing.</summary>
public sealed record SignatureInstrument(string Kind, string Title, bool? WetInkRequired, string? Retention, bool? OwnDocketEvent, string? FormUrl);

/// <summary>A local form the court asks for.</summary>
public sealed record LocalForm(string Id, string Title, string? Url, string WhenRequired, string Source);

/// <summary>When the filing fee is due.</summary>
public sealed record FeeRule(string Deadline, string Note);

/// <summary>What opening a case at this court involves.</summary>
public sealed record Opening(
    Fact<SignatureInstrument> SignatureInstrument,
    Fact<SsnStatementHandling?> SsnStatement,
    ValueCollection<LocalForm> LocalForms,
    Fact<FeeRule> FeeRule,
    Fact<ValueCollection<string>> DocketOrder,
    Fact<ValueDictionary<string, string>> FileNames);

/// <summary>The plan form a district's Chapter 13 cases file.</summary>
public sealed record Chapter13PlanForm(Chapter13PlanFormKind Form, string? Title, string? Url);

/// <summary>How the court's Case Upload facility stands for our packages.</summary>
public sealed record CaseUpload(CaseUploadStatus Status, string? Kind, DateOnly? VerifiedAt, string Note);

/// <summary>What a filer's CM/ECF registration needs.</summary>
public sealed record Registration(Fact<bool?> TrainingRequired, string Notes);

/// <summary>One bankruptcy court, as the registry describes it.</summary>
public sealed record CourtDistrict(
    string Code,
    string? CourtId,
    string Name,
    string State,
    int Circuit,
    string Website,
    ValueCollection<Division> Divisions,
    CmEcf Cmecf,
    PdfRules Pdf,
    MatrixRules Matrix,
    Opening Opening,
    CaseUpload CaseUpload,
    Registration Registration,
    ValueCollection<Source> Sources,
    string Notes,
    Fact<Chapter13PlanForm> Chapter13Plan)
{
    /// <summary>The division with <paramref name="code"/>, or null.</summary>
    public Division? Division(string code) => Divisions.FirstOrDefault(division => division.Code == code);

    /// <summary>The division a county's debtors file in — venue's answer.</summary>
    public Division? DivisionForCounty(string fips) =>
        Divisions.FirstOrDefault(division => division.Counties.Any(county => county.Fips == fips));
}

/// <summary>One immutable release of the series.</summary>
public sealed record CourtsRelease(
    string SeriesId,
    DateOnly EffectiveDate,
    int Sequence,
    string SourceUrl,
    DateOnly? SourcePublished,
    string? SourceSha256,
    string Notes,
    ValueCollection<CourtDistrict> Districts)
{
    /// <summary><c>courts/us-bankruptcy@2026-10-06</c>, with <c>+n</c> for a later sequence on the same day.</summary>
    public string ReleaseId
    {
        get
        {
            var date = EffectiveDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var id = $"{SeriesId}@{date}";
            return Sequence == 1 ? id : $"{id}+{Sequence}";
        }
    }

    /// <summary>The district with <paramref name="code"/>, or null.</summary>
    public CourtDistrict? District(string code) => Districts.FirstOrDefault(district => district.Code == code);
}

/// <summary>Whether a court fact was read from a dated source (Python's <c>FACT_STATUSES</c>).</summary>
public enum FactStatus
{
    /// <summary>Read from a cited source on a date.</summary>
    [JsonStringEnumMemberName("verified")]
    Verified,

    /// <summary>Not (yet) read from a source; the value may be null.</summary>
    [JsonStringEnumMemberName("unverified")]
    Unverified,
}

/// <summary>How the court's Case Upload facility stands for our packages (Python's <c>CASE_UPLOAD_STATUSES</c>).</summary>
public enum CaseUploadStatus
{
    /// <summary>Unproven until a training-database session says otherwise.</summary>
    [JsonStringEnumMemberName("unverified")]
    Unverified,

    /// <summary>The legacy pipe-delimited text file.</summary>
    [JsonStringEnumMemberName("legacy_txt")]
    LegacyTxt,

    /// <summary>The XML case opening.</summary>
    [JsonStringEnumMemberName("xml")]
    Xml,
}

/// <summary>How Official Form B121 reaches the court (Python's <c>SSN_STATEMENT_HANDLINGS</c>).</summary>
public enum SsnStatementHandling
{
    /// <summary>Filed as its own docket event.</summary>
    [JsonStringEnumMemberName("own_event")]
    OwnEvent,

    /// <summary>Not filed; the full SSN goes on the opening screen.</summary>
    [JsonStringEnumMemberName("not_filed")]
    NotFiled,

    /// <summary>Inside the petition package.</summary>
    [JsonStringEnumMemberName("with_petition")]
    WithPetition,
}

/// <summary>Which Chapter 13 plan form the district takes (Python's <c>CHAPTER_13_PLAN_FORMS</c>).</summary>
public enum Chapter13PlanFormKind
{
    /// <summary>Official Form 113, the national plan.</summary>
    [JsonStringEnumMemberName("official")]
    Official,

    /// <summary>The district's own Rule 3015.1 form.</summary>
    [JsonStringEnumMemberName("local")]
    Local,
}
