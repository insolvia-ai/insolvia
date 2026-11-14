using Insolvia.Domain.Clients;

namespace Insolvia.Api.Portal;

/// <summary>
/// What a FIRM USER sees of a client invited to their case's portal — enough
/// to manage the invitation. Never shown to the client.
/// </summary>
/// <param name="Subject">The client's user id.</param>
/// <param name="Email">The address invited, lower-cased.</param>
/// <param name="DisplayName">The name the firm gave at invitation.</param>
/// <param name="Roles">The debtors this login answers for.</param>
/// <param name="Status"><c>invited</c> until first use, then <c>active</c>; <c>revoked</c> once withdrawn.</param>
/// <param name="InvitedBy">The firm user who invited them.</param>
/// <param name="CreatedAt">When invited, ISO 8601 UTC with microseconds.</param>
/// <param name="UpdatedAt">When last written, ISO 8601 UTC with microseconds.</param>
public sealed record ClientBindingView(
    string Subject,
    string Email,
    string DisplayName,
    IReadOnlyList<ClientRole> Roles,
    BindingStatus Status,
    string InvitedBy,
    string CreatedAt,
    string UpdatedAt)
{
    /// <summary>The view of <paramref name="binding"/>.</summary>
    /// <param name="binding">The binding.</param>
    /// <returns>The view.</returns>
    public static ClientBindingView Of(ClientBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new(
            binding.Subject,
            binding.Email,
            binding.DisplayName,
            [.. binding.Roles],
            binding.Status,
            binding.InvitedBy,
            WireFormat.Micros(binding.CreatedAt),
            WireFormat.Micros(binding.UpdatedAt));
    }
}

/// <summary>Everyone ever invited to a case's portal, revoked included, oldest first.</summary>
/// <param name="Clients">The invitations.</param>
public sealed record ClientBindingList(IReadOnlyList<ClientBindingView> Clients);
