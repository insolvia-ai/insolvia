using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Insolvia.Domain.Fields;
using Insolvia.Domain.Provenance;

namespace Insolvia.Domain.Claims;

// The piece of claims.py the FIRM side needs before the case half is ported
// (18.7): a library creditor's additional notice parties are "the same shape
// `claim.notice_parties` uses" (issue 13.9), parsed by the same function.
// 18.7 ports the rest of claims.py beside this.

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

/// <summary>The notice-party list parser (Python's <c>claims.parse_notice_parties</c>).</summary>
public static class NoticeParties
{
    /// <summary>What a last-four box that is not digits is told.</summary>
    public const string Last4Message = "Must be up to four digits.";

    /// <summary>
    /// A <c>notice_parties</c> list. Errors are keyed under
    /// <c>notice_parties[…]</c> whatever the caller's field is called — the
    /// library creditor parser rewrites the prefix.
    /// </summary>
    public static ValueCollection<NoticeParty> Parse(JsonNode? value, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return [];
        }

        if (value is not JsonArray list)
        {
            errors["notice_parties"] = "Must be a list.";
            return [];
        }

        var parties = new List<NoticeParty>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < list.Count; index++)
        {
            var path = $"notice_parties[{index}]";
            var entry = FieldParsers.Mapping(list[index], path, errors);
            var givenId = Json.AsString(entry["id"]);
            if (givenId is null || !ProvenanceRules.AddressableId().IsMatch(givenId))
            {
                errors[$"{path}.id"] = "Required, and must be letters, digits, hyphen or underscore — generate one per row so provenance can name it.";
                continue;
            }

            if (!seen.Add(givenId))
            {
                errors[$"{path}.id"] = "Duplicate id.";
                continue;
            }

            parties.Add(new NoticeParty(
                Id: givenId,
                Name: FieldParsers.Text(entry["name"], $"{path}.name", errors),
                Address: FieldParsers.ParseAddress(entry["address"], $"{path}.address", errors),
                AccountLast4: Last4(entry["account_last4"], $"{path}.account_last4", errors)));
        }

        return parties.ToValueCollection();
    }

    /// <summary>
    /// The last-four box — never a full account number, which would be PII
    /// this record has no protection for.
    /// </summary>
    public static string? Last4(JsonNode? value, string path, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var last4 = FieldParsers.Text(value, path, errors, limit: 4);
        if (last4 is not null && !TextRules.IsDigits(last4))
        {
            errors[path] = Last4Message;
            return null;
        }

        return last4;
    }
}
