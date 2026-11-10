using System.Text.Json.Serialization;
using Insolvia.Domain;
using Insolvia.Domain.Access;
using Insolvia.Domain.Auth;
using Insolvia.Domain.Firms;

namespace Insolvia.Api.Firms;

/// <summary>
/// The signed-in caller (<c>GET</c> and <c>PATCH /v1/me</c>): who the token
/// says they are, and their firm membership when they have one.
/// </summary>
/// <param name="Subject">The token's subject.</param>
/// <param name="Username">The token's username; null when it carries none.</param>
/// <param name="ClientId">The app client the token was issued to.</param>
/// <param name="Scopes">The token's scopes.</param>
/// <param name="ExpiresAt">When the token expires, epoch seconds; null when the claim is not a whole number.</param>
/// <param name="Firm">The caller's firm membership; absent for a caller in no firm.</param>
public sealed record MeView(
    string Subject,
    string? Username,
    string ClientId,
    IReadOnlyList<string> Scopes,
    long? ExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MeFirm? Firm)
{
    /// <summary>The view of <paramref name="principal"/> and their membership.</summary>
    /// <param name="principal">The verified caller.</param>
    /// <param name="accessor">Their firm membership, or null.</param>
    /// <returns>The view.</returns>
    public static MeView Of(Principal principal, Accessor? accessor)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new(principal.Subject, principal.Username, principal.ClientId, principal.Scopes, principal.ExpiresAt, accessor is null ? null : MeFirm.Of(accessor));
    }
}

/// <summary>
/// The caller's own membership: the firm, their name and axes, and the
/// firm's defaults. <c>permissions</c> is the EFFECTIVE map — an administrator
/// reads add_edit everywhere — where the firm's user list carries the stored one.
/// </summary>
/// <param name="Id">The firm's id.</param>
/// <param name="Name">The firm's name.</param>
/// <param name="Role">The caller's job title.</param>
/// <param name="FirstName">The caller's first name; <c>""</c> when never recorded.</param>
/// <param name="LastName">The caller's last name; <c>""</c> when never recorded.</param>
/// <param name="DisplayName">The two halves composed, the one way a name reads.</param>
/// <param name="IsAdmin">Whether the caller administers the firm.</param>
/// <param name="AccessAllCases">Whether the caller sees every case.</param>
/// <param name="Permissions">The caller's effective level for every feature.</param>
/// <param name="SignatureBlock">The caller's signature block, its unanswered fields absent; null when none.</param>
/// <param name="Defaults">The firm's defaults and letterhead.</param>
public sealed record MeFirm(
    string Id,
    string Name,
    FirmRole Role,
    string FirstName,
    string LastName,
    string DisplayName,
    bool IsAdmin,
    bool AccessAllCases,
    IReadOnlyDictionary<Feature, PermissionLevel> Permissions,
    SignatureBlock? SignatureBlock,
    [property: Flatten] FirmDefaults Defaults)
{
    /// <summary>The view of <paramref name="accessor"/>.</summary>
    /// <param name="accessor">The caller's membership.</param>
    /// <returns>The view.</returns>
    public static MeFirm Of(Accessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        var user = accessor.User;
        return new(
            accessor.Firm.Id,
            accessor.Firm.Name,
            user.Role,
            user.FirstName,
            user.LastName,
            user.FullName,
            user.IsAdmin,
            user.AccessAllCases,
            Wire.Members<Feature>().ToDictionary(feature => feature, feature => Insolvia.Domain.Firms.Permissions.PermissionFor(user, feature)),
            user.SignatureBlock,
            FirmDefaults.Of(accessor.Firm));
    }
}

/// <summary>
/// The caller's change to themselves (<c>PATCH /v1/me</c>): their name
/// (either half alone) and their signature block; nothing else is read.
/// Parsed by <c>FirmParser.ParseSelfUpdate</c>, which owns validation; this
/// record is its shape for the OpenAPI document.
/// </summary>
/// <param name="FirstName">The caller's first name.</param>
/// <param name="LastName">The caller's last name.</param>
/// <param name="DisplayName">Legacy: one string split on its last space, read only when neither half is sent.</param>
/// <param name="SignatureBlock">The caller's signature block, whole; null removes it.</param>
public sealed record MeUpdateRequest(string? FirstName, string? LastName, string? DisplayName, SignatureBlock? SignatureBlock);
