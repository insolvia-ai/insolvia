using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.Debtors;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.FirmClients;

/// <summary>A directory entry's status. Archiving is its own act, never a side effect of an edit.</summary>
public enum FirmClientStatus
{
    /// <summary>In the directory and usable.</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>Archived (every merged client is archived too).</summary>
    [JsonStringEnumMemberName("archived")]
    Archived,
}

/// <summary>Where a prospect is in the funnel (issue 14.3 / #355) — meaningful only while unretained.</summary>
public enum ProspectStage
{
    /// <summary>A possible client.</summary>
    [JsonStringEnumMemberName("possible")]
    Possible,

    /// <summary>A consultation is booked.</summary>
    [JsonStringEnumMemberName("consultation_scheduled")]
    ConsultationScheduled,

    /// <summary>Waiting for the signed engagement agreement.</summary>
    [JsonStringEnumMemberName("awaiting_signed_agreement")]
    AwaitingSignedAgreement,

    /// <summary>Gone cold.</summary>
    [JsonStringEnumMemberName("exhausted")]
    Exhausted,
}

/// <summary>
/// One person in a firm's client directory (ADR 0022 / #353; from
/// <c>firm_clients.py</c>) — the person who outlives any one matter, as
/// opposed to a debtor, which is their identity COPIED into one case.
/// </summary>
/// <remarks>
/// Server-owned members, never written by the whole-record save: the tax-id
/// POINTER (<see cref="TaxIdRef"/>/<see cref="TaxIdLastFour"/> — never the
/// number), <see cref="Status"/>, the merge's terminal <see cref="MergedInto"/>
/// and in-flight claim (<see cref="MergingInto"/>/<see cref="MergingFrom"/>),
/// and the funnel's <see cref="ProspectStage"/>.
/// </remarks>
public sealed record FirmClient(
    string Id,
    string FirmId,
    FirmClientStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CreatedBy)
{
    /// <summary>The client's name; a stored client always has a given name or a surname.</summary>
    public PersonName Name { get; init; } = new();

    /// <summary>The 8-year alias list.</summary>
    public ValueCollection<OtherName> OtherNamesUsed { get; init; } = [];

    /// <summary>Date of birth — never in the future.</summary>
    public DateOnly? DateOfBirth { get; init; }

    /// <summary>Where the client lives.</summary>
    public Address ResidenceAddress { get; init; } = new();

    /// <summary>Where the client's mail goes.</summary>
    public Address MailingAddress { get; init; } = new();

    /// <summary>A phone number.</summary>
    public string? Phone { get; init; }

    /// <summary>A mobile number.</summary>
    public string? Mobile { get; init; }

    /// <summary>An email address (free text; a directory contact value).</summary>
    public string? Email { get; init; }

    /// <summary>How the firm heard of them (free text in v1).</summary>
    public string? LeadSource { get; init; }

    /// <summary>Who referred them.</summary>
    public string? ReferredBy { get; init; }

    /// <summary>When the firm was first retained — unset means a PROSPECT.</summary>
    public DateOnly? FirstRetainedAt { get; init; }

    /// <summary>The pointer to the sealed tax id. Never sent to a caller.</summary>
    public string? TaxIdRef { get; init; }

    /// <summary>The one plain fact a screen shows of the tax id.</summary>
    public string? TaxIdLastFour { get; init; }

    /// <summary>TERMINAL: the survivor this client was folded into.</summary>
    public string? MergedInto { get; init; }

    /// <summary>The in-flight merge claim on the client being folded away.</summary>
    public string? MergingInto { get; init; }

    /// <summary>The in-flight merge claim on the survivor.</summary>
    public string? MergingFrom { get; init; }

    /// <summary>The funnel position while a prospect.</summary>
    public ProspectStage? ProspectStage { get; init; }

    /// <summary>Whether the client is archived.</summary>
    public bool Archived => Status == FirmClientStatus.Archived;

    /// <summary>Whether the firm has yet to be retained by this person.</summary>
    public bool Prospect => FirstRetainedAt is null;

    /// <summary>Whether the client was merged into another.</summary>
    public bool Merged => MergedInto is not null;

    /// <summary>
    /// Why this client cannot be opened for a case (or linked to a debtor)
    /// right now, or null when it can. A READ, for the message: the linking
    /// writes carry the same rule as a condition on this row, and that decides.
    /// </summary>
    public string? RefusalForNewCase()
    {
        if (MergedInto is not null)
        {
            return "That client was merged into another client — use that one.";
        }

        if (MergingInto is not null)
        {
            return "That client is being merged into another client.";
        }

        return Archived ? "That client is archived — restore them first." : null;
    }
}

/// <summary>
/// A client as written — <c>POST /v1/firm/clients</c> to create,
/// <c>PUT …/{clientId}</c> to replace: the caller-editable fields only, so a
/// PUT clears what it omits. Server-owned fields an echoed read sends
/// (status, the tax id's last four, the stage) are not members and are ignored.
/// </summary>
/// <param name="Name">The client's name; a given name or a surname is required.</param>
/// <param name="OtherNamesUsed">Names used in the last eight years, each addressed by its id.</param>
/// <param name="DateOfBirth">Date of birth — never in the future (<see cref="FirmClientRules.Create"/> checks it against the clock).</param>
/// <param name="ResidenceAddress">Where the client lives.</param>
/// <param name="MailingAddress">Where the client's mail goes.</param>
/// <param name="Phone">A phone number — the debtor's cap, so what a client accepts a debtor accepts.</param>
/// <param name="Mobile">A mobile number.</param>
/// <param name="Email">An email address (free text; a directory contact value).</param>
/// <param name="LeadSource">How the firm heard of them (free text in v1).</param>
/// <param name="ReferredBy">Who referred them.</param>
/// <param name="FirstRetainedAt">When the firm was first retained; absent for a prospect.</param>
public sealed record FirmClientDraft(
    PersonName? Name = null,
    ValueCollection<OtherName>? OtherNamesUsed = null,
    DateOnly? DateOfBirth = null,
    Address? ResidenceAddress = null,
    Address? MailingAddress = null,
    [property: Text(40)] string? Phone = null,
    [property: Text(40)] string? Mobile = null,
    [property: Text] string? Email = null,
    [property: Text] string? LeadSource = null,
    [property: Text] string? ReferredBy = null,
    DateOnly? FirstRetainedAt = null) : IValidatableObject
{
    /// <summary>The client's name.</summary>
    public PersonName Name { get; init; } = Name ?? new();

    /// <summary>The 8-year aliases.</summary>
    [Rows]
    public ValueCollection<OtherName> OtherNamesUsed { get; init; } = OtherNamesUsed ?? [];

    /// <summary>Where the client lives.</summary>
    public Address ResidenceAddress { get; init; } = ResidenceAddress ?? new();

    /// <summary>Where the client's mail goes.</summary>
    public Address MailingAddress { get; init; } = MailingAddress ?? new();

    /// <summary>A surname or a given name (a mononym is a given name).</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Name.Surname) && string.IsNullOrWhiteSpace(Name.Given))
        {
            yield return new ValidationResult("A surname or a given name is required.", [nameof(Name)]);
        }
    }
}
