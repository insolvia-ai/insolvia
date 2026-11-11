using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Codebtors;

/// <summary>
/// A 106H codebtor row (from <c>codebtors.py</c>). The form asks WHICH debt
/// as a line number; the model stores the fact — the claim and contract ids —
/// and the forms engine renders the line. The id lists are attributed whole
/// by provenance and unchecked against their collections (progressive intake).
/// </summary>
public sealed record CodebtorBody(
    [property: Text] string? Name = null,
    Address? Address = null,
    ValueCollection<string>? ClaimIds = null,
    ValueCollection<string>? ContractLeaseIds = null)
{
    /// <summary>The codebtor's address; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new();

    /// <summary>The claims the codebtor shares.</summary>
    [Text(64)]
    public ValueCollection<string> ClaimIds { get; init; } = ClaimIds ?? [];

    /// <summary>The contracts and leases the codebtor shares.</summary>
    [Text(64)]
    public ValueCollection<string> ContractLeaseIds { get; init; } = ContractLeaseIds ?? [];
}

/// <summary>
/// 106H line 2's spouse or former spouse in a community-property state (and
/// B107 Q3's same fact) — its own record because the 8-year lookback can name
/// more than one person.
/// </summary>
public sealed record CommunityHouseholdMemberBody(
    [property: Text] string? Name = null,
    Address? Address = null,
    [property: Text(2)] string? CommunityState = null,
    bool? LivedWithDebtor = null)
{
    /// <summary>The member's address; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new();
}

/// <summary>The two 106H collections.</summary>
public static class CodebtorRules
{
    /// <summary>The codebtor collection's kind.</summary>
    public static readonly EntityKind<CodebtorBody> Kind = new("codebtor", "codebtors", "CODEBTOR");

    /// <summary>The community-household-member collection's kind.</summary>
    public static readonly EntityKind<CommunityHouseholdMemberBody> CommunityHouseholdMember =
        new("community_household_member", "community_household_members", "COMMUNITY_HOUSEHOLD_MEMBER");
}
