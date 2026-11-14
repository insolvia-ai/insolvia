using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Api.Portal;
using Insolvia.Domain;
using Insolvia.Domain.Access;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.DocumentRequests;
using Insolvia.Domain.Documents;
using Insolvia.Domain.Ports;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Insolvia.Api.Routes;

/// <summary>
/// The portal's document requests and uploads (ADR 0023 PR 5;
/// routes/portal_documents.py) — the documents a CLIENT's firm asked for, and
/// the upload that answers one. The portal's rules all hold (see
/// <see cref="PortalRoutes"/>): one filter, no case in a URL, the case reached
/// only through the binding's projection.
/// </summary>
/// <remarks>
/// <para><b>One upload seam, not two.</b> <c>POST /v1/portal/documents</c> is
/// the staff route's create with the client as uploader, channel
/// <c>client</c>, the REQUEST's kind, and the same presigned PUT
/// (<see cref="DocumentRoutes.UploadAsync"/>); <c>…/complete</c> is the
/// staff route's three steps, then the request is satisfied. A client's upload
/// is never auto-extracted (ADR 0019's ZDR item): complete calls the staff
/// route's trigger, whose channel check makes it a no-op here.</para>
/// <para><b>What a client sees</b> of a document: their own uploads' metadata,
/// against the request each answers. Never a staff upload, never a download URL.</para>
/// <para>Every request is on the access log as the client: <c>portal.read</c>
/// for the list, <c>document.create</c> for both halves of an upload.</para>
/// </remarks>
public sealed class PortalDocumentRoutes : IEndpointModule
{
    // DocumentRoutes' limit, for the same four short fields plus an id.
    private const int MaxRequestBytes = 8 * 1024;
    private const string Logger = "insolvia_api.api.routes.portal_documents";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/portal/document-requests", RequestsAsync).RequireClient();
        routes.MapPost("/v1/portal/documents", CreateAsync).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes)).RequireClient();
        routes.MapPost("/v1/portal/documents/{documentId}/complete", CompleteAsync).RequireClient();
    }

    /// <summary>List my document requests.</summary>
    /// <remarks>The documents the firm asked this client for, each with its status and the client's own uploads against it, and the overall progress. Waived requests are included — "no longer needed" is something to be told.</remarks>
    internal static async Task<PortalRequestList> RequestsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var client = context.CurrentClient();
        var caseId = await BoundCaseAsync(context, client, AccessAction.PortalRead, cancellationToken);
        var requests = await context.Port<IDocumentRequestStore>().ListForCaseAsync(caseId, cancellationToken);
        var own = (await context.Port<IDocumentStore>().ListForCaseAsync(caseId, cancellationToken)).Where(document => IsOwn(document, client)).ToList();
        return new([.. requests.Select(item => PortalRequestView.Of(item, own.Where(document => document.RequestId == item.Id)))], DocumentRequestRules.Progress(requests));
    }

    /// <summary>Start a document upload.</summary>
    /// <remarks>Authorises one upload against one of the firm's open document requests, and answers the document and a presigned PUT for its bytes; then call <c>…/complete</c>.</remarks>
    internal static async Task<Created<PortalUploadTicket>> CreateAsync(HttpContext context, PortalUploadRequest request, CancellationToken cancellationToken)
    {
        var client = context.CurrentClient();
        var caseId = await BoundCaseAsync(context, client, AccessAction.DocumentCreate, cancellationToken);
        var answered = await DocumentRoutes.OpenRequestOr400Async(context, caseId, request.RequestId!, cancellationToken);
        var document = DocumentRules.Create(request.Draft(answered.Kind), caseId, client.Subject, DocumentChannel.Client, answered.Id, context.Clock());
        await context.Port<IDocumentStore>().CreateAsync(document, cancellationToken);
        var upload = await DocumentRoutes.UploadAsync(context, document, cancellationToken);
        context.Log(Logger, "client document created", ("document_id", document.Id), ("request_id", answered.Id));
        return TypedResults.Created((string?)null, new PortalUploadTicket(PortalDocumentView.Of(document), upload));
    }

    /// <summary>Complete a document upload.</summary>
    /// <remarks>Say the upload's PUT finished: the document is stored and the request it answers is received. Idempotent; a 409 when the bytes are not there.</remarks>
    // HeadObject, clear the tag, write stored, then satisfy the request.
    internal static async Task<PortalUpload> CompleteAsync(HttpContext context, string documentId, CancellationToken cancellationToken)
    {
        var client = context.CurrentClient();
        var blobs = context.Port<IDocumentBlobStore>();
        await BoundCaseAsync(context, client, AccessAction.DocumentCreate, cancellationToken);
        var document = await OwnUploadOr404Async(context, client, documentId, cancellationToken);
        var stored = await blobs.StatAsync(document.StorageRef, cancellationToken)
            ?? throw new ConflictException("the object for this document is not in the bucket; the upload did not complete");
        await blobs.ClearUploadTagAsync(document.StorageRef, cancellationToken);
        var confirmed = DocumentRules.Confirm(document, stored);
        if (await context.Port<IDocumentStore>().UpdateAsync(confirmed, cancellationToken) is null)
        {
            throw new NotFoundException("document not found");
        }

        await DocumentRoutes.SatisfyRequestAsync(context, confirmed, cancellationToken);
        await DocumentRoutes.TriggerExtractionAsync(context, confirmed, client.Subject, cancellationToken);
        context.Log(Logger, "client document completed", ("document_id", documentId));
        return new(PortalDocumentView.Of(confirmed));
    }

    // The binding's case, if it still exists, with the request on the access
    // log — the portal's one way to a case. The row is written before the case is read.
    private static async Task<string> BoundCaseAsync(HttpContext context, ClientAccessor client, AccessAction action, CancellationToken cancellationToken)
    {
        await PortalRoutes.RecordAsync(context, client, action, cancellationToken);
        await PortalRoutes.PublicStatusAsync(context, client, cancellationToken);
        return client.CaseId;
    }

    // One of THIS client's uploads on their case. A staff upload, the other
    // spouse's, or another case's all answer the 404 of one that never existed.
    private static async Task<Document> OwnUploadOr404Async(HttpContext context, ClientAccessor client, string documentId, CancellationToken cancellationToken)
    {
        var document = await context.Port<IDocumentStore>().GetAsync(client.CaseId, documentId, cancellationToken);
        return document is not null && IsOwn(document, client) ? document : throw new NotFoundException("document not found");
    }

    private static bool IsOwn(Document document, ClientAccessor client) =>
        document.Channel == DocumentChannel.Client && document.UploadedBy == client.Subject;
}
