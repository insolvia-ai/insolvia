using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.ContractLeases;

/// <summary>
/// The executory contract / unexpired lease record — 106G, one row each (from
/// <c>contract_leases.py</c>). <see cref="Intention"/> and
/// <see cref="ListOnStatementOfIntention"/> are Form 108's columns, stored on
/// the lease because the fact is the lease's (issue #347).
/// </summary>
public sealed record ContractLeaseBody(
    [property: Text] string? CounterpartyName = null,
    Address? CounterpartyAddress = null,
    [property: Narrative] string? Description = null,
    ContractLeaseIntention? Intention = null,
    bool? ListOnStatementOfIntention = null)
{
    /// <summary>The counterparty's address; an empty one when none was given.</summary>
    public Address CounterpartyAddress { get; init; } = CounterpartyAddress ?? new();
}

/// <summary>The contract-and-lease collection.</summary>
public static class ContractLeaseRules
{
    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<ContractLeaseBody> Kind = new("contract_lease", "contract_leases", "CONTRACT_LEASE");
}

/// <summary>Form 108's assume-or-reject for a lease (Python's <c>CONTRACT_LEASE_INTENTIONS</c>).</summary>
public enum ContractLeaseIntention
{
    /// <summary>The wire value <c>assume</c>.</summary>
    [JsonStringEnumMemberName("assume")]
    Assume,

    /// <summary>The wire value <c>reject</c>.</summary>
    [JsonStringEnumMemberName("reject")]
    Reject,
}
