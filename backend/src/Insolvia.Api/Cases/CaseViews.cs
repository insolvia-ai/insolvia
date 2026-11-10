using System.Text.Json.Nodes;
using Insolvia.Api.Http;
using Insolvia.Domain;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Cases;
using Insolvia.Domain.CaseTasks;
using Insolvia.Domain.Clients;
using Insolvia.Domain.Debtors;
using Insolvia.Domain.DocumentRequests;
using Insolvia.Domain.Documents;
using Insolvia.Domain.Events;
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
/// <see cref="Firms.FirmViews"/> gives: Core was closed to this change (#526).
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

    /// <summary>
    /// <c>document_json</c>: staff's view. No <c>uploadedBy</c>, <c>storageRef</c>
    /// or <c>etag</c>; <c>requestId</c> present as null when unset (the wire shape has it so).
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
            ["uploadedAt"] = FlaskJson.Millis(document.UploadedAt),
            ["status"] = document.Status.ToWire(),
            ["channel"] = document.Channel.ToWire(),
            ["requestId"] = FlaskJson.Text(document.RequestId),
        };
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

    /// <summary><c>request_json</c>: staff's view; <c>description</c> and <c>receivedAt</c> null when unset, no <c>createdBy</c>.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The body.</returns>
    public static JsonObject Request(DocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new JsonObject
        {
            ["id"] = request.Id,
            ["caseId"] = request.CaseId,
            ["title"] = request.Title,
            ["kind"] = request.Kind.ToWire(),
            ["description"] = FlaskJson.Text(request.Description),
            ["status"] = request.Status.ToWire(),
            ["documentIds"] = new JsonArray([.. request.DocumentIds.Select(id => (JsonNode)JsonValue.Create(id))]),
            ["createdAt"] = CaseRules.Iso(request.CreatedAt),
            ["updatedAt"] = CaseRules.Iso(request.UpdatedAt),
            ["receivedAt"] = request.ReceivedAt is { } received ? CaseRules.Iso(received) : null,
        };
    }

    /// <summary><c>requests_json</c>: the requests and the progress, from the same list so they agree.</summary>
    /// <param name="requests">The case's requests.</param>
    /// <returns>The body.</returns>
    public static JsonObject Requests(IReadOnlyList<DocumentRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var progress = DocumentRequestRules.Progress(requests);
        return new JsonObject
        {
            ["requests"] = new JsonArray([.. requests.Select(request => (JsonNode)Request(request))]),
            ["progress"] = new JsonObject
            {
                ["total"] = progress.Total,
                ["received"] = progress.Received,
                ["outstanding"] = progress.Outstanding,
                ["waived"] = progress.Waived,
            },
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

    /// <summary><c>task_json</c>, with the derived <c>overdue</c> against one <paramref name="today"/> for the whole response.</summary>
    /// <param name="task">The task.</param>
    /// <param name="today">The day every task in the response is judged against.</param>
    /// <returns>The body.</returns>
    public static JsonObject Task(CaseTask task, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(task);
        var body = new JsonObject
        {
            ["id"] = task.Id,
            ["caseId"] = task.CaseId,
            ["subject"] = task.Subject,
            ["done"] = task.Done,
            ["overdue"] = CaseTaskRules.IsOverdue(task, today),
            ["createdAt"] = CaseRules.Iso(task.CreatedAt),
            ["updatedAt"] = CaseRules.Iso(task.UpdatedAt),
            ["createdBy"] = task.CreatedBy,
        };
        AddIf(body, "description", task.Description);
        AddIf(body, "dueDate", task.DueDate is { } due ? FlaskJson.Date(due) : null);
        AddIf(body, "assigneeSubject", task.AssigneeSubject);
        AddIf(body, "formSeries", task.FormSeries);
        AddIf(body, "completedAt", task.CompletedAt is { } completed ? CaseRules.Iso(completed) : null);
        return body;
    }

    /// <summary><c>public_status_json</c>: <c>/v1/portal/me</c>'s case block — no id.</summary>
    /// <param name="status">The bound case's chapter and stage.</param>
    /// <returns>The body.</returns>
    public static JsonObject PublicStatus(CasePublicStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new JsonObject { ["chapter"] = status.Chapter, ["stage"] = status.Stage.ToWire() };
    }

    /// <summary><c>event_json</c>: snake_case; <c>generated</c> derived and sent; absent values omitted.</summary>
    /// <param name="calendarEvent">The event.</param>
    /// <returns>The body.</returns>
    public static JsonObject Event(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var body = new JsonObject
        {
            ["id"] = calendarEvent.Id,
            ["title"] = calendarEvent.Title,
            ["start"] = calendarEvent.Start,
            ["end"] = calendarEvent.End,
            ["all_day"] = calendarEvent.AllDay,
            ["attendees"] = new JsonArray([.. calendarEvent.Attendees.Select(subject => (JsonNode)JsonValue.Create(subject))]),
            ["generated"] = calendarEvent.Generated,
            ["dismissed"] = calendarEvent.Dismissed,
            ["created_by"] = calendarEvent.CreatedBy,
            ["created_at"] = CaseRules.Iso(calendarEvent.CreatedAt),
            ["updated_at"] = CaseRules.Iso(calendarEvent.UpdatedAt),
        };
        AddIf(body, "case_id", calendarEvent.CaseId);
        AddIf(body, "description", calendarEvent.Description);
        AddIf(body, "location", calendarEvent.Location);
        AddIf(body, "rule_id", calendarEvent.RuleId);
        AddIf(body, "rule_citation", calendarEvent.RuleCitation);
        return body;
    }

    private static void AddIf(JsonObject body, string key, string? value)
    {
        if (value is not null)
        {
            body[key] = value;
        }
    }
}
