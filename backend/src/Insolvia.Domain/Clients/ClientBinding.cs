using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.Fields;
using Insolvia.Domain.Firms;

namespace Insolvia.Domain.Clients;

/// <summary>
/// The filing roles a portal binding may answer for. <c>non_filing_spouse</c>
/// is a filing role and deliberately NOT here: ADR 0023 gives a non-filing
/// spouse no login in v1.
/// </summary>
public enum ClientRole
{
    /// <summary>The first debtor.</summary>
    [JsonStringEnumMemberName("debtor_1")]
    Debtor1,

    /// <summary>The second (joint) debtor.</summary>
    [JsonStringEnumMemberName("debtor_2")]
    Debtor2,
}

/// <summary>A binding's state: <c>invited → active</c> on first use, <c>→ revoked</c> when withdrawn.</summary>
public enum BindingStatus
{
    /// <summary>Sent a way in, not yet used. LIVE.</summary>
    [JsonStringEnumMemberName("invited")]
    Invited,

    /// <summary>In use. LIVE.</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>Withdrawn — kept, never deleted: it is the record the person once had access.</summary>
    [JsonStringEnumMemberName("revoked")]
    Revoked,
}

/// <summary>
/// One client's standing on one case (ADR 0023) — the portal's authorization
/// record, read from OUR store, never from a claim. <see cref="DisplayName"/>
/// is what the firm typed at invitation (a client cannot rename themselves in
/// the review queue); <see cref="Email"/> is lower-cased, the key the
/// refiled-case lookup matches on.
/// </summary>
public sealed record ClientBinding(
    string FirmId,
    string CaseId,
    string Subject,
    string Email,
    string DisplayName,
    ValueCollection<ClientRole> Roles,
    BindingStatus Status,
    string InvitedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>A LIVE binding holds its roles and admits its holder — <c>invited</c> counts.</summary>
    public bool Live => Status is BindingStatus.Invited or BindingStatus.Active;
}

/// <summary>A validated invitation body.</summary>
public sealed record InvitationDraft(string Email, string DisplayName, ValueCollection<ClientRole> Roles);

/// <summary>
/// An invitation (<c>POST /v1/cases/{caseId}/portal/invitation</c>), and a
/// fixture case's client. Any other key is refused: a caller sending
/// <c>caseId</c> or <c>subject</c> believes it controls something it does not.
/// </summary>
/// <param name="Email">The client's address; stored lower-cased.</param>
/// <param name="DisplayName">The name to greet them by.</param>
/// <param name="Roles">The debtors the login answers for, once each; absent for <c>debtor_1</c> alone.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortalInvitationRequest(
    [property: Required(ErrorMessage = "An email address is required."), MaxLength(FirmRules.MaxEmail, ErrorMessage = "Please keep this under 320 characters."), RegularExpression(FirmRules.EmailPattern, ErrorMessage = FirmRules.EmailMessage)] string? Email,
    [property: Required(ErrorMessage = FirmRules.NameRequired), Text(ClientBindings.MaxDisplayName), RegularExpression(FirmRules.NotBlank, ErrorMessage = FirmRules.NameRequired)] string? DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), MinLength(1, ErrorMessage = "Choose at least one debtor this person answers for.")] IReadOnlyList<ClientRole>? Roles = null)
    : IValidatableObject
{
    /// <summary>The draft a binding is created from: the address lower-cased, the roles in canonical order.</summary>
    /// <returns>The draft.</returns>
    // Defaults to the first debtor alone — safe on a joint case too: a held
    // debtor_1 is a narrowing or a 409, never a silent second holder.
    public InvitationDraft ToDraft() => new(
        TextRules.Lower(Email!),
        DisplayName!,
        Roles is null ? [ClientRole.Debtor1] : Wire.Members<ClientRole>().Where(Roles.Contains).ToValueCollection());

    /// <summary>Each role once.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Roles is not null && Roles.Distinct().Count() != Roles.Count)
        {
            yield return new ValidationResult("Must be debtor_1, debtor_2, or both, once each.", [nameof(Roles)]);
        }
    }
}

/// <summary>Parsing and the transitions of a portal binding (from <c>clients.py</c>).</summary>
public static class ClientBindings
{
    /// <summary>
    /// A revocation named a role another subject now holds — the binding was
    /// read before a narrowing moved the role. 409: reload and try again.
    /// </summary>
    public const string AccessChanged = "that client's access changed while this was written; reload and try again";

    /// <summary>The cap on the name a firm greets a client by.</summary>
    public const int MaxDisplayName = 200;

    /// <summary>A new binding, <see cref="BindingStatus.Invited"/>.</summary>
    public static ClientBinding CreateBinding(InvitationDraft draft, string firmId, string caseId, string subject, string invitedBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.Now(clock);
        return new ClientBinding(firmId, caseId, subject, draft.Email, draft.DisplayName, draft.Roles, BindingStatus.Invited, invitedBy, now, now);
    }

    /// <summary><c>invited → active</c>, on the client's first successful portal request. Anything else is returned unchanged.</summary>
    public static ClientBinding Activate(ClientBinding binding, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return binding.Status != BindingStatus.Invited
            ? binding
            : binding with { Status = BindingStatus.Active, UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>Withdraw access. Revoking twice is a conflict.</summary>
    public static ClientBinding Revoke(ClientBinding binding, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Status == BindingStatus.Revoked)
        {
            throw new ConflictException("that client's access has already been revoked");
        }

        return binding with { Status = BindingStatus.Revoked, UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>
    /// Decide whether <paramref name="newBinding"/> may be written, and which
    /// other LIVE bindings on its case it narrows (already narrowed — the
    /// store writes them in the same transaction). Refuses (409) a subject
    /// that already holds a live binding anywhere in the firm, and a
    /// narrowing that would empty a binding — that is a revocation nobody asked for.
    /// </summary>
    public static ValueCollection<ClientBinding> PlanBinding(ClientBinding newBinding, ClientBinding? previous, IEnumerable<ClientBinding> onCase)
    {
        ArgumentNullException.ThrowIfNull(newBinding);
        ArgumentNullException.ThrowIfNull(onCase);
        if (previous is not null && previous.Live)
        {
            throw new ConflictException("that person already has portal access to a case; revoke it before inviting them again");
        }

        var narrowed = new List<ClientBinding>();
        foreach (var other in onCase)
        {
            if (other.Subject == newBinding.Subject || !other.Live)
            {
                continue;
            }

            var kept = other.Roles.Where(role => !newBinding.Roles.Contains(role)).ToValueCollection();
            if (kept == other.Roles)
            {
                continue;
            }

            if (kept.Count == 0)
            {
                throw new ConflictException(
                    "another client on this case already answers for "
                    + string.Join(" and ", other.Roles.Select(role => role.ToWire()))
                    + "; revoke their access first, or invite this person for the other debtor");
            }

            narrowed.Add(other with { Roles = kept, UpdatedAt = newBinding.UpdatedAt });
        }

        return narrowed.ToValueCollection();
    }
}
