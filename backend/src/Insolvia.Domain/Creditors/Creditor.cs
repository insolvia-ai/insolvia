using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Creditors;

/// <summary>
/// One deduplicated name-and-address for the creditor matrix (from
/// <c>creditors.py</c>). Separate from a claim on purpose: a debtor may owe
/// one creditor twice. The name is ONE line, not a <see cref="PersonName"/>
/// — creditors are predominantly entities. Nothing here dedupes; matching is
/// a suggestion to a human.
/// </summary>
public sealed record CreditorBody([property: Text] string? Name = null, Address? Address = null)
{
    /// <summary>The creditor's address; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new();
}

/// <summary>The creditor collection.</summary>
public static class CreditorRules
{
    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<CreditorBody> Kind = new("creditor", "creditors", "CREDITOR");
}
