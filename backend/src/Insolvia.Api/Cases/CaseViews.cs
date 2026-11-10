using System.Text.Json.Nodes;
using Insolvia.Api.Http;
using Insolvia.Domain;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Clients;
using Insolvia.Domain.Debtors;
using Insolvia.Domain.DocumentRequests;
using Insolvia.Domain.Documents;
using Insolvia.Domain.Provenance;
using Insolvia.Domain.TaxIds;

namespace Insolvia.Api.Cases;

/// <summary>
/// The JSON representations of the case side — insolvia_core's <c>case_json</c>,
/// <c>status_change_json</c>, <c>entity_json</c>, <c>debtor_json</c> (with
/// <c>tax_id_json</c>, the last-four view), <c>document_json</c>,
/// <c>portal_document_json</c>, <c>request_json</c>/<c>requests_json</c>/
/// <c>portal_request_json</c>, <c>task_json</c>, <c>note_json</c>,
/// <c>public_status_json</c>, the API's <c>event_json</c> and the courts
/// route's bodies — and ADR 0027's lifecycle event, which has no Python twin.
/// Each keeps the null-versus-absent rule of the function it was ported from,
/// part of the wire contract (the comment on each says which); key order is
/// <see cref="FlaskJson"/>'s to sort.
/// </summary>
/// <remarks>
/// Here rather than in Core, beside the records, for the reason
/// the firm views gave: Core was closed to this change (#526).
/// </remarks>
public static class CaseViews
{
    /// <summary>
    /// <c>debtor_json</c>. The tax id is the LAST-FOUR VIEW (<see cref="TaxId"/>)
    /// and nothing else; <c>client_id</c> present when the debtor names a client;
    /// <c>differs_from_client</c> present ONLY when the caller computed it (an
    /// empty list is the answer "identical", so it is sent).
    /// </summary>
    /// <param name="debtor">The debtor.</param>
    /// <param name="differsFromClient">The field paths that differ from the client, or null when not computed.</param>
    /// <returns>The body.</returns>
    public static JsonObject Debtor(Debtor debtor, IReadOnlyList<string>? differsFromClient = null)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        var body = BodyJson.Prune(DebtorRules.BodyNode(debtor.Body));
        if (debtor.TaxId is not null)
        {
            body["tax_id"] = TaxId(debtor.TaxId);
        }

        body["id"] = debtor.Id;
        body["case_id"] = debtor.CaseId;
        body["filing_role"] = debtor.FilingRole.ToWire();
        body["created_at"] = CaseRules.Iso(debtor.CreatedAt);
        body["updated_at"] = CaseRules.Iso(debtor.UpdatedAt);
        AddIf(body, "client_id", debtor.ClientId);
        if (differsFromClient is not null)
        {
            body["differs_from_client"] = new JsonArray([.. differsFromClient.Select(path => (JsonNode)JsonValue.Create(path))]);
        }

        body["provenance"] = ProvenanceRules.ToJson(debtor.Provenance);
        return body;
    }

    /// <summary><c>tax_id_json</c>: the kind and the last four. The ref is plumbing; the number has no wire shape at all.</summary>
    /// <param name="taxId">The pointer.</param>
    /// <returns>The body.</returns>
    public static JsonObject TaxId(TaxIdRef taxId)
    {
        ArgumentNullException.ThrowIfNull(taxId);
        return new JsonObject { ["kind"] = taxId.Kind.ToWire(), ["last_four"] = taxId.LastFour };
    }

    /// <summary><c>portal_document_json</c>: a CLIENT's view of their own upload — no case id, no channel.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The body.</returns>
    public static JsonObject PortalDocument(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new JsonObject
        {
            ["id"] = document.Id,
            ["requestId"] = FlaskJson.Text(document.RequestId),
            ["fileName"] = document.FileName,
            ["contentType"] = document.ContentType.ToWire(),
            ["byteSize"] = document.ByteSize,
            ["uploadedAt"] = FlaskJson.Millis(document.UploadedAt),
            ["status"] = document.Status.ToWire(),
        };
    }

    /// <summary><c>portal_request_json</c>: the CLIENT's view — no case id, no document ids, only their own uploads.</summary>
    /// <param name="request">The request.</param>
    /// <param name="ownUploads">The client's own uploads against it.</param>
    /// <returns>The body.</returns>
    public static JsonObject PortalRequest(DocumentRequest request, IEnumerable<Document> ownUploads)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ownUploads);
        return new JsonObject
        {
            ["id"] = request.Id,
            ["title"] = request.Title,
            ["kind"] = request.Kind.ToWire(),
            ["description"] = FlaskJson.Text(request.Description),
            ["status"] = request.Status.ToWire(),
            ["uploads"] = new JsonArray([.. ownUploads.Select(document => (JsonNode)PortalDocument(document))]),
        };
    }

    /// <summary><c>public_status_json</c>: <c>/v1/portal/me</c>'s case block — no id.</summary>
    /// <param name="status">The bound case's chapter and stage.</param>
    /// <returns>The body.</returns>
    public static JsonObject PublicStatus(CasePublicStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new JsonObject { ["chapter"] = status.Chapter, ["stage"] = status.Stage.ToWire() };
    }

    private static void AddIf(JsonObject body, string key, string? value)
    {
        if (value is not null)
        {
            body[key] = value;
        }
    }
}
