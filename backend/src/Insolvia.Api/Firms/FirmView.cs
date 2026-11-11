using Insolvia.Domain.Firms;

namespace Insolvia.Api.Firms;

/// <summary>
/// The firm as its own members see it (<c>firm_summary_json</c>): no
/// provisioning provenance — who at Insolvia created it is not the firm's to
/// read — and the defaults as explicit nulls until set.
/// </summary>
/// <param name="Id">The firm's id.</param>
/// <param name="Name">The firm's name.</param>
/// <param name="Status">Whether the firm is active or suspended.</param>
/// <param name="CreatedAt">Provisioned, ISO 8601 UTC to the millisecond.</param>
/// <param name="UpdatedAt">Last changed, ISO 8601 UTC to the millisecond.</param>
/// <param name="Defaults">What a new case is prefilled with, and the letterhead.</param>
public sealed record FirmSummaryView(
    string Id,
    string Name,
    FirmStatus Status,
    string CreatedAt,
    string UpdatedAt,
    [property: Flatten] FirmDefaults Defaults)
{
    /// <summary>The view of <paramref name="firm"/>.</summary>
    /// <param name="firm">The firm.</param>
    /// <returns>The view.</returns>
    public static FirmSummaryView Of(Firm firm)
    {
        ArgumentNullException.ThrowIfNull(firm);
        return new(firm.Id, firm.Name, firm.Status, WireFormat.Millis(firm.CreatedAt), WireFormat.Millis(firm.UpdatedAt), FirmDefaults.Of(firm));
    }
}

/// <summary>
/// A firm's defaults (<c>firm_defaults_json</c>). Each is an EXPLICIT null
/// when unset, so "not set" never reads as an older API that lacked the field.
/// The defaults prefill a new case; they never decide anything.
/// </summary>
/// <param name="DefaultCourt">The default court's id; null when unset.</param>
/// <param name="DefaultDivision">The default division's id within that court; null when unset.</param>
/// <param name="DefaultChapter">The default chapter; null when unset.</param>
/// <param name="Letterhead">What the firm prints at the top of a letter, its unanswered fields absent; null when unset.</param>
public sealed record FirmDefaults(string? DefaultCourt, string? DefaultDivision, int? DefaultChapter, Letterhead? Letterhead)
{
    /// <summary>The defaults of <paramref name="firm"/>.</summary>
    /// <param name="firm">The firm.</param>
    /// <returns>The defaults.</returns>
    public static FirmDefaults Of(Firm firm)
    {
        ArgumentNullException.ThrowIfNull(firm);
        return new(firm.DefaultCourt?.Court, firm.DefaultCourt?.Division, firm.DefaultChapter, firm.Letterhead);
    }
}

/// <summary>
/// A change to the firm's own record (<c>PATCH /v1/firm</c>); every member
/// optional, a present null clears it. Parsed by <c>FirmParser.ParseFirmUpdate</c>,
/// which owns validation; this record is its shape for the OpenAPI document.
/// A status is not accepted: a firm cannot suspend itself.
/// </summary>
/// <param name="Name">The firm's name.</param>
/// <param name="DefaultCourt">The default court's id; sent with <paramref name="DefaultDivision"/> as a pair.</param>
/// <param name="DefaultDivision">The default division's id within that court.</param>
/// <param name="DefaultChapter">The default chapter.</param>
/// <param name="Letterhead">The letterhead, whole.</param>
public sealed record FirmUpdateRequest(string? Name, string? DefaultCourt, string? DefaultDivision, int? DefaultChapter, Letterhead? Letterhead);

/// <summary>
/// A colleague as anyone in the firm may see them (<c>firm_user_summary_json</c>):
/// a name, a job title and the signature block — no email, no permissions, no status.
/// </summary>
/// <param name="Subject">The colleague's sign-in subject, their id.</param>
/// <param name="FirstName">Their first name; <c>""</c> when never recorded.</param>
/// <param name="LastName">Their last name; <c>""</c> when never recorded.</param>
/// <param name="DisplayName">The two halves composed, the one way a name reads.</param>
/// <param name="Role">Their job title.</param>
/// <param name="SignatureBlock">Their standing signature block, its unanswered fields absent; null when none.</param>
public sealed record UserSummaryView(string Subject, string FirstName, string LastName, string DisplayName, FirmRole Role, SignatureBlock? SignatureBlock)
{
    /// <summary>The view of <paramref name="user"/>.</summary>
    /// <param name="user">The colleague.</param>
    /// <returns>The view.</returns>
    public static UserSummaryView Of(FirmUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new(user.Subject, user.FirstName, user.LastName, user.FullName, user.Role, user.SignatureBlock);
    }
}

/// <summary>
/// A firm member's whole record (<c>firm_user_json</c>), for the firm's
/// administrators. <c>permissions</c> is the STORED map, not the effective one.
/// </summary>
/// <param name="Subject">The member's sign-in subject, their id.</param>
/// <param name="Email">Their sign-in email address.</param>
/// <param name="FirstName">Their first name; <c>""</c> when never recorded.</param>
/// <param name="LastName">Their last name; <c>""</c> when never recorded.</param>
/// <param name="DisplayName">The two halves composed, the one way a name reads.</param>
/// <param name="Role">Their job title; it sets the default permissions only.</param>
/// <param name="IsAdmin">Whether they administer the firm.</param>
/// <param name="AccessAllCases">Whether they see every case, not only those they are on.</param>
/// <param name="Permissions">The stored level for each feature that has one.</param>
/// <param name="Status">Whether the membership is active or disabled.</param>
/// <param name="CreatedAt">Added, ISO 8601 UTC to the millisecond.</param>
/// <param name="UpdatedAt">Last changed, ISO 8601 UTC to the millisecond.</param>
/// <param name="SignatureBlock">Their standing signature block, its unanswered fields absent; null when none.</param>
public sealed record UserView(
    string Subject,
    string Email,
    string FirstName,
    string LastName,
    string DisplayName,
    FirmRole Role,
    bool IsAdmin,
    bool AccessAllCases,
    IReadOnlyDictionary<Feature, PermissionLevel> Permissions,
    FirmUserStatus Status,
    string CreatedAt,
    string UpdatedAt,
    SignatureBlock? SignatureBlock)
{
    /// <summary>The view of <paramref name="user"/>.</summary>
    /// <param name="user">The member.</param>
    /// <returns>The view.</returns>
    public static UserView Of(FirmUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new(
            user.Subject,
            user.Email,
            user.FirstName,
            user.LastName,
            user.FullName,
            user.Role,
            user.IsAdmin,
            user.AccessAllCases,
            user.Permissions,
            user.Status,
            WireFormat.Millis(user.CreatedAt),
            WireFormat.Millis(user.UpdatedAt),
            user.SignatureBlock);
    }
}

/// <summary>The firm's members, disabled ones included, by name only.</summary>
/// <param name="People">Every member of the firm.</param>
public sealed record FirmDirectory(IReadOnlyList<UserSummaryView> People);

/// <summary>The firm's members, whole records.</summary>
/// <param name="Users">Every member of the firm.</param>
public sealed record FirmUserList(IReadOnlyList<UserView> Users);

/// <summary>
/// Someone to add to the firm (<c>POST /v1/firm/users</c>). Parsed by
/// <c>FirmParser.ParseFirmUserCreation</c>, which owns validation; this
/// record is its shape for the OpenAPI document.
/// </summary>
/// <param name="Email">Their email address; the sign-in account is created for it.</param>
/// <param name="FirstName">Their first name; sent with <paramref name="LastName"/>.</param>
/// <param name="LastName">Their last name.</param>
/// <param name="DisplayName">Legacy: one string split on its last space, read only when neither half is sent.</param>
/// <param name="Role">Their job title.</param>
/// <param name="IsAdmin">Whether they administer the firm; false when absent.</param>
/// <param name="AccessAllCases">Whether they see every case; false when absent.</param>
/// <param name="Permissions">Levels that MERGE over the role's defaults.</param>
public sealed record FirmUserCreateRequest(
    string Email,
    string? FirstName,
    string? LastName,
    string? DisplayName,
    FirmRole Role,
    bool? IsAdmin,
    bool? AccessAllCases,
    IReadOnlyDictionary<Feature, PermissionLevel>? Permissions);

/// <summary>
/// A change to a firm member (<c>PATCH /v1/firm/users/{subject}</c>); every
/// member optional. The email is not changeable. Parsed by
/// <c>FirmParser.ParseFirmUserUpdate</c>, which owns validation; this record
/// is its shape for the OpenAPI document.
/// </summary>
/// <param name="FirstName">Their first name.</param>
/// <param name="LastName">Their last name.</param>
/// <param name="DisplayName">Legacy: one string split on its last space, read only when neither half is sent.</param>
/// <param name="Role">Their job title.</param>
/// <param name="IsAdmin">Whether they administer the firm.</param>
/// <param name="AccessAllCases">Whether they see every case.</param>
/// <param name="Permissions">The whole permission map; it REPLACES the stored one.</param>
/// <param name="Status">Active or disabled.</param>
/// <param name="SignatureBlock">Their signature block, whole; null removes it.</param>
public sealed record FirmUserUpdateRequest(
    string? FirstName,
    string? LastName,
    string? DisplayName,
    FirmRole? Role,
    bool? IsAdmin,
    bool? AccessAllCases,
    IReadOnlyDictionary<Feature, PermissionLevel>? Permissions,
    FirmUserStatus? Status,
    SignatureBlock? SignatureBlock);
