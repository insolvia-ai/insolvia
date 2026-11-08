using System.Text.Json.Nodes;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Codebtors;

/// <summary>
/// A 106H codebtor row (from <c>codebtors.py</c>). The form asks WHICH debt
/// as a line number; the model stores the fact — the claim and contract ids —
/// and the forms engine renders the line. The id lists are attributed whole
/// by provenance and unchecked against their collections (progressive intake).
/// </summary>
public sealed record CodebtorBody(string? Name, Address Address, ValueCollection<string> ClaimIds, ValueCollection<string> ContractLeaseIds);

/// <summary>
/// 106H line 2's spouse or former spouse in a community-property state (and
/// B107 Q3's same fact) — its own record because the 8-year lookback can name
/// more than one person.
/// </summary>
public sealed record CommunityHouseholdMemberBody(string? Name, Address Address, string? CommunityState, bool? LivedWithDebtor);

/// <summary>The two 106H collections.</summary>
public static class CodebtorRules
{
    /// <summary>The codebtor collection's kind.</summary>
    public static readonly EntityKind<CodebtorBody> Kind = new("codebtor", "codebtors", "CODEBTOR", Parse);

    /// <summary>The community-household-member collection's kind.</summary>
    public static readonly EntityKind<CommunityHouseholdMemberBody> CommunityHouseholdMember =
        new("community_household_member", "community_household_members", "COMMUNITY_HOUSEHOLD_MEMBER", ParseCommunityHouseholdMember);

    /// <summary>Validates a codebtor body (Python's <c>parse_codebtor</c>).</summary>
    public static CodebtorBody Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new CodebtorBody(
            Name: FieldParsers.Text(payload["name"], "name", errors),
            Address: FieldParsers.ParseAddress(payload["address"], "address", errors),
            ClaimIds: FieldParsers.StringList(payload["claim_ids"], "claim_ids", errors, limit: 64),
            ContractLeaseIds: FieldParsers.StringList(payload["contract_lease_ids"], "contract_lease_ids", errors, limit: 64));
        IdRows.ThrowIfAny(errors);
        return body;
    }

    /// <summary>Validates a community-household-member body (Python's <c>parse_community_household_member</c>).</summary>
    public static CommunityHouseholdMemberBody ParseCommunityHouseholdMember(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new CommunityHouseholdMemberBody(
            Name: FieldParsers.Text(payload["name"], "name", errors),
            Address: FieldParsers.ParseAddress(payload["address"], "address", errors),
            CommunityState: FieldParsers.Text(payload["community_state"], "community_state", errors, limit: 2),
            LivedWithDebtor: FieldParsers.Boolean(payload["lived_with_debtor"], "lived_with_debtor", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }
}
