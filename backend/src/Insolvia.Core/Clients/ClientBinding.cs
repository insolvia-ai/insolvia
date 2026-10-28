using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.Firms;

namespace Insolvia.Core.Clients;

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

/// <summary>Parsing and the transitions of a portal binding (from <c>clients.py</c>).</summary>
public static class ClientBindings
{
    private static readonly HashSet<string> InvitationFields = new(StringComparer.Ordinal) { "email", "displayName", "roles" };

    /// <summary>
    /// <c>POST /v1/cases/&lt;id&gt;/portal/invitation</c>'s body. Unknown keys
    /// are REFUSED: a caller sending <c>caseId</c> or <c>subject</c> believes
    /// it controls something it does not.
    /// </summary>
    public static InvitationDraft ParseInvitation(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var unknown = payload.Select(pair => pair.Key).Where(key => !InvitationFields.Contains(key)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ValidationException("unsupported fields: " + string.Join(", ", unknown));
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var email = FirmParser.ParseEmail(payload["email"], errors);
        var displayName = ParseDisplayName(payload["displayName"], errors);
        var roles = ParseRoles(payload["roles"], errors);
        if (errors.Count > 0 || email is null || displayName is null || roles is null)
        {
            throw new FieldValidationException(errors);
        }

        return new InvitationDraft(email, displayName, roles);
    }

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

    private static string? ParseDisplayName(JsonNode? value, Dictionary<string, string> errors)
    {
        var raw = Json.AsString(value);
        if (raw is null || PyText.Strip(raw).Length == 0)
        {
            errors["displayName"] = "A name is required.";
            return null;
        }

        var name = PyText.Strip(raw);
        if (PyText.Length(name) > FirmParser.MaxDisplayName)
        {
            errors["displayName"] = $"Please keep this under {FirmParser.MaxDisplayName} characters.";
            return null;
        }

        return name;
    }

    // Defaults to the first debtor alone — safe on a joint case too: a held
    // debtor_1 is a narrowing or a 409, never a silent second holder.
    private static ValueCollection<ClientRole>? ParseRoles(JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return [ClientRole.Debtor1];
        }

        if (value is not JsonArray list || list.Count == 0)
        {
            errors["roles"] = "Choose at least one debtor this person answers for.";
            return null;
        }

        if (list.Any(role => Json.AsString(role) == "non_filing_spouse"))
        {
            errors["roles"] = "A non-filing spouse has no portal login; their details are asked of a debtor.";
            return null;
        }

        var parsed = new List<ClientRole>();
        foreach (var raw in list)
        {
            if (!Wire.TryParse<ClientRole>(Json.AsString(raw), out var role) || parsed.Contains(role))
            {
                errors["roles"] = "Must be debtor_1, debtor_2, or both, once each.";
                return null;
            }

            parsed.Add(role);
        }

        // Canonical order, so two invitations naming the same roles store the same value.
        return Wire.Members<ClientRole>().Where(parsed.Contains).ToValueCollection();
    }
}
