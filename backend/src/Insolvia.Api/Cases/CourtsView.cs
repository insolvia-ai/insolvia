using Insolvia.Domain.Courts;

namespace Insolvia.Api.Cases;

/// <summary>The court registry release in force: every district and its divisions.</summary>
/// <param name="ReleaseId">The release, e.g. <c>courts/us-bankruptcy@2026-10-06</c>.</param>
/// <param name="EffectiveDate">When it took effect, YYYY-MM-DD.</param>
/// <param name="Districts">Every bankruptcy court.</param>
public sealed record CourtsView(string ReleaseId, string EffectiveDate, IReadOnlyList<CourtDistrictView> Districts)
{
    /// <summary>The view of <paramref name="release"/>.</summary>
    /// <param name="release">The release.</param>
    /// <returns>The view.</returns>
    public static CourtsView Of(CourtsRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return new(release.ReleaseId, WireFormat.Date(release.EffectiveDate), [.. release.Districts.Select(CourtDistrictView.Of)]);
    }
}

/// <summary>One bankruptcy court.</summary>
/// <param name="Code">The court's CM/ECF code — what a case's <c>court</c> names.</param>
/// <param name="CourtId">The court's PACER id; null when unknown.</param>
/// <param name="Name">The printed district name.</param>
/// <param name="State">The state, as its two-letter code.</param>
/// <param name="Circuit">The federal circuit.</param>
/// <param name="Website">The court's website.</param>
/// <param name="Divisions">Its divisions.</param>
/// <param name="CaseUpload">How its Case Upload facility stands for Insolvia's packages.</param>
public sealed record CourtDistrictView(
    string Code,
    string? CourtId,
    string Name,
    string State,
    int Circuit,
    string Website,
    IReadOnlyList<DivisionView> Divisions,
    CaseUploadView CaseUpload)
{
    /// <summary>The view of <paramref name="district"/>.</summary>
    /// <param name="district">The court.</param>
    /// <returns>The view.</returns>
    public static CourtDistrictView Of(CourtDistrict district)
    {
        ArgumentNullException.ThrowIfNull(district);
        return new(
            district.Code,
            district.CourtId,
            district.Name,
            district.State,
            district.Circuit,
            district.Website,
            [.. district.Divisions.Select(DivisionView.Of)],
            new CaseUploadView(district.CaseUpload.Status, district.CaseUpload.VerifiedAt is { } verified ? WireFormat.Date(verified) : null));
    }
}

/// <summary>How a court's Case Upload facility stands.</summary>
/// <param name="Status">Its standing.</param>
/// <param name="VerifiedAt">When that was last checked, YYYY-MM-DD; null when never.</param>
public sealed record CaseUploadView(CaseUploadStatus Status, string? VerifiedAt);

/// <summary>One division of a court.</summary>
/// <param name="Code">The division's code — what a case's <c>division</c> names.</param>
/// <param name="Name">Its name.</param>
/// <param name="OfficeCode">Its CM/ECF office code; null when unknown.</param>
/// <param name="OfficeCodeVerified">Whether the office code was read from a dated source.</param>
/// <param name="Courthouse">Its courthouse's address; null when unknown.</param>
/// <param name="Counties">The counties whose debtors file here.</param>
public sealed record DivisionView(
    string Code,
    string Name,
    string? OfficeCode,
    bool OfficeCodeVerified,
    CourthouseView? Courthouse,
    IReadOnlyList<County> Counties)
{
    /// <summary>The view of <paramref name="division"/>.</summary>
    /// <param name="division">The division.</param>
    /// <returns>The view.</returns>
    public static DivisionView Of(Division division)
    {
        ArgumentNullException.ThrowIfNull(division);
        var courthouse = division.Courthouse.Value;
        return new(
            division.Code,
            division.Name,
            division.OfficeCode.Value,
            division.OfficeCode.Verified,
            courthouse is null ? null : new CourthouseView(courthouse.Name, courthouse.Line1, courthouse.Line2, courthouse.City, courthouse.State, courthouse.PostalCode),
            division.Counties);
    }
}

/// <summary>A courthouse's postal address.</summary>
/// <param name="Name">The building's name.</param>
/// <param name="Line1">The street address.</param>
/// <param name="Line2">The second address line; null when none.</param>
/// <param name="City">The city.</param>
/// <param name="State">The state's two-letter code.</param>
/// <param name="PostalCode">The ZIP code.</param>
[SnakeCase]
public sealed record CourthouseView(string Name, string Line1, string? Line2, string City, string State, string PostalCode);
