using System.Text.Json.Nodes;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.Auth;
using Insolvia.Core.Claims;
using Insolvia.Core.Debtors;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Fields;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.LibraryCreditors;
using Insolvia.Core.Questionnaires;

namespace Insolvia.Api.Firms;

/// <summary>
/// The JSON representations of the firm side — insolvia_core's
/// <c>firm_summary_json</c>, <c>firm_user_json</c>, <c>firm_user_summary_json</c>,
/// <c>firm_defaults_json</c>, <c>firm_client_json</c>, <c>library_creditor_json</c>,
/// <c>questionnaire_json</c> and <c>checklist_json</c>, and /v1/me's body.
/// Each keeps the null-versus-absent rule of the function it was ported from,
/// which differs on purpose between them (the comment on each says which);
/// key order is <see cref="FlaskJson"/>'s to sort.
/// </summary>
/// <remarks>
/// These belong in Insolvia.Core beside the records; they are here because
/// Core was closed to this change (#525).
/// </remarks>
public static class FirmViews
{
    /// <summary>
    /// The /v1/me body (routes/me.py <c>_me_body</c>) — one shape for GET and
    /// PATCH. <c>firm</c> is ABSENT, not null, for a caller in no firm, and
    /// its <c>permissions</c> are the EFFECTIVE map (an admin reads add_edit
    /// everywhere), where <see cref="User"/> carries the stored one.
    /// </summary>
    /// <param name="principal">The verified caller.</param>
    /// <param name="accessor">Their firm membership, or null.</param>
    /// <returns>The body.</returns>
    public static JsonObject Me(Principal principal, Accessor? accessor)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var body = new JsonObject
        {
            ["subject"] = principal.Subject,
            ["username"] = FlaskJson.Text(principal.Username),
            ["clientId"] = principal.ClientId,
            ["scopes"] = new JsonArray([.. principal.Scopes.Select(scope => (JsonNode?)JsonValue.Create(scope))]),
            ["expiresAt"] = principal.ExpiresAt is { } expires ? JsonValue.Create(expires) : null,
        };
        if (accessor is not null)
        {
            var user = accessor.User;
            var firm = new JsonObject
            {
                ["id"] = accessor.Firm.Id,
                ["name"] = accessor.Firm.Name,
                ["role"] = user.Role.ToWire(),
                ["firstName"] = user.FirstName,
                ["lastName"] = user.LastName,
                ["displayName"] = user.FullName,
                ["isAdmin"] = user.IsAdmin,
                ["accessAllCases"] = user.AccessAllCases,
                ["permissions"] = PermissionMap(Wire.Members<Feature>().Select(feature => (feature, Permissions.PermissionFor(user, feature)))),
                ["signatureBlock"] = SignatureBlockOf(user),
            };
            AddDefaults(firm, accessor.Firm);
            body["firm"] = firm;
        }

        return body;
    }

    /// <summary>
    /// <c>firm_summary_json</c>: the firm as its OWN members see it — no
    /// provisioning provenance (<c>createdBy</c>/<c>createdByEmail</c> name
    /// Insolvia staff), and the defaults as explicit nulls until set.
    /// </summary>
    /// <param name="firm">The firm.</param>
    /// <returns>The body.</returns>
    public static JsonObject FirmSummary(Firm firm)
    {
        ArgumentNullException.ThrowIfNull(firm);
        var body = new JsonObject
        {
            ["id"] = firm.Id,
            ["name"] = firm.Name,
            ["status"] = firm.Status.ToWire(),
            ["createdAt"] = FlaskJson.Millis(firm.CreatedAt),
            ["updatedAt"] = FlaskJson.Millis(firm.UpdatedAt),
        };
        AddDefaults(body, firm);
        return body;
    }

    /// <summary>
    /// <c>firm_user_summary_json</c>: a colleague as anyone in the firm may see
    /// them — a name, a job title and the signature block; no email, no
    /// permissions, no status.
    /// </summary>
    /// <param name="user">The colleague.</param>
    /// <returns>The body.</returns>
    public static JsonObject UserSummary(FirmUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new JsonObject
        {
            ["subject"] = user.Subject,
            ["firstName"] = user.FirstName,
            ["lastName"] = user.LastName,
            ["displayName"] = user.FullName,
            ["role"] = user.Role.ToWire(),
            ["signatureBlock"] = SignatureBlockOf(user),
        };
    }

    /// <summary>
    /// <c>firm_user_json</c>: the whole record, for the firm's administrators.
    /// <c>permissions</c> is the STORED map, not the effective one.
    /// </summary>
    /// <param name="user">The colleague.</param>
    /// <returns>The body.</returns>
    public static JsonObject User(FirmUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new JsonObject
        {
            ["subject"] = user.Subject,
            ["email"] = user.Email,
            ["firstName"] = user.FirstName,
            ["lastName"] = user.LastName,
            ["displayName"] = user.FullName,
            ["role"] = user.Role.ToWire(),
            ["isAdmin"] = user.IsAdmin,
            ["accessAllCases"] = user.AccessAllCases,
            ["permissions"] = PermissionMap(user.Permissions.Select(pair => (pair.Key, pair.Value))),
            ["status"] = user.Status.ToWire(),
            ["createdAt"] = FlaskJson.Millis(user.CreatedAt),
            ["updatedAt"] = FlaskJson.Millis(user.UpdatedAt),
            ["signatureBlock"] = SignatureBlockOf(user),
        };
    }

    /// <summary>
    /// <c>firm_client_json</c> (snake_case): absent optional fields OMITTED;
    /// <c>name</c> always present; the tax id only as its last four; the
    /// survivor a merged client went into; the stage only while a prospect.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <returns>The body.</returns>
    public static JsonObject Client(FirmClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var body = new JsonObject
        {
            ["id"] = client.Id,
            ["status"] = client.Status.ToWire(),
            ["created_at"] = FlaskJson.Micros(client.CreatedAt),
            ["updated_at"] = FlaskJson.Micros(client.UpdatedAt),
            ["created_by"] = client.CreatedBy,
            ["name"] = new JsonObject(),
        };

        var editable = (JsonObject)FlaskJson.Prune(new JsonObject
        {
            ["name"] = Name(client.Name),
            ["other_names_used"] = new JsonArray([.. client.OtherNamesUsed.Select(OtherNameOf)]),
            ["date_of_birth"] = client.DateOfBirth is { } born ? FlaskJson.Date(born) : null,
            ["residence_address"] = AddressOf(client.ResidenceAddress),
            ["mailing_address"] = AddressOf(client.MailingAddress),
            ["phone"] = FlaskJson.Text(client.Phone),
            ["mobile"] = FlaskJson.Text(client.Mobile),
            ["email"] = FlaskJson.Text(client.Email),
            ["lead_source"] = FlaskJson.Text(client.LeadSource),
            ["referred_by"] = FlaskJson.Text(client.ReferredBy),
            ["first_retained_at"] = client.FirstRetainedAt is { } retained ? FlaskJson.Date(retained) : null,
        })!;
        foreach (var (key, value) in editable.ToList())
        {
            editable.Remove(key);
            body[key] = value;
        }

        if (client.TaxIdLastFour is not null)
        {
            body["tax_id_last_four"] = client.TaxIdLastFour;
        }

        if (client.MergedInto is not null)
        {
            body["merged_into"] = client.MergedInto;
        }

        if (client.Prospect && client.ProspectStage is { } stage)
        {
            body["prospect_stage"] = stage.ToWire();
        }

        return body;
    }

    /// <summary>
    /// <c>library_creditor_json</c> (snake_case): <c>address</c> always
    /// present (pruned, so <c>{}</c> when empty), <c>notes</c> OMITTED when absent.
    /// </summary>
    /// <param name="creditor">The creditor.</param>
    /// <returns>The body.</returns>
    public static JsonObject LibraryCreditor(LibraryCreditor creditor)
    {
        ArgumentNullException.ThrowIfNull(creditor);
        var body = new JsonObject
        {
            ["id"] = creditor.Id,
            ["name"] = creditor.Name,
            ["address"] = FlaskJson.Prune(AddressOf(creditor.Address)),
            ["additional_notice_parties"] = new JsonArray([.. creditor.AdditionalNoticeParties.Select(NoticePartyOf)]),
            ["preferred"] = creditor.Preferred,
            ["created_at"] = FlaskJson.Micros(creditor.CreatedAt),
            ["updated_at"] = FlaskJson.Micros(creditor.UpdatedAt),
        };
        if (creditor.Notes is not null)
        {
            body["notes"] = creditor.Notes;
        }

        return body;
    }

    /// <summary>
    /// <c>questionnaire_json</c>: every section, switched off or not, with the
    /// firm's instructions (null when none) beside the default. <c>isDefault</c>
    /// says nothing is stored; <c>updatedAt</c>/<c>updatedBy</c> are explicit nulls then.
    /// </summary>
    /// <param name="config">The stored config, or null for the defaults.</param>
    /// <returns>The body.</returns>
    public static JsonObject Questionnaire(QuestionnaireConfig? config) => new()
    {
        ["isDefault"] = config is null,
        ["updatedAt"] = config is null ? null : FlaskJson.Micros(config.UpdatedAt),
        ["updatedBy"] = FlaskJson.Text(config?.UpdatedBy),
        ["sections"] = new JsonArray([.. Core.Questionnaires.Questionnaire.Resolve(config).Select(resolved => (JsonNode?)new JsonObject
        {
            ["id"] = resolved.Section.Id,
            ["title"] = resolved.Section.Title,
            ["covers"] = resolved.Section.Covers,
            ["switchable"] = resolved.Section.Switchable,
            ["enabled"] = resolved.Enabled,
            ["instructions"] = FlaskJson.Text(resolved.CustomInstructions),
            ["defaultInstructions"] = resolved.Section.DefaultInstructions,
        })]),
    };

    /// <summary>
    /// <c>checklist_json</c>: the list in force and the shipped default beside
    /// it; <c>description</c> an explicit null when absent.
    /// </summary>
    /// <param name="checklist">The stored checklist, or null for the default.</param>
    /// <returns>The body.</returns>
    public static JsonObject Checklist(DocumentChecklist? checklist) => new()
    {
        ["isDefault"] = checklist is null,
        ["updatedAt"] = checklist is null ? null : FlaskJson.Micros(checklist.UpdatedAt),
        ["updatedBy"] = FlaskJson.Text(checklist?.UpdatedBy),
        ["items"] = new JsonArray([.. DocumentChecklists.Resolve(checklist).Select(ChecklistItemOf)]),
        ["defaultItems"] = new JsonArray([.. DocumentChecklists.DefaultChecklist.Select(ChecklistItemOf)]),
    };

    // firm_defaults_json: EXPLICIT nulls, so "not set" never reads as an older API.
    private static void AddDefaults(JsonObject body, Firm firm)
    {
        body["defaultCourt"] = FlaskJson.Text(firm.DefaultCourt?.Court);
        body["defaultDivision"] = FlaskJson.Text(firm.DefaultCourt?.Division);
        body["defaultChapter"] = firm.DefaultChapter is { } chapter ? JsonValue.Create(chapter) : null;
        body["letterhead"] = firm.Letterhead is { } letterhead
            ? FlaskJson.Prune(new JsonObject
            {
                ["name"] = FlaskJson.Text(letterhead.Name),
                ["address"] = AddressOf(letterhead.Address),
                ["phone"] = FlaskJson.Text(letterhead.Phone),
                ["email"] = FlaskJson.Text(letterhead.Email),
            })
            : null;
    }

    // signature_block_json: pruned, keyed as a filing_professional body; null when none.
    private static JsonNode? SignatureBlockOf(FirmUser user) => user.SignatureBlock is { } block
        ? FlaskJson.Prune(new JsonObject
        {
            ["bar_number"] = FlaskJson.Text(block.BarNumber),
            ["bar_state"] = FlaskJson.Text(block.BarState),
            ["firm_name"] = FlaskJson.Text(block.FirmName),
            ["address"] = AddressOf(block.Address),
            ["phone"] = FlaskJson.Text(block.Phone),
            ["email"] = FlaskJson.Text(block.Email),
        })
        : null;

    private static JsonObject PermissionMap(IEnumerable<(Feature Feature, PermissionLevel Level)> levels)
    {
        var map = new JsonObject();
        foreach (var (feature, level) in levels)
        {
            map[feature.ToWire()] = level.ToWire();
        }

        return map;
    }

    // asdict(address), unpruned — each caller prunes at the level its wire shape needs.
    private static JsonObject AddressOf(Address address) => new()
    {
        ["line1"] = FlaskJson.Text(address.Line1),
        ["line2"] = FlaskJson.Text(address.Line2),
        ["city"] = FlaskJson.Text(address.City),
        ["state"] = FlaskJson.Text(address.State),
        ["postal_code"] = FlaskJson.Text(address.PostalCode),
        ["county"] = FlaskJson.Text(address.County),
        ["raw"] = FlaskJson.Text(address.Raw),
    };

    private static JsonObject Name(PersonName name) => new()
    {
        ["given"] = FlaskJson.Text(name.Given),
        ["middle"] = FlaskJson.Text(name.Middle),
        ["surname"] = FlaskJson.Text(name.Surname),
        ["suffix"] = FlaskJson.Text(name.Suffix),
    };

    private static JsonNode? OtherNameOf(OtherName name) => new JsonObject
    {
        ["id"] = name.Id,
        ["given"] = FlaskJson.Text(name.Given),
        ["middle"] = FlaskJson.Text(name.Middle),
        ["surname"] = FlaskJson.Text(name.Surname),
        ["business_name"] = FlaskJson.Text(name.BusinessName),
    };

    // library_creditors._notice_party_dict: the id, the pruned scalars, and
    // the address ALWAYS present (pruned, so {} when empty).
    private static JsonNode? NoticePartyOf(NoticeParty party)
    {
        var body = (JsonObject)FlaskJson.Prune(new JsonObject
        {
            ["name"] = FlaskJson.Text(party.Name),
            ["account_last4"] = FlaskJson.Text(party.AccountLast4),
        })!;
        body["id"] = party.Id;
        body["address"] = FlaskJson.Prune(AddressOf(party.Address));
        return body;
    }

    private static JsonNode? ChecklistItemOf(ChecklistItem item) => new JsonObject
    {
        ["title"] = item.Title,
        ["kind"] = item.Kind.ToWire(),
        ["description"] = FlaskJson.Text(item.Description),
    };
}
