using System.ComponentModel.DataAnnotations;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Claims;

// A library creditor's additional notice parties are "the same shape
// `claim.notice_parties` uses" (issue 13.9): this record, checked by the same attributes.

/// <summary>
/// 106D Part 2 / 106E/F Part 3: someone else to be notified about a debt.
/// Carries its own client-chosen <see cref="Id"/> for provenance paths.
/// </summary>
public sealed record NoticeParty(
    string Id,
    [property: Text] string? Name = null,
    Address? Address = null,
    [property: Text(4), RegularExpression("^[0-9]*$", ErrorMessage = NoticeParties.Last4Message)] string? AccountLast4 = null) : IRow
{
    /// <summary>The party's address; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new Address();
}

/// <summary>The notice-party messages.</summary>
public static class NoticeParties
{
    /// <summary>What a last-four box that is not digits is told.</summary>
    public const string Last4Message = "Must be up to four digits.";
}
