using System.Text.Json.Serialization;
using Insolvia.Api.Http;
using Insolvia.Domain.Debtors;
using Insolvia.Domain.Fields;
using Insolvia.Domain.FirmClients;

namespace Insolvia.Api.Firms;

/// <summary>
/// A client in the firm's directory (<c>firm_client_json</c>): every
/// unanswered optional field absent, <c>name</c> always present, the tax id
/// only as its last four.
/// </summary>
/// <param name="Id">The client's id.</param>
/// <param name="Status">Active or archived; a merged client is archived.</param>
/// <param name="CreatedAt">Added, ISO 8601 UTC to the microsecond.</param>
/// <param name="UpdatedAt">Last changed, ISO 8601 UTC to the microsecond.</param>
/// <param name="CreatedBy">The subject of the staff member who added them.</param>
/// <param name="Name">The client's name, its unanswered parts absent.</param>
/// <param name="OtherNamesUsed">Names used in the last eight years; absent when none.</param>
/// <param name="DateOfBirth">Date of birth, YYYY-MM-DD.</param>
/// <param name="ResidenceAddress">Where the client lives, its unanswered parts absent.</param>
/// <param name="MailingAddress">Where the client's mail goes, its unanswered parts absent.</param>
/// <param name="Phone">A phone number.</param>
/// <param name="Mobile">A mobile number.</param>
/// <param name="Email">An email address.</param>
/// <param name="LeadSource">How the firm heard of them.</param>
/// <param name="ReferredBy">Who referred them.</param>
/// <param name="FirstRetainedAt">When the firm was first retained, YYYY-MM-DD; absent for a prospect.</param>
/// <param name="TaxIdLastFour">The last four digits of the tax id entered on one of their cases.</param>
/// <param name="MergedInto">The client this one was merged into; once set, edit that one instead.</param>
/// <param name="ProspectStage">Where a prospect is in the funnel; absent once retained.</param>
[SnakeCase]
public sealed record ClientView(
    string Id,
    FirmClientStatus Status,
    string CreatedAt,
    string UpdatedAt,
    string CreatedBy,
    PersonName Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OtherName>? OtherNamesUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DateOfBirth,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Address? ResidenceAddress,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Address? MailingAddress,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phone,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mobile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LeadSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReferredBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirstRetainedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TaxIdLastFour,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MergedInto,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProspectStage? ProspectStage)
{
    private static readonly Address NoAddress = new();

    /// <summary>The view of <paramref name="client"/>.</summary>
    /// <param name="client">The client.</param>
    /// <returns>The view.</returns>
    public static ClientView Of(FirmClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new(
            client.Id,
            client.Status,
            FlaskJson.Micros(client.CreatedAt),
            FlaskJson.Micros(client.UpdatedAt),
            client.CreatedBy,
            client.Name,
            client.OtherNamesUsed.Count == 0 ? null : client.OtherNamesUsed,
            client.DateOfBirth is { } born ? FlaskJson.Date(born) : null,
            client.ResidenceAddress == NoAddress ? null : client.ResidenceAddress,
            client.MailingAddress == NoAddress ? null : client.MailingAddress,
            client.Phone,
            client.Mobile,
            client.Email,
            client.LeadSource,
            client.ReferredBy,
            client.FirstRetainedAt is { } retained ? FlaskJson.Date(retained) : null,
            client.TaxIdLastFour,
            client.MergedInto,
            client.Prospect ? client.ProspectStage : null);
    }
}

/// <summary>The firm's clients.</summary>
/// <param name="Clients">Every client in the directory, archived and merged ones included.</param>
public sealed record ClientList(IReadOnlyList<ClientView> Clients);

/// <summary>The outcome of a merge: both records as they now stand.</summary>
/// <param name="Client">The survivor, which the other was folded into.</param>
/// <param name="Merged">The client folded away, now archived and pointing at the survivor.</param>
public sealed record ClientMergeView(ClientView Client, ClientView Merged);

/// <summary>
/// A client as written (<c>POST /v1/firm/clients</c>, <c>PUT …/{clientId}</c>):
/// the whole record, each unanswered field absent; a PUT clears what it
/// omits. Server-owned fields an echoed read sends are ignored, but a tax id
/// is refused — it is entered on a case. Parsed by <c>FirmClientRules.Parse</c>,
/// which owns validation; this record is its shape for the OpenAPI document.
/// </summary>
/// <param name="Name">The client's name; a given name or a surname is required.</param>
/// <param name="OtherNamesUsed">Names used in the last eight years.</param>
/// <param name="DateOfBirth">Date of birth, YYYY-MM-DD, never in the future.</param>
/// <param name="ResidenceAddress">Where the client lives.</param>
/// <param name="MailingAddress">Where the client's mail goes.</param>
/// <param name="Phone">A phone number.</param>
/// <param name="Mobile">A mobile number.</param>
/// <param name="Email">An email address.</param>
/// <param name="LeadSource">How the firm heard of them.</param>
/// <param name="ReferredBy">Who referred them.</param>
/// <param name="FirstRetainedAt">When the firm was first retained, YYYY-MM-DD; absent for a prospect.</param>
[SnakeCase]
public sealed record ClientRequest(
    PersonName Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OtherName>? OtherNamesUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DateOfBirth,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Address? ResidenceAddress,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Address? MailingAddress,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phone,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mobile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LeadSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReferredBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirstRetainedAt);

/// <summary>Archive a client, or bring one back (<c>PUT …/{clientId}/status</c>).</summary>
/// <param name="Status">The status to set.</param>
[SnakeCase]
public sealed record ClientStatusRequest(FirmClientStatus Status);

/// <summary>Place a prospect in the funnel, or take them out (<c>PUT …/{clientId}/prospect-stage</c>).</summary>
/// <param name="ProspectStage">The stage, or null to leave the funnel; the key itself is required.</param>
[SnakeCase]
public sealed record ProspectStageRequest(ProspectStage? ProspectStage);

/// <summary>Fold another client into this one (<c>POST …/{clientId}/merge</c>).</summary>
/// <param name="MergedClientId">The client to fold away, in the same firm.</param>
[SnakeCase]
public sealed record ClientMergeRequest(string MergedClientId);
