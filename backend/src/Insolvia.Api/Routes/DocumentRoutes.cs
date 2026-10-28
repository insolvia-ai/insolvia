using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Documents;
using Insolvia.Core.Firms;
using Insolvia.Core.Jobs;
using Insolvia.Core.Ports;

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
    private const string TaggingHeader = "x-amz-tagging";
    private const string Logger = "insolvia_api.api.routes.documents";
    private const string DocumentNotFound = "document not found";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/cases/{caseId}/documents", CreateAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
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
    internal static async Task<JsonObject> UploadBlockAsync(HttpContext context, Document document, CancellationToken cancellationToken)
    {
        var url = await context.Port<IDocumentBlobStore>().UploadUrlAsync(document.StorageRef, document.ContentType.ToWire(), document.ByteSize, UploadUrlTtlSeconds, cancellationToken);
        return new JsonObject
        {
            ["url"] = url,
            ["method"] = "PUT",
            ["headers"] = new JsonObject { ["Content-Type"] = document.ContentType.ToWire(), [TaggingHeader] = DocumentRules.UploadTag },
            ["expiresAt"] = FlaskJson.Millis(DocumentRules.ExpiryTimestamp(UploadUrlTtlSeconds, context.Clock())),
        };
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

    private static async Task<IResult> CreateAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var body = await context.BodyAsync(MaxRequestBytes);
        var draft = DocumentRules.ParseUpload(body);
        var requestId = ParseRequestId(body);
        await context.ReachableCaseAsync(caseId, AccessAction.DocumentCreate, cancellationToken);
        if (requestId is not null)
        {
            await OpenRequestOr400Async(context, caseId, requestId, cancellationToken);
        }

        var document = DocumentRules.Create(draft, caseId, accessor.Subject, requestId: requestId, clock: context.Clock());
        await context.Port<IDocumentStore>().CreateAsync(document, cancellationToken);
        var upload = await UploadBlockAsync(context, document, cancellationToken);
        context.Log(Logger, "document created", ("case_id", caseId), ("document_id", document.Id), ("kind", document.Kind.ToWire()));
        return FlaskJson.Result(new JsonObject { ["document"] = CaseViews.Document(document), ["upload"] = upload }, StatusCodes.Status201Created);
    }

    private static async Task<IResult> CompleteAsync(HttpContext context, string caseId, string documentId, CancellationToken cancellationToken)
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
        return FlaskJson.Result(new JsonObject { ["document"] = CaseViews.Document(confirmed) });
    }

    private static async Task<IResult> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.DocumentRead, cancellationToken);
        var documents = await context.Port<IDocumentStore>().ListForCaseAsync(caseId, cancellationToken);
        return FlaskJson.Result(new JsonObject { ["documents"] = new JsonArray([.. documents.Select(document => (JsonNode)CaseViews.Document(document))]) });
    }

    private static async Task<IResult> UrlAsync(HttpContext context, string caseId, string documentId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.DocumentDownload, cancellationToken);
        var document = await DocumentOr404Async(context, caseId, documentId, cancellationToken);
        return FlaskJson.Result(new JsonObject
        {
            ["url"] = await context.Port<IDocumentBlobStore>().DownloadUrlAsync(document.StorageRef, DownloadUrlTtlSeconds, cancellationToken),
            ["method"] = "GET",
            ["expiresAt"] = FlaskJson.Millis(DocumentRules.ExpiryTimestamp(DownloadUrlTtlSeconds, context.Clock())),
        });
    }

    // The row first, so two racing deletes cannot both reach the bucket.
    private static async Task<IResult> DeleteAsync(HttpContext context, string caseId, string documentId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.DocumentDelete, cancellationToken);
        var document = await DocumentOr404Async(context, caseId, documentId, cancellationToken);
        if (!await context.Port<IDocumentStore>().DeleteAsync(caseId, documentId, cancellationToken))
        {
            throw new NotFoundException(DocumentNotFound);
        }

        await context.Port<IDocumentBlobStore>().DeleteAsync(document.StorageRef, cancellationToken);
        context.Log(Logger, "document deleted", ("case_id", caseId), ("document_id", documentId));
        return FlaskJson.NoContent();
    }

    private static async Task<Document> DocumentOr404Async(HttpContext context, string caseId, string documentId, CancellationToken cancellationToken) =>
        await context.Port<IDocumentStore>().GetAsync(caseId, documentId, cancellationToken) ?? throw new NotFoundException(DocumentNotFound);

    private static string? ParseRequestId(JsonObject body)
    {
        if (!body.TryGetPropertyValue("requestId", out var value) || value is null)
        {
            return null;
        }

        return Json.AsString(value) is { Length: > 0 } id
            ? id
            : throw new FieldValidationException(new Dictionary<string, string> { ["requestId"] = "Must be a document request id." });
    }
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
        routes.MapPost("/v1/cases/{caseId}/document-requests", CreateAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
        routes.MapPost("/v1/cases/{caseId}/document-requests/from-checklist", ApplyChecklistAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
        routes.MapPatch("/v1/cases/{caseId}/document-requests/{requestId}", UpdateAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/document-requests/{requestId}", DeleteAsync).RequireAuth().Requires(Feature.Documents, PermissionLevel.AddEdit);
    }

    private static async Task<IResult> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        return FlaskJson.Result(CaseViews.Requests(await context.Port<IDocumentRequestStore>().ListForCaseAsync(caseId, cancellationToken)));
    }

    private static async Task<IResult> CreateAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var item = DocumentRequestRules.ParseCreation(await context.BodyAsync(MaxRequestBytes));
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var made = DocumentRequestRules.Create(item, caseId, accessor.Subject, context.Clock());
        await context.Port<IDocumentRequestStore>().CreateAsync(made, cancellationToken);
        context.Log(Logger, "document request created", ("case_id", caseId), ("id", made.Id));
        return FlaskJson.Result(CaseViews.Request(made), StatusCodes.Status201Created);
    }

    private static async Task<IResult> ApplyChecklistAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
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
        var body = CaseViews.Requests(await store.ListForCaseAsync(caseId, cancellationToken));
        body["added"] = added.Count;
        return FlaskJson.Result(body);
    }

    private static async Task<IResult> UpdateAsync(HttpContext context, string caseId, string requestId, CancellationToken cancellationToken)
    {
        var store = context.Port<IDocumentRequestStore>();
        var status = DocumentRequestRules.ParseUpdate(await context.BodyAsync(MaxRequestBytes));
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var stored = await store.GetAsync(caseId, requestId, cancellationToken) ?? throw new NotFoundException(RequestNotFound);
        var written = await store.UpdateAsync(DocumentRequestRules.SetStatus(stored, status, context.Clock()), cancellationToken) ?? throw new NotFoundException(RequestNotFound);
        context.Log(Logger, "document request updated", ("case_id", caseId), ("id", requestId), ("status", status.ToWire()));
        return FlaskJson.Result(CaseViews.Request(written));
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, string caseId, string requestId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        if (!await context.Port<IDocumentRequestStore>().DeleteAsync(caseId, requestId, cancellationToken))
        {
            throw new NotFoundException(RequestNotFound);
        }

        context.Log(Logger, "document request deleted", ("case_id", caseId), ("id", requestId));
        return FlaskJson.NoContent();
    }
}
