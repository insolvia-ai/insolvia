using System.Text.Json.Nodes;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;

namespace Insolvia.Core.FirmClients;

/// <summary>
/// Parsing, the transitions and the directory order of firm clients (from
/// <c>firm_clients.py</c>). The copy onto a case and back
/// (<c>debtor_from_client</c>, <c>differs_from_client</c>,
/// <c>recopy_from_client</c>, <c>client_updated_from_debtor</c>) needs the
/// debtor record and is the case half's (18.7).
/// </summary>
public static class FirmClientRules
{
    /// <summary>The cap on the lead source (free text in v1).</summary>
    public const int MaxLeadSource = 200;

    /// <summary>The cap on "referred by".</summary>
    public const int MaxReferredBy = 200;

    /// <summary>The debtor parser's phone cap — a value the client accepts must be one the debtor accepts.</summary>
    public const int MaxPhone = 40;

    /// <summary>What to say when a link raced a change on the client row that has since changed back.</summary>
    public const string LinkRaced = "That client changed while this was being saved — reload and try again.";

    // Refused, not ignored: a caller that sent digits would believe a number
    // was saved; one that sent a ref would be pointing at somebody's sealed id.
    private static readonly string[] RefusedTaxIdKeys = ["tax_id", "tax_id_ref", "tax_id_last_four"];

    /// <summary>
    /// Validate POST/PUT <c>/v1/firm/clients</c> (snake_case wire). Unknown
    /// keys are ignored — an echoed GET body sends server-owned fields — except
    /// the tax id, which is refused. <c>name.surname</c> or <c>name.given</c>
    /// is required (a mononym is a given name). <paramref name="requireName"/>
    /// false is for re-reading a stored body: shape re-checked, write rules not.
    /// </summary>
    public static FirmClientDraft Parse(JsonObject payload, bool requireName = true, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in RefusedTaxIdKeys)
        {
            if (payload[key] is not null)
            {
                errors[key] = "A client's tax ID cannot be set here; it is entered on a case.";
            }
        }

        var name = FieldParsers.ParseName(payload["name"], "name", errors);
        if (requireName && name.Surname is null && name.Given is null && !errors.Keys.Any(path => path.StartsWith("name", StringComparison.Ordinal)))
        {
            errors["name"] = "A surname or a given name is required.";
        }

        var otherNamesUsed = OtherNames.Parse(payload["other_names_used"], errors);
        var dateOfBirth = DateOfBirth(payload["date_of_birth"], errors, clock);
        var residenceAddress = FieldParsers.ParseAddress(payload["residence_address"], "residence_address", errors);
        var mailingAddress = FieldParsers.ParseAddress(payload["mailing_address"], "mailing_address", errors);
        var phone = FieldParsers.Text(payload["phone"], "phone", errors, MaxPhone);
        var mobile = FieldParsers.Text(payload["mobile"], "mobile", errors, MaxPhone);
        var email = FieldParsers.Text(payload["email"], "email", errors);
        var leadSource = FieldParsers.Text(payload["lead_source"], "lead_source", errors, MaxLeadSource);
        var referredBy = FieldParsers.Text(payload["referred_by"], "referred_by", errors, MaxReferredBy);
        var firstRetainedAt = FieldParsers.FormDate(payload["first_retained_at"], "first_retained_at", errors);

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return new FirmClientDraft(name, otherNamesUsed, dateOfBirth, residenceAddress, mailingAddress, phone, mobile, email, leadSource, referredBy, firstRetainedAt);
    }

    /// <summary>
    /// <c>POST /v1/firm/clients/&lt;survivor&gt;/merge</c>'s body:
    /// <c>{"merged_client_id": "&lt;id&gt;"}</c> — named for what happens to
    /// it, so the direction cannot be read backwards.
    /// </summary>
    public static string ParseClientMerge(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var merged = Json.AsString(payload["merged_client_id"]);
        if (merged is null || TextRules.Strip(merged).Length == 0)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["merged_client_id"] = "Choose the client to merge into this one." });
        }

        return TextRules.Strip(merged);
    }

    /// <summary><c>PUT /v1/firm/clients/&lt;id&gt;/status</c>: archive, or bring back. Never a delete.</summary>
    public static FirmClientStatus ParseClientStatus(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!Wire.TryParse<FirmClientStatus>(Json.AsString(payload["status"]), out var status))
        {
            throw new FieldValidationException(new Dictionary<string, string>
            {
                ["status"] = "Must be one of " + string.Join(", ", Wire.Names<FirmClientStatus>()) + ".",
            });
        }

        return status;
    }

    /// <summary>
    /// <c>PUT /v1/firm/clients/&lt;id&gt;/prospect-stage</c>: a stage to place a
    /// prospect, or <c>null</c> to take them out of the funnel. The key itself
    /// is required.
    /// </summary>
    public static ProspectStage? ParseProspectStage(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.ContainsKey("prospect_stage"))
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["prospect_stage"] = "Choose a stage, or null." });
        }

        var stage = payload["prospect_stage"];
        if (stage is null)
        {
            return null;
        }

        if (!Wire.TryParse<ProspectStage>(Json.AsString(stage), out var parsed))
        {
            throw new FieldValidationException(new Dictionary<string, string>
            {
                ["prospect_stage"] = "Must be one of " + string.Join(", ", Wire.Names<ProspectStage>()) + ".",
            });
        }

        return parsed;
    }

    /// <summary>A new, active client.</summary>
    public static FirmClient Create(FirmClientDraft draft, string firmId, string createdBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.Now(clock);
        return Apply(new FirmClient(Guid.NewGuid().ToString(), firmId, FirmClientStatus.Active, now, now, createdBy), draft);
    }

    /// <summary>
    /// The draft applied over <paramref name="existing"/> — a whole record,
    /// so what the draft leaves out is cleared — keeping what the caller does
    /// not own: id, firm, creation stamps, status, the tax-id pointer, the
    /// merge attributes and the stage.
    /// </summary>
    public static FirmClient Replace(FirmClient existing, FirmClientDraft draft, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return Apply(existing, draft) with { UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>The status write, and nothing else.</summary>
    public static FirmClient SetStatus(FirmClient existing, FirmClientStatus status, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return existing with { Status = status, UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>
    /// THE directory order — surname, given, middle, then id — case-folded so
    /// <c>de la Cruz</c> does not sort after every capitalised name. Every
    /// store sorts with this, so two stores cannot disagree.
    /// </summary>
    public static ValueCollection<FirmClient> Sorted(IEnumerable<FirmClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        return clients
            .OrderBy(client => TextRules.Casefold(client.Name.Surname ?? string.Empty), StringComparer.Ordinal)
            .ThenBy(client => TextRules.Casefold(client.Name.Given ?? string.Empty), StringComparer.Ordinal)
            .ThenBy(client => TextRules.Casefold(client.Name.Middle ?? string.Empty), StringComparer.Ordinal)
            .ThenBy(client => client.Id, StringComparer.Ordinal)
            .ToValueCollection();
    }

    /// <summary>
    /// What to tell a caller whose link write was refused by its condition on
    /// the client row (re-read after the refusal): the read check's own words,
    /// so a merge that raced the link reads like one that landed first.
    /// </summary>
    public static string RefusalForRefusedLink(FirmClient? client) =>
        client is null ? "No such client." : client.RefusalForNewCase() ?? LinkRaced;

    private static FirmClient Apply(FirmClient client, FirmClientDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return client with
        {
            Name = draft.Name,
            OtherNamesUsed = draft.OtherNamesUsed,
            DateOfBirth = draft.DateOfBirth,
            ResidenceAddress = draft.ResidenceAddress,
            MailingAddress = draft.MailingAddress,
            Phone = draft.Phone,
            Mobile = draft.Mobile,
            Email = draft.Email,
            LeadSource = draft.LeadSource,
            ReferredBy = draft.ReferredBy,
            FirstRetainedAt = draft.FirstRetainedAt,
        };
    }

    private static DateOnly? DateOfBirth(JsonNode? value, Dictionary<string, string> errors, TimeProvider? clock)
    {
        var parsed = FieldParsers.FormDate(value, "date_of_birth", errors);
        if (parsed is not null && parsed.Value > Clock.Today(clock))
        {
            errors["date_of_birth"] = "A date of birth cannot be in the future.";
            return null;
        }

        return parsed;
    }
}
