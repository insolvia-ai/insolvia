using Insolvia.Domain.Access;
using Insolvia.Domain.Clients;

namespace Insolvia.Api.Portal;

/// <summary>
/// Whose portal this is: the name the FIRM gave at invitation, the firm's
/// name alone, and the case's public status — deliberately no case id.
/// </summary>
/// <param name="Subject">The client's user id.</param>
/// <param name="DisplayName">The name the firm gave at invitation.</param>
/// <param name="Roles">The debtors this login answers for.</param>
/// <param name="Firm">The firm.</param>
/// <param name="Case">The bound case's chapter and stage.</param>
public sealed record PortalMeView(
    string Subject,
    string DisplayName,
    IReadOnlyList<ClientRole> Roles,
    PortalFirmView Firm,
    CasePublicStatus Case)
{
    /// <summary>The view for <paramref name="client"/>.</summary>
    /// <param name="client">The resolved client.</param>
    /// <param name="status">The bound case's chapter and stage.</param>
    /// <returns>The view.</returns>
    public static PortalMeView Of(ClientAccessor client, CasePublicStatus status)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new(client.Subject, client.Binding.DisplayName, [.. client.Roles], new(client.Firm.Name), status);
    }
}

/// <summary>A firm, as its clients see it.</summary>
/// <param name="Name">The firm's name.</param>
public sealed record PortalFirmView(string Name);
