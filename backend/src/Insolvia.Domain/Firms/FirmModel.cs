using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.Courts;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Firms;

/// <summary>
/// What the firm prints at the top of a letter and on the signer block's firm
/// lines (issue #360). Every field optional.
/// </summary>
public sealed record Letterhead(
    [property: Text(FirmRules.MaxFirmName)] string? Name = null,
    Address? Address = null,
    [property: Text(32)] string? Phone = null,
    [property: Text(FirmRules.MaxEmail)] string? Email = null)
{
    /// <summary>The office address; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new Address();
}

/// <summary>
/// An attorney's standing signature block (issue #360). The member names and
/// limits are a <c>filingProfessional</c> body's, so "use firm default" copies
/// it with no mapping. The name is not here — it is the firm user's own.
/// </summary>
public sealed record SignatureBlock(
    [property: Text(32)] string? BarNumber = null,
    [property: Text(2)] string? BarState = null,
    [property: Text] string? FirmName = null,
    Address? Address = null,
    [property: Text(32)] string? Phone = null,
    [property: Text] string? Email = null)
{
    /// <summary>The address on the block; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new Address();
}

/// <summary>A firm's default court and division for new cases — a validated registry reference.</summary>
public sealed record DefaultCourt(string Court, string Division);

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
/// Adding someone to the firm (<c>POST /v1/firm/users</c>). No subject: it
/// comes back from Cognito when the pool user is created, never from the
/// caller. Supplied <see cref="Permissions"/> MERGE over the role's defaults,
/// so granting one feature does not silently revoke the rest.
/// </summary>
/// <param name="Email">Their email address; the sign-in account is created for it, lower-cased.</param>
/// <param name="FirstName">Their first name.</param>
/// <param name="LastName">Their last name.</param>
/// <param name="Role">Their job title; it sets the default permissions.</param>
/// <param name="IsAdmin">Whether they administer the firm.</param>
/// <param name="AccessAllCases">Whether they see every case, not only those they are on.</param>
/// <param name="Permissions">Levels that merge over the role's defaults.</param>
public sealed record NewFirmUser(
    [property: Required(ErrorMessage = "An email address is required."), MaxLength(FirmRules.MaxEmail, ErrorMessage = "Please keep this under 320 characters."), RegularExpression(FirmRules.EmailPattern, ErrorMessage = FirmRules.EmailMessage)] string? Email,
    [property: Required(ErrorMessage = FirmRules.NameRequired), Text(FirmRules.MaxNamePart), MinLength(1, ErrorMessage = FirmRules.NameRequired), RegularExpression(FirmRules.NotBlank, ErrorMessage = FirmRules.NameRequired)] string? FirstName,
    [property: Required(ErrorMessage = FirmRules.NameRequired), Text(FirmRules.MaxNamePart), MinLength(1, ErrorMessage = FirmRules.NameRequired), RegularExpression(FirmRules.NotBlank, ErrorMessage = FirmRules.NameRequired)] string? LastName,
    [property: Required(ErrorMessage = "Choose a role.")] FirmRole? Role,
    bool IsAdmin = false,
    bool AccessAllCases = false,
    IReadOnlyDictionary<Feature, PermissionLevel>? Permissions = null)
{
    /// <summary>The draft a firm user is created from: the address lower-cased, the permissions merged over the role's.</summary>
    /// <returns>The draft.</returns>
    public FirmUserDraft ToDraft() => new(
        TextRules.Lower(Email!),
        FirstName!,
        LastName!,
        Role!.Value,
        IsAdmin,
        AccessAllCases,
        Firms.Permissions.DefaultFor(Role.Value).Concat(Permissions ?? new Dictionary<Feature, PermissionLevel>()).ToValueDictionary());
}

/// <summary>
/// A PATCH of the firm's own record (<c>PATCH /v1/firm</c>, #217): only the
/// keys sent change. The defaults and the letterhead clear with an explicit
/// null, which <see cref="Names"/> tells apart from a key not sent. There is no
/// status here and never will be — suspending is the admin service's
/// <see cref="FirmLifecycle.SetFirmStatus"/>.
/// </summary>
public sealed record FirmChanges : IValidatableObject
{
    private readonly ImmutableHashSet<string> _named = [];

    /// <summary>A new name.</summary>
    [Text(FirmRules.MaxFirmName), MinLength(1, ErrorMessage = FirmRules.NameRequired), RegularExpression(FirmRules.NotBlank, ErrorMessage = FirmRules.NameRequired)]
    public string? Name { get; init; }

    /// <summary>The default court's CM/ECF code, sent with <see cref="DefaultDivision"/>; both null clear the pair.</summary>
    public string? DefaultCourt { get; init { field = value; _named = _named.Add(nameof(DefaultCourt)); } }

    /// <summary>The default division's code within that court.</summary>
    public string? DefaultDivision { get; init { field = value; _named = _named.Add(nameof(DefaultDivision)); } }

    /// <summary>The default chapter; null clears it.</summary>
    [AllowedValues(null, 7, 11, 12, 13, ErrorMessage = "Chapter must be one of 7, 11, 12, 13.")]
    public int? DefaultChapter { get; init { field = value; _named = _named.Add(nameof(DefaultChapter)); } }

    /// <summary>The letterhead, whole; null clears it.</summary>
    public Letterhead? Letterhead { get; init { field = value; _named = _named.Add(nameof(Letterhead)); } }

    /// <summary>Whether nothing would change.</summary>
    [JsonIgnore]
    public bool IsEmpty => Name is null && _named.IsEmpty;

    /// <summary>Whether the request named <paramref name="member"/> — a clearable member set to null clears it.</summary>
    /// <param name="member">The C# member's name.</param>
    /// <returns>Whether it was sent.</returns>
    public bool Names(string member) => _named.Contains(member);

    /// <summary>The court pair travels together, and the registry knows it.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DefaultCourt is null && DefaultDivision is null)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(DefaultCourt))
        {
            yield return new ValidationResult("Choose a court from the registry, or clear both.", [nameof(DefaultCourt)]);
        }
        else if (string.IsNullOrWhiteSpace(DefaultDivision))
        {
            yield return new ValidationResult("Choose the court's division, or clear both.", [nameof(DefaultDivision)]);
        }
        else if (CourtRegistry.Shipped.Division(DefaultCourt, DefaultDivision) is null)
        {
            yield return CourtRegistry.Shipped.District(DefaultCourt) is null
                ? new ValidationResult("That court is not in the registry.", [nameof(DefaultCourt)])
                : new ValidationResult("That division is not one of this court's.", [nameof(DefaultDivision)]);
        }
    }
}

/// <summary>
/// A firm-user PATCH (<c>PATCH /v1/firm/users/{subject}</c>): only the keys
/// sent change — changing a role alone must not reset the permission map, and
/// <see cref="Permissions"/> REPLACES the stored map (a merging PATCH could
/// only ever grant). The email is not changeable: it is a pool operation. The
/// signature block clears with an explicit null.
/// </summary>
public sealed record FirmUserChanges
{
    private readonly bool _signatureBlockSent;

    /// <summary>Their first name.</summary>
    [Text(FirmRules.MaxNamePart), MinLength(1, ErrorMessage = FirmRules.NameRequired), RegularExpression(FirmRules.NotBlank, ErrorMessage = FirmRules.NameRequired)]
    public string? FirstName { get; init; }

    /// <summary>Their last name.</summary>
    [Text(FirmRules.MaxNamePart), MinLength(1, ErrorMessage = FirmRules.NameRequired), RegularExpression(FirmRules.NotBlank, ErrorMessage = FirmRules.NameRequired)]
    public string? LastName { get; init; }

    /// <summary>Their job title.</summary>
    public FirmRole? Role { get; init; }

    /// <summary>Whether they administer the firm.</summary>
    public bool? IsAdmin { get; init; }

    /// <summary>Whether they see every case.</summary>
    public bool? AccessAllCases { get; init; }

    /// <summary>The whole permission map; it replaces the stored one.</summary>
    public IReadOnlyDictionary<Feature, PermissionLevel>? Permissions { get; init; }

    /// <summary>Active or disabled.</summary>
    public FirmUserStatus? Status { get; init; }

    /// <summary>Their signature block, whole; null removes it.</summary>
    public SignatureBlock? SignatureBlock { get; init { field = value; _signatureBlockSent = true; } }

    /// <summary>Whether the request sent a signature block, a null one included.</summary>
    [JsonIgnore]
    public bool SignatureBlockSent => _signatureBlockSent;

    /// <summary>Whether nothing would change.</summary>
    [JsonIgnore]
    public bool IsEmpty => FirstName is null && LastName is null && Role is null && IsAdmin is null && AccessAllCases is null && Permissions is null && Status is null && !_signatureBlockSent;
}

/// <summary>
/// The caller's change to themselves (<c>PATCH /v1/me</c>): their name
/// (either half alone) and their signature block. Everything else on the row
/// is somebody else's statement about the member and is not read.
/// </summary>
public sealed record SelfChanges
{
    private readonly bool _signatureBlockSent;

    /// <summary>The caller's first name.</summary>
    [Text(FirmRules.MaxNamePart), MinLength(1, ErrorMessage = FirmRules.NameRequired), RegularExpression(FirmRules.NotBlank, ErrorMessage = FirmRules.NameRequired)]
    public string? FirstName { get; init; }

    /// <summary>The caller's last name.</summary>
    [Text(FirmRules.MaxNamePart), MinLength(1, ErrorMessage = FirmRules.NameRequired), RegularExpression(FirmRules.NotBlank, ErrorMessage = FirmRules.NameRequired)]
    public string? LastName { get; init; }

    /// <summary>The caller's signature block, whole; null removes it.</summary>
    public SignatureBlock? SignatureBlock { get; init { field = value; _signatureBlockSent = true; } }

    /// <summary>The same change as an administrator's PATCH would make.</summary>
    /// <returns>The changes.</returns>
    public FirmUserChanges ToUserChanges() => _signatureBlockSent
        ? new FirmUserChanges { FirstName = FirstName, LastName = LastName, SignatureBlock = SignatureBlock }
        : new FirmUserChanges { FirstName = FirstName, LastName = LastName };

    /// <summary>Whether nothing would change.</summary>
    [JsonIgnore]
    public bool IsEmpty => FirstName is null && LastName is null && !_signatureBlockSent;
}

/// <summary>The limits and messages the firm requests share.</summary>
public static class FirmRules
{
    /// <summary>The cap on a firm name.</summary>
    public const int MaxFirmName = 200;

    /// <summary>The cap on one HALF of a name — two at 200 would allow a 401-character display string.</summary>
    public const int MaxNamePart = 100;

    /// <summary>The cap on an email address.</summary>
    public const int MaxEmail = 320;

    /// <summary>What a blank name is told.</summary>
    public const string NameRequired = "A name is required.";

    /// <summary>A string with something besides whitespace in it.</summary>
    public const string NotBlank = @".*\S.*";

    /// <summary>Deliberately loose: Cognito, which sends to it, is what establishes an address.</summary>
    public const string EmailPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";

    /// <summary>What a malformed address is told.</summary>
    public const string EmailMessage = "That doesn't look like a valid email address.";
}
