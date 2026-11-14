using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Domain;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.DocumentRequests;
using Insolvia.Domain.Documents;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Jobs;
using Insolvia.Domain.Ports;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Insolvia.Api.Routes;

/// <summary>
/// A case's documents (routes/documents.py; issue 8.6), gated by
/// <c>documents</c>. The bytes never pass through the API: a create mints a
/// presigned PUT bound to the declared type and size, <c>…/complete</c> asks
/// the bucket whether the object landed (409 when it did not), clears the
/// reaper's tag and records what S3 counted; a read mints a short presigned GET.
/// </summary>
/// <remarks>
/// A completed staff upload of an extractable kind starts extraction on its
/// own (<see cref="TriggerExtractionAsync"/>), best effort.
/// </remarks>
public sealed class DocumentRoutes : IEndpointModule
{
    // A document create is four short fields.
    private const int MaxRequestBytes = 8 * 1024;
    private const int UploadUrlTtlSeconds = 15 * 60;
    private const int DownloadUrlTtlSeconds = 5 * 60;
    private const string Logger = "insolvia_api.api.routes.documents";
    private const string DocumentNotFound = "document not found";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/cases/{caseId}/documents", CreateAsync).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes)).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
        routes.MapPost("/v1/cases/{caseId}/documents/{documentId}/complete", CompleteAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/documents", ListAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/documents/{documentId}/url", UrlAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.ViewOnly);
        routes.MapDelete("/v1/cases/{caseId}/documents/{documentId}", DeleteAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
    }

    /// <summary>
    /// The upload block a create answers with: the presigned PUT and the exact
    /// headers it was signed for (S3 refuses anything else). Shared with the
    /// portal's upload route (18.19).
    /// </summary>
    internal static async Task<PresignedRequest> UploadAsync(HttpContext context, Document document, CancellationToken cancellationToken)
    {
        var url = await context.Port<IDocumentBlobStore>().UploadUrlAsync(document.StorageRef, document.ContentType.ToWire(), document.ByteSize, UploadUrlTtlSeconds, cancellationToken);
        return new PresignedRequest(url, "PUT", new UploadHeaders(document.ContentType.ToWire(), DocumentRules.UploadTag), WireFormat.Millis(DocumentRules.ExpiryTimestamp(UploadUrlTtlSeconds, context.Clock())));
    }

    /// <summary>The request a new upload answers — a 400 on <c>requestId</c> when it is not this case's, or no longer wants documents.</summary>
    internal static async Task<DocumentRequest> OpenRequestOr400Async(HttpContext context, string caseId, string requestId, CancellationToken cancellationToken)
    {
        var request = await context.Port<IDocumentRequestStore>().GetAsync(caseId, requestId, cancellationToken)
            ?? throw new FieldValidationException(new Dictionary<string, string> { ["requestId"] = "No such document request." });
        return DocumentRequestRules.AcceptsUploads(request)
            ? request
            : throw new FieldValidationException(new Dictionary<string, string> { ["requestId"] = "This document is no longer needed." });
    }

    /// <summary>A confirmed upload against a request marks it received — after the document write, best effort, never undone.</summary>
    internal static async Task SatisfyRequestAsync(HttpContext context, Document document, CancellationToken cancellationToken)
    {
        if (document.RequestId is null)
        {
            return;
        }

        var store = context.Port<IDocumentRequestStore>();
        var request = await store.GetAsync(document.CaseId, document.RequestId, cancellationToken);
        if (request is null)
        {
            return;
        }

        var satisfied = DocumentRequestRules.Satisfy(request, document.Id, context.Clock());
        if (!ReferenceEquals(satisfied, request) && satisfied != request)
        {
            await store.UpdateAsync(satisfied, cancellationToken);
            context.Log(Logger, "document request satisfied", ("case_id", document.CaseId), ("request_id", request.Id));
        }
    }

    /// <summary>
    /// A confirmed upload of an extractable kind starts extraction (issue 8.7)
    /// — BEST EFFORT: the upload already succeeded, and failing the confirm
    /// over a pipeline hiccup would tell the user their document did not
    /// arrive. Everything that goes wrong is logged and swallowed; the jobs
    /// endpoint is the retry path. Without a queue composed, nothing happens.
    /// The accept endpoint's one-active-job-per-document rule holds here too.
    /// A CLIENT'S upload is never auto-extracted (ADR 0023 decision 6): the
    /// portal's complete route may call this, and the channel check makes it a no-op.
    /// </summary>
    internal static async Task TriggerExtractionAsync(HttpContext context, Document document, string acceptedBy, CancellationToken cancellationToken)
    {
        if (document.Channel == DocumentChannel.Client
            || !JobRules.ExtractableDocumentKinds.Contains(document.Kind)
            || context.RequestServices.GetService<IJobStore>() is not { } jobs
            || context.RequestServices.GetService<IJobSender>() is null)
        {
            return;
        }

        Job job;
        try
        {
            var active = JobRules.FindActive(await jobs.ListForCaseAsync(document.CaseId, cancellationToken), JobRules.DocumentExtractionKind, document.Id);
            if (active is not null)
            {
                return;
            }

            job = JobRules.NewJob(JobRules.DocumentExtractionKind, document.CaseId, acceptedBy, document.Id, clock: context.Clock());
            await JobRoutes.SendAsync(context, job, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Log(Logger, "extraction auto-trigger failed; the upload itself succeeded", ("case_id", document.CaseId), ("document_id", document.Id));
            return;
        }

        context.Log(Logger, "extraction triggered by upload", ("case_id", document.CaseId), ("document_id", document.Id), ("job_id", job.Id));
    }

    /// <summary>Start a document upload.</summary>
    /// <remarks>
    /// Records a pending document and mints a presigned PUT for its bytes.
    /// The PUT is bound to the declared content type and size, and expires in
    /// 15 minutes. Call <c>…/complete</c> once the bytes have landed. With a
    /// <c>requestId</c>, the request must be this case's and still want documents.
    /// </remarks>
    internal static async Task<Created<DocumentUploadTicket>> CreateAsync(HttpContext context, string caseId, DocumentUploadRequest request, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var requestId = request.RequestId;
        await context.ReachableCaseAsync(caseId, AccessAction.DocumentCreate, cancellationToken);
        if (requestId is not null)
        {
            await OpenRequestOr400Async(context, caseId, requestId, cancellationToken);
        }

        var document = DocumentRules.Create(request.Draft(), caseId, accessor.Subject, requestId: requestId, clock: context.Clock());
        await context.Port<IDocumentStore>().CreateAsync(document, cancellationToken);
        var upload = await UploadAsync(context, document, cancellationToken);
        context.Log(Logger, "document created", ("case_id", caseId), ("document_id", document.Id), ("kind", document.Kind.ToWire()));
        return TypedResults.Created((string?)null, new DocumentUploadTicket(DocumentView.Of(document), upload));
    }

    /// <summary>Confirm a document upload.</summary>
    /// <remarks>
    /// Asks the bucket whether the bytes landed, and marks the document stored.
    /// 409 when the object is not in the bucket. Records the size the bucket
    /// counted, marks an answered document request received, and starts
    /// extraction for an extractable kind (best effort).
    /// </remarks>
    internal static async Task<DocumentResult> CompleteAsync(HttpContext context, string caseId, string documentId, CancellationToken cancellationToken)
    {
        var blobs = context.Port<IDocumentBlobStore>();
        await context.ReachableCaseAsync(caseId, AccessAction.DocumentCreate, cancellationToken);
        var document = await DocumentOr404Async(context, caseId, documentId, cancellationToken);
        var stored = await blobs.StatAsync(document.StorageRef, cancellationToken)
            ?? throw new ConflictException("the object for this document is not in the bucket; the upload did not complete");
        await blobs.ClearUploadTagAsync(document.StorageRef, cancellationToken);
        var confirmed = DocumentRules.Confirm(document, stored);
        if (await context.Port<IDocumentStore>().UpdateAsync(confirmed, cancellationToken) is null)
        {
            throw new NotFoundException(DocumentNotFound);
        }

        context.Log(Logger, "document completed", ("case_id", caseId), ("document_id", documentId), ("byte_size", confirmed.ByteSize));
        await SatisfyRequestAsync(context, confirmed, cancellationToken);
        var accessor = await context.CurrentAccessorAsync();
        await TriggerExtractionAsync(context, confirmed, accessor.Subject, cancellationToken);
        return new DocumentResult(DocumentView.Of(confirmed));
    }

    /// <summary>A case's documents.</summary>
    internal static async Task<DocumentList> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.DocumentRead, cancellationToken);
        var documents = await context.Port<IDocumentStore>().ListForCaseAsync(caseId, cancellationToken);
        return new DocumentList([.. documents.Select(DocumentView.Of)]);
    }

    /// <summary>A document's download URL.</summary>
    /// <remarks>A presigned GET for the bytes. It expires in 5 minutes; ask again rather than storing it.</remarks>
    internal static async Task<PresignedRequest> UrlAsync(HttpContext context, string caseId, string documentId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.DocumentDownload, cancellationToken);
        var document = await DocumentOr404Async(context, caseId, documentId, cancellationToken);
        var url = await context.Port<IDocumentBlobStore>().DownloadUrlAsync(document.StorageRef, DownloadUrlTtlSeconds, cancellationToken);
        return new PresignedRequest(url, "GET", null, WireFormat.Millis(DocumentRules.ExpiryTimestamp(DownloadUrlTtlSeconds, context.Clock())));
    }

    /// <summary>Delete a document and its bytes.</summary>
    internal static async Task<NoContent> DeleteAsync(HttpContext context, string caseId, string documentId, CancellationToken cancellationToken)
    {
        // The row first, so two racing deletes cannot both reach the bucket.
        await context.ReachableCaseAsync(caseId, AccessAction.DocumentDelete, cancellationToken);
        var document = await DocumentOr404Async(context, caseId, documentId, cancellationToken);
        if (!await context.Port<IDocumentStore>().DeleteAsync(caseId, documentId, cancellationToken))
        {
            throw new NotFoundException(DocumentNotFound);
        }

        await context.Port<IDocumentBlobStore>().DeleteAsync(document.StorageRef, cancellationToken);
        context.Log(Logger, "document deleted", ("case_id", caseId), ("document_id", documentId));
        return TypedResults.NoContent();
    }

    private static async Task<Document> DocumentOr404Async(HttpContext context, string caseId, string documentId, CancellationToken cancellationToken) =>
        await context.Port<IDocumentStore>().GetAsync(caseId, documentId, cancellationToken) ?? throw new NotFoundException(DocumentNotFound);
}

/// <summary>
/// A case's document requests (routes/document_requests.py, the case half;
/// ADR 0023 PR 5), gated by <c>documents</c>: list with progress, create,
/// apply the firm's checklist (adding only what is not already asked), set a
/// status, delete. The firm's checklist itself is <see cref="DocumentChecklistRoutes"/>.
/// </summary>
public sealed class DocumentRequestRoutes : IEndpointModule
{
    private const int MaxRequestBytes = 256 * 1024;
    private const string Logger = "insolvia_api.api.routes.document_requests";
    private const string RequestNotFound = "document request not found";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/document-requests", ListAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.ViewOnly);
        var sizeLimit = new RequestSizeLimitAttribute(MaxRequestBytes);
        routes.MapPost("/v1/cases/{caseId}/document-requests", CreateAsync).WithMetadata(sizeLimit).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
        routes.MapPost("/v1/cases/{caseId}/document-requests/from-checklist", ApplyChecklistAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
        routes.MapPatch("/v1/cases/{caseId}/document-requests/{requestId}", UpdateAsync).WithMetadata(sizeLimit).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/document-requests/{requestId}", DeleteAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
    }

    /// <summary>A case's document requests.</summary>
    /// <remarks>With the case's progress, counted from the same list.</remarks>
    internal static async Task<DocumentRequestList> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        return DocumentRequestList.Of(await context.Port<IDocumentRequestStore>().ListForCaseAsync(caseId, cancellationToken));
    }

    /// <summary>Ask the client for one document.</summary>
    internal static async Task<Created<DocumentRequestView>> CreateAsync(HttpContext context, string caseId, DocumentRequestCreation request, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var made = DocumentRequestRules.Create(request.ToItem(), caseId, accessor.Subject, context.Clock());
        await context.Port<IDocumentRequestStore>().CreateAsync(made, cancellationToken);
        context.Log(Logger, "document request created", ("case_id", caseId), ("id", made.Id));
        return TypedResults.Created((string?)null, DocumentRequestView.Of(made));
    }

    /// <summary>Apply the firm's checklist.</summary>
    /// <remarks>Asks for every document on the firm's checklist that this case has not already been asked for.</remarks>
    internal static async Task<ChecklistApplied> ApplyChecklistAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var store = context.Port<IDocumentRequestStore>();
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var checklist = DocumentChecklists.Resolve(await context.FirmStore().GetDocumentChecklistAsync(accessor.FirmId, cancellationToken));
        var added = DocumentRequestRules.FromChecklist(checklist, await store.ListForCaseAsync(caseId, cancellationToken), caseId, accessor.Subject, context.Clock());
        foreach (var made in added)
        {
            await store.CreateAsync(made, cancellationToken);
        }

        context.Log(Logger, "document checklist applied", ("case_id", caseId), ("added", added.Count));
        var list = DocumentRequestList.Of(await store.ListForCaseAsync(caseId, cancellationToken));
        return new ChecklistApplied(list.Requests, list.Progress, added.Count);
    }

    /// <summary>Set a document request's status.</summary>
    /// <remarks>Reopen it (<c>requested</c>) or waive it (<c>waived</c>); <c>received</c> follows an upload.</remarks>
    internal static async Task<DocumentRequestView> UpdateAsync(HttpContext context, string caseId, string requestId, DocumentRequestStatusChange change, CancellationToken cancellationToken)
    {
        var store = context.Port<IDocumentRequestStore>();
        var status = change.Status!.Value;
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var stored = await store.GetAsync(caseId, requestId, cancellationToken) ?? throw new NotFoundException(RequestNotFound);
        var written = await store.UpdateAsync(DocumentRequestRules.SetStatus(stored, status, context.Clock()), cancellationToken) ?? throw new NotFoundException(RequestNotFound);
        context.Log(Logger, "document request updated", ("case_id", caseId), ("id", requestId), ("status", status.ToWire()));
        return DocumentRequestView.Of(written);
    }

    /// <summary>Withdraw a document request.</summary>
    internal static async Task<NoContent> DeleteAsync(HttpContext context, string caseId, string requestId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        if (!await context.Port<IDocumentRequestStore>().DeleteAsync(caseId, requestId, cancellationToken))
        {
            throw new NotFoundException(RequestNotFound);
        }

        context.Log(Logger, "document request deleted", ("case_id", caseId), ("id", requestId));
        return TypedResults.NoContent();
    }
}
