using System.Text.Json.Serialization;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Debtors;
using Insolvia.Domain.Provenance;
using Insolvia.Domain.TaxIds;

namespace Insolvia.Api.Cases;

/// <summary>
/// A debtor as staff read it: the stored body at the top level, beside the
/// server's own fields. The tax id leaves as its kind and last four only.
/// </summary>
/// <param name="Id">The debtor's id, stable across saves.</param>
/// <param name="CaseId">The case.</param>
/// <param name="FilingRole">Which debtor on the petition.</param>
/// <param name="CreatedAt">First saved, ISO 8601 UTC.</param>
/// <param name="UpdatedAt">Last saved, ISO 8601 UTC.</param>
/// <param name="Body">The case data; its unanswered fields are absent.</param>
/// <param name="TaxId">The tax id's kind and last four; absent when none is stored.</param>
/// <param name="ClientId">The firm client this debtor was copied from; absent when unlinked.</param>
/// <param name="DiffersFromClient">The body paths that no longer match the client record; absent unless the caller may see the client directory.</param>
/// <param name="Provenance">Where each populated field came from, by body path.</param>
[SnakeCase]
public sealed record DebtorView(
    string Id,
    string CaseId,
    FilingRole FilingRole,
    string CreatedAt,
    string UpdatedAt,
    [property: Flatten] DebtorBody Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TaxIdView? TaxId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? DiffersFromClient,
    IReadOnlyDictionary<string, ProvenanceEntry> Provenance)
{
    /// <summary>The view of <paramref name="debtor"/>.</summary>
    /// <param name="debtor">The stored debtor.</param>
    /// <param name="differsFromClient">The paths that differ from the client record, for a caller who may see it.</param>
    /// <returns>The view.</returns>
    public static DebtorView Of(Debtor debtor, IReadOnlyList<string>? differsFromClient = null)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        return new(
            debtor.Id,
            debtor.CaseId,
            debtor.FilingRole,
            CaseRules.Iso(debtor.CreatedAt),
            CaseRules.Iso(debtor.UpdatedAt),
            debtor.Body,
            debtor.TaxId is null ? null : TaxIdView.Of(debtor.TaxId),
            debtor.ClientId,
            differsFromClient,
            debtor.Provenance);
    }
}

/// <summary>A stored tax id as it may leave the server: its kind and last four, never the number.</summary>
/// <param name="Kind">SSN or ITIN.</param>
/// <param name="LastFour">The last four digits.</param>
[SnakeCase]
public sealed record TaxIdView(TaxIdKind Kind, string LastFour)
{
    /// <summary>The view of <paramref name="taxId"/>.</summary>
    /// <param name="taxId">The pointer to the sealed number.</param>
    /// <returns>The view.</returns>
    public static TaxIdView Of(TaxIdRef taxId)
    {
        ArgumentNullException.ThrowIfNull(taxId);
        return new(taxId.Kind, taxId.LastFour);
    }
}

/// <summary>
/// A debtor as written (<c>PUT …/debtors/{filingRole}</c>): the whole body,
/// each unanswered field absent. Parsed by <c>DebtorRules.Parse</c>, which
/// owns validation; this record is its shape for the OpenAPI document.
/// </summary>
/// <param name="Body">The case data.</param>
/// <param name="TaxId">The number being entered (<c>value</c>), or the last four being kept (<c>last_four</c>); absent to remove it.</param>
/// <param name="Provenance">Where each populated field came from, by body path.</param>
[SnakeCase]
public sealed record DebtorRequest(
    [property: Flatten] DebtorBody Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TaxIdInput? TaxId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, ProvenanceEntry>? Provenance);

/// <summary>Which firm client a debtor is (<c>PUT …/debtors/{filingRole}/client</c>).</summary>
/// <param name="ClientId">The client, in the case's firm.</param>
[SnakeCase]
public sealed record DebtorLinkRequest(string ClientId);
