using System.Text.Json.Serialization;
using Insolvia.Domain.Claims;
using Insolvia.Domain.Fields;
using Insolvia.Domain.LibraryCreditors;

namespace Insolvia.Api.Firms;

/// <summary>
/// A creditor in the firm's reusable library (<c>library_creditor_json</c>):
/// <c>address</c> always present (<c>{}</c> when empty), <c>notes</c> absent when none.
/// </summary>
/// <param name="Id">The library creditor's id.</param>
/// <param name="Name">The creditor's name.</param>
/// <param name="Address">The creditor's address, its unanswered parts absent.</param>
/// <param name="AdditionalNoticeParties">Others to notify about a debt to this creditor.</param>
/// <param name="Preferred">Whether the firm pins it to the top of the picker.</param>
/// <param name="CreatedAt">Added, ISO 8601 UTC to the microsecond.</param>
/// <param name="UpdatedAt">Last changed, ISO 8601 UTC to the microsecond.</param>
/// <param name="Notes">The firm's notes; absent when none.</param>
public sealed record LibraryCreditorView(
    string Id,
    string Name,
    Address Address,
    IReadOnlyList<NoticePartyView> AdditionalNoticeParties,
    bool Preferred,
    string CreatedAt,
    string UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notes)
{
    /// <summary>The view of <paramref name="creditor"/>.</summary>
    /// <param name="creditor">The library creditor.</param>
    /// <returns>The view.</returns>
    public static LibraryCreditorView Of(LibraryCreditor creditor)
    {
        ArgumentNullException.ThrowIfNull(creditor);
        return new(
            creditor.Id,
            creditor.Name,
            creditor.Address,
            [.. creditor.AdditionalNoticeParties.Select(NoticePartyView.Of)],
            creditor.Preferred,
            WireFormat.Micros(creditor.CreatedAt),
            WireFormat.Micros(creditor.UpdatedAt),
            creditor.Notes);
    }
}

/// <summary>Someone else to notify about a debt: the address always present (<c>{}</c> when empty).</summary>
/// <param name="Id">The party's id, chosen by the client that wrote it.</param>
/// <param name="Name">The party's name; absent when none.</param>
/// <param name="Address">The party's address, its unanswered parts absent.</param>
/// <param name="AccountLast4">The last four of the account number the party knows it by; absent when none.</param>
public sealed record NoticePartyView(
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    Address Address,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AccountLast4)
{
    /// <summary>The view of <paramref name="party"/>.</summary>
    /// <param name="party">The notice party.</param>
    /// <returns>The view.</returns>
    public static NoticePartyView Of(NoticeParty party)
    {
        ArgumentNullException.ThrowIfNull(party);
        return new(party.Id, party.Name, party.Address, party.AccountLast4);
    }
}

/// <summary>The firm's creditor library.</summary>
/// <param name="Creditors">Every creditor in the library.</param>
public sealed record LibraryCreditorList(IReadOnlyList<LibraryCreditorView> Creditors);
