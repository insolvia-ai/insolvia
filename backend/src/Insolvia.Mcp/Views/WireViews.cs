using System.Globalization;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Candidates;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Debtors;
using Insolvia.Core.Documents;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Provenance;

namespace Insolvia.Mcp.Views;

/// <summary>
/// The record wire shapes the tools serve — insolvia_core's <c>case_json</c>,
/// <c>entity_json</c>, <c>debtor_json</c>, <c>document_json</c>,
/// <c>firm_client_json</c> and <c>candidate_review_json</c>. "A record's wire
/// shape is the API's wire shape" (mcp-surface.md § Schemas), so each keeps
/// its Python function's member order and its absent-versus-null rule.
/// </summary>
/// <remarks>
/// These belong in Insolvia.Core beside the records, as the Python had them,
/// where the API's case routes would share them; they are here because Core
/// was closed to this change (#532), the same position Insolvia.Api's
/// <c>FirmViews</c> is in. Moving both into Core is one mechanical PR.
/// </remarks>
public static class WireViews
{
    /// <summary>
    /// <c>case_json</c>: no <c>firmId</c> (every caller is in that firm), the
    /// registry reference and every optional member ABSENT until set, and the
    /// deletion stamp never — a deleted case is not served at all.
    /// </summary>
    /// <param name="target">The case.</param>
    /// <returns>The body.</returns>
    public static JsonObject Case(Case target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var body = new JsonObject
        {
            ["id"] = target.Id,
            ["createdBy"] = target.CreatedBy,
            ["chapter"] = target.Chapter,
            ["district"] = target.District,
            ["status"] = target.Status.ToWire(),
            ["createdAt"] = Micros(target.CreatedAt),
            ["updatedAt"] = Micros(target.UpdatedAt),
        };
        AddIfPresent(body, "court", target.Court);
        AddIfPresent(body, "division", target.Division);
        if (target.FormRevisions is { } revisions)
        {
            var pinned = new JsonObject();
            foreach (var (form, revision) in revisions)
            {
                pinned[form] = revision;
            }

            body["formRevisions"] = pinned;
        }

        AddIfPresent(body, "constantsSetId", target.ConstantsSetId);
        AddIfPresent(body, "exemptionSet", target.ExemptionSet?.ToWire());
        AddIfPresent(body, "filedAt", target.FiledAt is { } filed ? Date(filed) : null);
        AddIfPresent(body, "meeting341At", target.Meeting341At is { } meeting ? Date(meeting) : null);
        AddIfPresent(body, "caseNumber", target.CaseNumber);
        AddIfPresent(body, "judge", target.Judge);
        AddIfPresent(body, "trustee", target.Trustee);
        AddIfPresent(body, "officeFileNumber", target.OfficeFileNumber);
        AddIfPresent(body, "archivedAt", target.ArchivedAt is { } archived ? Micros(archived) : null);
        AddIfPresent(body, "archivedBy", target.ArchivedBy);
        return body;
    }

    /// <summary>
    /// <c>entity_json</c>: identity, provenance, <c>amended</c> (always — "not
    /// amended" is a fact), then the pruned body.
    /// </summary>
    /// <param name="entity">The record.</param>
    /// <returns>The body.</returns>
    public static JsonObject Entity(ICaseEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var body = new JsonObject
        {
            ["id"] = entity.Id,
            ["case_id"] = entity.CaseId,
            ["created_at"] = Micros(entity.CreatedAt),
            ["updated_at"] = Micros(entity.UpdatedAt),
            ["provenance"] = ProvenanceRules.ToJson(entity.Provenance),
            ["amended"] = entity.Amended,
        };
        Spread(body, BodyJson.Prune(entity.BodyNode()));
        return body;
    }

    /// <summary>
    /// <c>debtor_json</c> (without <c>differs_from_client</c>, which the tools
    /// never compute): identity, <c>client_id</c> when linked, provenance, the
    /// pruned body, and the tax id as the LAST-FOUR VIEW only — the full value
    /// has no wire shape at all.
    /// </summary>
    /// <param name="debtor">The debtor.</param>
    /// <returns>The body.</returns>
    public static JsonObject Debtor(Debtor debtor)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        var body = new JsonObject
        {
            ["id"] = debtor.Id,
            ["case_id"] = debtor.CaseId,
            ["filing_role"] = debtor.FilingRole.ToWire(),
            ["created_at"] = Micros(debtor.CreatedAt),
            ["updated_at"] = Micros(debtor.UpdatedAt),
        };
        AddIfPresent(body, "client_id", debtor.ClientId);
        body["provenance"] = ProvenanceRules.ToJson(debtor.Provenance);
        Spread(body, BodyJson.Prune(DebtorRules.BodyNode(debtor.Body)));
        if (debtor.TaxId is { } taxId)
        {
            body["tax_id"] = new JsonObject { ["kind"] = taxId.Kind.ToWire(), ["last_four"] = taxId.LastFour };
        }

        return body;
    }

    /// <summary>
    /// <c>document_json</c>: no <c>uploadedBy</c>, <c>storageRef</c> or
    /// <c>etag</c>; <c>status</c> always (a client branches on it), and
    /// <c>requestId</c> as null when the upload answers no request.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The body.</returns>
    public static JsonObject Document(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new JsonObject
        {
            ["id"] = document.Id,
            ["caseId"] = document.CaseId,
            ["kind"] = document.Kind.ToWire(),
            ["fileName"] = document.FileName,
            ["contentType"] = document.ContentType.ToWire(),
            ["byteSize"] = document.ByteSize,
            ["uploadedAt"] = Millis(document.UploadedAt),
            ["status"] = document.Status.ToWire(),
            ["channel"] = document.Channel.ToWire(),
            ["requestId"] = document.RequestId,
        };
    }

    /// <summary>
    /// <c>firm_client_json</c>: <c>name</c> always, the editable fields pruned,
    /// and <c>tax_id_last_four</c> as the ONLY tax-id member — never the ref.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <returns>The body.</returns>
    public static JsonObject FirmClient(FirmClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var body = new JsonObject
        {
            ["id"] = client.Id,
            ["status"] = client.Status.ToWire(),
            ["created_at"] = Micros(client.CreatedAt),
            ["updated_at"] = Micros(client.UpdatedAt),
            ["created_by"] = client.CreatedBy,
            ["name"] = new JsonObject(),
        };
        Spread(body, BodyJson.Prune(BodyJson.ToNode(new FirmClientDraft(
            client.Name,
            client.OtherNamesUsed,
            client.DateOfBirth,
            client.ResidenceAddress,
            client.MailingAddress,
            client.Phone,
            client.Mobile,
            client.Email,
            client.LeadSource,
            client.ReferredBy,
            client.FirstRetainedAt))));
        AddIfPresent(body, "tax_id_last_four", client.TaxIdLastFour);
        AddIfPresent(body, "merged_into", client.MergedInto);
        if (client.Prospect && client.ProspectStage is { } stage)
        {
            body["prospect_stage"] = stage.ToWire();
        }

        return body;
    }

    /// <summary>
    /// <c>candidate_review_json</c> — the <c>check_proposals</c> shape: the
    /// review status, the human's corrections and the resulting record id,
    /// each absent until there is one.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <returns>The body.</returns>
    public static JsonObject CandidateReview(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var body = new JsonObject
        {
            ["candidateId"] = candidate.Id,
            ["entityType"] = candidate.EntityType,
            ["status"] = candidate.Status.ToWire(),
        };
        AddIfPresent(body, "confirmedBy", candidate.ConfirmedBy);
        AddIfPresent(body, "confirmedAt", candidate.ConfirmedAt);
        if (candidate.CorrectedPayload is { } corrected)
        {
            body["correctedPayload"] = BodyJson.Prune(corrected);
        }

        AddIfPresent(body, "resultingRecordId", candidate.ResultingRecordId);
        return body;
    }

    // `fields.timestamp()`: microseconds and a literal Z — every case-domain stamp.
    private static string Micros(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    // documents.py's `_timestamp()`: milliseconds.
    private static string Millis(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void AddIfPresent(JsonObject target, string key, string? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    // Python's `{**identity, **body}`: the body's members after the identity's.
    private static void Spread(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            source.Remove(key);
            target[key] = value;
        }
    }
}
