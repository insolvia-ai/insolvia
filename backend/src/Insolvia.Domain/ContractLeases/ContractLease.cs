using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.ContractLeases;

/// <summary>
/// The executory contract / unexpired lease record — 106G, one row each (from
/// <c>contract_leases.py</c>). <see cref="Intention"/> and
/// <see cref="ListOnStatementOfIntention"/> are Form 108's columns, stored on
/// the lease because the fact is the lease's (issue #347).
/// </summary>
public sealed record ContractLeaseBody(
    string? CounterpartyName,
    Address CounterpartyAddress,
    string? Description,
    ContractLeaseIntention? Intention,
    bool? ListOnStatementOfIntention);

/// <summary>The contract-and-lease collection.</summary>
public static class ContractLeaseRules
{
    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<ContractLeaseBody> Kind = new("contract_lease", "contract_leases", "CONTRACT_LEASE", Parse);

    /// <summary>Validates a contract-or-lease body (Python's <c>parse_contract_lease</c>).</summary>
    public static ContractLeaseBody Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new ContractLeaseBody(
            CounterpartyName: FieldParsers.Text(payload["counterparty_name"], "counterparty_name", errors),
            CounterpartyAddress: FieldParsers.ParseAddress(payload["counterparty_address"], "counterparty_address", errors),
            Description: FieldParsers.Narrative(payload["description"], "description", errors),
            Intention: FieldParsers.Choice<ContractLeaseIntention>(payload["intention"], "intention", errors),
            ListOnStatementOfIntention: FieldParsers.Boolean(payload["list_on_statement_of_intention"], "list_on_statement_of_intention", errors));
        IdRows.ThrowIfAny(errors);
        return body;
    }
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
