using System.Text.Json.Serialization;
using Insolvia.Domain.Firms;

namespace Insolvia.Admin.Firms;

/// <summary>
/// A firm as staff administer it: its defaults and its provisioning provenance,
/// each written as <c>null</c> when unset rather than left out —
/// <c>createdBy: null</c> tells the portal "nobody recorded" (a seeded firm).
/// The tenant API never answers with the provenance.
/// </summary>
/// <param name="Id">The firm's id.</param>
/// <param name="Name">The firm's name.</param>
/// <param name="Status">Active, or suspended (none of its users can reach the API).</param>
/// <param name="CreatedAt">When it was provisioned, ISO 8601 UTC to the millisecond.</param>
/// <param name="UpdatedAt">When it last changed, ISO 8601 UTC to the millisecond.</param>
/// <param name="CreatedBy">The subject of the staff member who provisioned it; null when nobody recorded.</param>
/// <param name="CreatedByEmail">Their email; null when nobody recorded.</param>
/// <param name="DefaultCourt">The court a new case starts in (its CM/ECF code); null when unset.</param>
/// <param name="DefaultDivision">That court's division; null when unset.</param>
/// <param name="DefaultChapter">The chapter a new case starts as; null when unset.</param>
/// <param name="Letterhead">The firm's letterhead, its unanswered parts absent; null when it has none.</param>
public sealed record FirmView(
    string Id,
    string Name,
    FirmStatus Status,
    string CreatedAt,
    string UpdatedAt,
    string? CreatedBy,
    string? CreatedByEmail,
    string? DefaultCourt,
    string? DefaultDivision,
    int? DefaultChapter,
    Letterhead? Letterhead)
{
    /// <summary>The view of <paramref name="firm"/>.</summary>
    /// <param name="firm">The firm.</param>
    /// <returns>The view.</returns>
    public static FirmView Of(Firm firm)
    {
        ArgumentNullException.ThrowIfNull(firm);
        return new(
            firm.Id,
            firm.Name,
            firm.Status,
            WireFormat.Millis(firm.CreatedAt),
            WireFormat.Millis(firm.UpdatedAt),
            firm.CreatedBy,
            firm.CreatedByEmail,
            firm.DefaultCourt?.Court,
            firm.DefaultCourt?.Division,
            firm.DefaultChapter,
            firm.Letterhead);
    }
}

/// <summary>A firm with how many users it has — the list and the single read.</summary>
/// <param name="Firm">The firm.</param>
/// <param name="UserCount">How many users it has, of every status.</param>
public sealed record FirmSummaryView([property: Flatten] FirmView Firm, int UserCount)
{
    /// <summary>The view of <paramref name="firm"/>.</summary>
    /// <param name="firm">The firm.</param>
    /// <param name="userCount">How many users it has.</param>
    /// <returns>The view.</returns>
    public static FirmSummaryView Of(Firm firm, int userCount) => new(FirmView.Of(firm), userCount);
}

/// <summary>
/// A firm's user, whole: this caller administers firms. <c>permissions</c> is
/// the STORED map, not the effective one (an admin's is every feature).
/// </summary>
/// <param name="Subject">The user's sign-in subject.</param>
/// <param name="Email">Their email.</param>
/// <param name="FirstName">Their first name.</param>
/// <param name="LastName">Their last name.</param>
/// <param name="DisplayName">First and last name together.</param>
/// <param name="Role">Their role at the firm.</param>
/// <param name="IsAdmin">Whether they administer the firm.</param>
/// <param name="AccessAllCases">Whether they see every case, not only their own.</param>
/// <param name="Permissions">The stored level for each feature.</param>
/// <param name="Status">Invited, active or deactivated.</param>
/// <param name="CreatedAt">When they were added, ISO 8601 UTC to the millisecond.</param>
/// <param name="UpdatedAt">When they last changed, ISO 8601 UTC to the millisecond.</param>
/// <param name="SignatureBlock">What signs their filings, its unanswered parts absent; null when they have none.</param>
public sealed record FirmUserView(
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
    /// <param name="user">The user.</param>
    /// <returns>The view.</returns>
    public static FirmUserView Of(FirmUser user)
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

/// <summary>Every firm.</summary>
/// <param name="Firms">The firms, each with its user count.</param>
public sealed record FirmList(IReadOnlyList<FirmSummaryView> Firms);

/// <summary>A firm's users.</summary>
/// <param name="Users">The users.</param>
public sealed record FirmUserList(IReadOnlyList<FirmUserView> Users);

/// <summary>A newly provisioned firm and its first administrator.</summary>
/// <param name="Firm">The firm.</param>
/// <param name="Admin">Its first user, an administrator with every permission.</param>
public sealed record ProvisionedFirm(FirmView Firm, FirmUserView Admin);

/// <summary>A firm to provision (<c>POST /v1/firms</c>).</summary>
/// <param name="Name">The firm's name.</param>
/// <param name="Admin">Its first administrator.</param>
public sealed record ProvisionRequest(string Name, ProvisionAdmin Admin);

/// <summary>A new firm's first administrator, who is sent a sign-in invitation.</summary>
/// <param name="Email">Where the invitation goes; also their sign-in.</param>
/// <param name="FirstName">Their first name.</param>
/// <param name="LastName">Their last name.</param>
/// <param name="DisplayName">A whole name, split into first and last when those are absent.</param>
/// <param name="Role">Their role at the firm; <c>attorney</c> when absent.</param>
public sealed record ProvisionAdmin(
    string Email,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirstName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FirmRole? Role);

/// <summary>A firm's new status (<c>PATCH /v1/firms/{firmId}</c>).</summary>
/// <param name="Status">Active, or suspended.</param>
public sealed record FirmStatusRequest(FirmStatus Status);
