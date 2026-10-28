using Insolvia.Core.Fields;

namespace Insolvia.Core.Firms;

/// <summary>
/// What the firm prints at the top of a letter and on the signer block's firm
/// lines (issue #360). Every field optional.
/// </summary>
public sealed record Letterhead(string? Name = null, Address? Address = null, string? Phone = null, string? Email = null)
{
    /// <summary>The office address; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new Address();
}

/// <summary>
/// An attorney's standing signature block (issue #360). The member names are
/// a <c>filing_professional</c> body's, so "use firm default" copies it with
/// no mapping. The name is not here — it is the firm user's own.
/// </summary>
public sealed record SignatureBlock(
    string? BarNumber = null,
    string? BarState = null,
    string? FirmName = null,
    Address? Address = null,
    string? Phone = null,
    string? Email = null)
{
    /// <summary>The address on the block; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new Address();
}

/// <summary>A firm's default court and division for new cases — a validated registry reference.</summary>
public sealed record DefaultCourt(string Court, string Division);

/// <summary>
/// A value a PATCH writes, INCLUDING null to clear it. Every change record
/// reads a bare null as "leave unchanged"; an <see cref="Assign{T}"/> with a
/// null <see cref="Value"/> is "take it away".
/// </summary>
public sealed record Assign<T>(T Value);

/// <summary>
/// A law firm — the tenant. <see cref="CreatedBy"/>/<see cref="CreatedByEmail"/>
/// are provisioning provenance (#212): the staff principal who created it,
/// absent on firms seeded before the admin portal. Facts about creation, never
/// authorization inputs. The defaults (issue #360) PREFILL; they never decide.
/// </summary>
public sealed record Firm(
    string Id,
    string Name,
    FirmStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CreatedBy = null,
    string? CreatedByEmail = null,
    DefaultCourt? DefaultCourt = null,
    int? DefaultChapter = null,
    Letterhead? Letterhead = null);

/// <summary>
/// One person at one firm, keyed by <c>(FirmId, Subject)</c> — the Cognito
/// <c>sub</c>, so no surrogate id. Four independent axes decide what they see:
/// <see cref="Role"/> (defaults only), <see cref="IsAdmin"/>,
/// <see cref="AccessAllCases"/> and <see cref="Permissions"/> (read only
/// through <see cref="Firms.Permissions.PermissionFor"/>).
/// </summary>
/// <remarks>
/// The name is two fields and NO stored display string; <see cref="FullName"/>
/// composes it. <c>""</c> on either half is a real value: "never recorded".
/// </remarks>
public sealed record FirmUser(
    string FirmId,
    string Subject,
    string Email,
    string FirstName,
    string LastName,
    FirmRole Role,
    bool IsAdmin,
    bool AccessAllCases,
    ValueDictionary<Feature, PermissionLevel> Permissions,
    FirmUserStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    SignatureBlock? SignatureBlock = null)
{
    /// <summary>
    /// The display string, composed from the two halves — THE ONLY PLACE
    /// THAT DECIDES HOW A NAME READS. An empty half drops the separator.
    /// </summary>
    public string FullName => string.Join(" ", new[] { FirstName, LastName }.Where(part => part.Length > 0));
}

/// <summary>A validated firm creation request.</summary>
public sealed record FirmDraft(string Name);

/// <summary>
/// A validated request to add someone to a firm. No subject: it comes back
/// from Cognito when the pool user is created, never from the caller.
/// </summary>
public sealed record FirmUserDraft(
    string Email,
    string FirstName,
    string LastName,
    FirmRole Role,
    bool IsAdmin,
    bool AccessAllCases,
    ValueDictionary<Feature, PermissionLevel> Permissions);

/// <summary>
/// A validated PATCH of the firm's own record (#217). Null means "leave
/// unchanged". There is no status here and never will be — suspending is the
/// admin service's <see cref="FirmLifecycle.SetFirmStatus"/>.
/// </summary>
public sealed record FirmChanges(
    string? Name = null,
    Assign<DefaultCourt?>? DefaultCourt = null,
    Assign<int?>? DefaultChapter = null,
    Assign<Letterhead?>? Letterhead = null);

/// <summary>
/// A validated firm-user PATCH. Null means "leave unchanged" — changing a
/// role alone must not reset the permission map.
/// </summary>
public sealed record FirmUserChanges(
    string? FirstName = null,
    string? LastName = null,
    FirmRole? Role = null,
    bool? IsAdmin = null,
    bool? AccessAllCases = null,
    ValueDictionary<Feature, PermissionLevel>? Permissions = null,
    FirmUserStatus? Status = null,
    Assign<SignatureBlock?>? SignatureBlock = null);
