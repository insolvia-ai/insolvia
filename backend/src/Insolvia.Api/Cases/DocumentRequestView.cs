using System.Text.Json.Serialization;
using Insolvia.Domain.Cases;
using Insolvia.Domain.DocumentRequests;
using Insolvia.Domain.Documents;

namespace Insolvia.Api.Cases;

/// <summary>A document asked of a case's client, as staff read it.</summary>
/// <param name="Id">The request's id.</param>
/// <param name="CaseId">The case.</param>
/// <param name="Title">What is asked for.</param>
/// <param name="Kind">The kind an upload against it is filed under.</param>
/// <param name="Description">What the client reads under the title; null when none.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="DocumentIds">The completed uploads against it, in completion order.</param>
/// <param name="CreatedAt">When it was asked, ISO 8601 UTC.</param>
/// <param name="UpdatedAt">When it was last written, ISO 8601 UTC.</param>
/// <param name="ReceivedAt">When the first upload completed, ISO 8601 UTC; null until then. Kept if staff reopen it.</param>
public sealed record DocumentRequestView(
    string Id,
    string CaseId,
    string Title,
    DocumentKind Kind,
    string? Description,
    DocumentRequestStatus Status,
    IReadOnlyList<string> DocumentIds,
    string CreatedAt,
    string UpdatedAt,
    string? ReceivedAt)
{
    /// <summary>The view of <paramref name="request"/>.</summary>
    /// <param name="request">The stored request.</param>
    /// <returns>The view.</returns>
    public static DocumentRequestView Of(DocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(
            request.Id,
            request.CaseId,
            request.Title,
            request.Kind,
            request.Description,
            request.Status,
            request.DocumentIds,
            CaseRules.Iso(request.CreatedAt),
            CaseRules.Iso(request.UpdatedAt),
            request.ReceivedAt is { } received ? CaseRules.Iso(received) : null);
    }
}

/// <summary>A case's document requests and its progress, read from the same list so they agree.</summary>
/// <param name="Requests">The requests, oldest first.</param>
/// <param name="Progress">Arrived against outstanding; the total excludes waived.</param>
public sealed record DocumentRequestList(IReadOnlyList<DocumentRequestView> Requests, DocumentRequestProgress Progress)
{
    /// <summary>The list of <paramref name="requests"/>.</summary>
    /// <param name="requests">The case's requests.</param>
    /// <returns>The list.</returns>
    public static DocumentRequestList Of(IReadOnlyList<DocumentRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return new([.. requests.Select(DocumentRequestView.Of)], DocumentRequestRules.Progress(requests));
    }
}

/// <summary>A case's document requests after the firm's checklist was applied.</summary>
/// <param name="Requests">Every request of the case, oldest first.</param>
/// <param name="Progress">Arrived against outstanding; the total excludes waived.</param>
/// <param name="Added">How many requests the checklist added — only those not already asked.</param>
public sealed record ChecklistApplied(IReadOnlyList<DocumentRequestView> Requests, DocumentRequestProgress Progress, int Added);

/// <summary>A document request as the client reads it in the portal: no case id, no document ids, only their own uploads.</summary>
/// <param name="Id">The request's id.</param>
/// <param name="Title">What is asked for.</param>
/// <param name="Kind">The kind an upload against it is filed under.</param>
/// <param name="Description">What the client reads under the title; null when none.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Uploads">The client's own uploads against it.</param>
public sealed record PortalRequestView(
    string Id,
    string Title,
    DocumentKind Kind,
    string? Description,
    DocumentRequestStatus Status,
    IReadOnlyList<PortalDocumentView> Uploads)
{
    /// <summary>The client's view of <paramref name="request"/>.</summary>
    /// <param name="request">The stored request.</param>
    /// <param name="ownUploads">The client's own uploads against it.</param>
    /// <returns>The view.</returns>
    public static PortalRequestView Of(DocumentRequest request, IEnumerable<Document> ownUploads)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ownUploads);
        return new(request.Id, request.Title, request.Kind, request.Description, request.Status, [.. ownUploads.Select(PortalDocumentView.Of)]);
    }
}

/// <summary>
/// One ad-hoc document request (<c>POST …/document-requests</c>). Parsed by
/// <c>DocumentRequestRules.ParseCreation</c>, which owns validation; this
/// record is its shape for the OpenAPI document.
/// </summary>
/// <param name="Title">What is asked for, one line.</param>
/// <param name="Kind">The kind an upload against it is filed under.</param>
/// <param name="Description">What the client reads under the title; absent when none.</param>
public sealed record DocumentRequestCreation(
    string Title,
    DocumentKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description);

/// <summary>
/// A document request's new status (<c>PATCH …/document-requests/{requestId}</c>).
/// Only <c>requested</c> and <c>waived</c> may be set; <c>received</c> follows an upload.
/// </summary>
/// <param name="Status">The status to set.</param>
public sealed record DocumentRequestStatusChange(DocumentRequestStatus Status);

/// <summary>The document requests a client's firm made of them, and the case's progress.</summary>
/// <param name="Requests">The requests, oldest first, each with the client's own uploads against it.</param>
/// <param name="Progress">Arrived against outstanding; the total excludes waived.</param>
public sealed record PortalRequestList(IReadOnlyList<PortalRequestView> Requests, DocumentRequestProgress Progress);
