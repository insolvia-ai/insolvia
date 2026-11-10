using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Domain;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.Documents;
using Insolvia.Domain.Engines.Projections;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Forms;
using Insolvia.Domain.Jobs;
using Insolvia.Domain.Ports;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Insolvia.Api.Routes;

/// <summary>
/// The API's half of the pipeline (routes/jobs.py; ADR 0018): accept a job,
/// read its status. The worker does the work; per ADR 0001 the client never
/// sees the queue — this status read is its only window.
/// </summary>
public sealed class JobRoutes : IEndpointModule
{
    // An accept body is one short field.
    private const int MaxRequestBytes = 16 * 1024;
    private const string Logger = "insolvia_api.api.routes.jobs";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/cases/{caseId}/jobs", AcceptAsync).RequestBody<JobRequest>().RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/jobs/{jobId}", GetAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
    }

    /// <summary>
    /// Hand a fresh job to the pipeline: the row FIRST (so a message can never
    /// name a record that does not exist), then the message; a send that fails
    /// marks the row failed (best effort) so it cannot wedge the idempotency
    /// rule, and the failure propagates.
    /// </summary>
    internal static async Task SendAsync(HttpContext context, Job job, CancellationToken cancellationToken)
    {
        var store = context.Port<IJobStore>();
        await store.CreateAsync(job, cancellationToken);
        try
        {
            await context.Port<IJobSender>().EnqueueAsync(job, cancellationToken);
        }
        catch (Exception)
        {
            await store.UpdateAsync(
                JobRules.Fail(job, new JobFailure("enqueue_failed", "The job could not be handed to the pipeline."), context.Clock()),
                JobStatus.Queued,
                CancellationToken.None);
            throw;
        }
    }

    /// <summary>Hand a job to the pipeline for the case.</summary>
    /// <remarks>
    /// Always 202 with the job now in flight — a fresh record, or the
    /// already-active one of the same (kind, document, options): a client that
    /// cannot tell whether its first request landed gets the same job back.
    /// 503 <c>PipelineUnavailable</c> when this deployment has no pipeline.
    /// </remarks>
    internal static async Task<Results<Accepted<JobView>, JsonHttpResult<ErrorBody>>> AcceptAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        if (context.RequestServices.GetService<IJobSender>() is null)
        {
            // A real deployed state: the image can roll out ahead of the queue.
            // 503 says "not yet"; accepting and never running would be a lie.
            return TypedResults.Json(new ErrorBody("PipelineUnavailable", "job pipeline is not available in this deployment", null), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var jobs = context.Port<IJobStore>();
        var (kind, documentId, rawOptions) = JobRules.ParseAcceptance(await context.BodyAsync(MaxRequestBytes));
        var accessor = await context.CurrentAccessorAsync();
        var found = await context.Cases().GetAsync(caseId, accessor, cancellationToken) ?? throw new NotFoundException(CaseRouteSupport.CaseNotFound);

        if (JobRules.DocumentScopedKinds.Contains(kind))
        {
            // Resolved against the ALREADY-AUTHORISED case: another case's
            // document answers the same 404 as a missing one.
            var document = await context.Port<IDocumentStore>().GetAsync(found.Id, documentId ?? "", cancellationToken) ?? throw new NotFoundException("document not found");
            if (!JobRules.ExtractableDocumentKinds.Contains(document.Kind))
            {
                throw new ValidationException("extraction reads: " + string.Join(", ", JobRules.ExtractableDocumentKinds.Select(k => k.ToWire())));
            }
        }

        JsonObject? options = null;
        if (JobRules.OptionsScopedKinds.Contains(kind))
        {
            // Validated AND canonicalised here: the stored job always carries
            // the full shape. `forms` is checked against the whole registry;
            // whether THIS case files it is the worker's gate.
            var parsed = FormOverlay.ParseOutputOptions(rawOptions, allowForms: true);
            if (parsed.Forms is { } forms)
            {
                var known = FormTemplates.SeriesIds().Select(series => series.StartsWith("form/", StringComparison.Ordinal) ? series[5..] : series).ToHashSet(StringComparer.Ordinal);
                var unknown = forms.Where(form => !known.Contains(form)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (unknown.Count > 0)
                {
                    throw new ValidationException($"unknown form(s): {string.Join(", ", unknown)}");
                }
            }

            options = FormOverlay.OutputOptionsJson(parsed);
        }

        var existing = JobRules.FindActive(await jobs.ListForCaseAsync(found.Id, cancellationToken), kind, documentId, options);
        if (existing is not null)
        {
            // The no-op repeat is not access-logged: the first accept was.
            return TypedResults.Accepted((string?)null, JobView.Of(existing));
        }

        var job = JobRules.NewJob(kind, found.Id, accessor.Subject, documentId, options, context.Clock());
        await SendAsync(context, job, cancellationToken);
        await context.RecordCaseAccessAsync(found.Id, AccessAction.JobAccept, allowed: true, cancellationToken);
        context.Log(Logger, "job accepted", ("case_id", found.Id), ("job_id", job.Id), ("kind", kind));
        return TypedResults.Accepted((string?)null, JobView.Of(job));
    }

    /// <summary>One job's status.</summary>
    /// <remarks>A job of another case answers the same 404 as a missing one.</remarks>
    internal static async Task<JobView> GetAsync(HttpContext context, string caseId, string jobId, CancellationToken cancellationToken)
    {
        // Not access-logged: a poll reads pipeline state, not case data.
        var jobs = context.Port<IJobStore>();
        await context.VisibleCaseAsync(caseId, cancellationToken);
        var job = await jobs.GetAsync(caseId, jobId, cancellationToken) ?? throw new NotFoundException("job not found");
        return JobView.Of(job);
    }
}

/// <summary>
/// Assembled packets (routes/packets.py; issue #96): list a case's packets
/// and mint one's download. The TRIGGER is a job (<c>packet_assembly</c>).
/// </summary>
public sealed class PacketRoutes : IEndpointModule
{
    private const int DownloadUrlTtlSeconds = 5 * 60;

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/packets", ListAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/packets/{packetId}/url", UrlAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
    }

    /// <summary>A case's packets, newest first.</summary>
    /// <remarks>Re-assembly creates a packet rather than replacing one.</remarks>
    internal static async Task<PacketList> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var packets = context.Port<IPacketStore>();
        await context.ReachableCaseAsync(caseId, AccessAction.PacketRead, cancellationToken);
        var listed = await packets.ListForCaseAsync(caseId, cancellationToken);
        return new PacketList([.. listed.Select(item => PacketView.Of(Packets.FromItem(item)))]);
    }

    /// <summary>A short-lived URL serving one packet's bytes.</summary>
    internal static async Task<PacketUrlView> UrlAsync(HttpContext context, string caseId, string packetId, CancellationToken cancellationToken)
    {
        var packets = context.Port<IPacketStore>();
        var blobs = context.Port<IDocumentBlobStore>();
        await context.ReachableCaseAsync(caseId, AccessAction.PacketDownload, cancellationToken);
        var packet = await packets.GetAsync(caseId, packetId, cancellationToken) ?? throw new NotFoundException("packet not found");
        return new PacketUrlView(
            await blobs.DownloadUrlAsync(packet.StorageRef, DownloadUrlTtlSeconds, cancellationToken),
            "GET",
            FlaskJson.Millis(DocumentRules.ExpiryTimestamp(DownloadUrlTtlSeconds, context.Clock())));
    }
}

/// <summary>
/// What a case is worth, what it owes, and whether it could be filed
/// (routes/case_summary.py). <c>readyToFile</c> and <c>problems</c> come from
/// the SAME completeness gate packet assembly runs.
/// </summary>
public sealed class CaseSummaryRoutes : IEndpointModule
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/summary", SummaryAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
    }

    /// <summary>What the case is worth, what it owes, and whether it could be filed.</summary>
    /// <remarks>Money is a decimal string, never a JSON number.</remarks>
    internal static async Task<CaseSummaryView> SummaryAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var found = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var data = await CaseDataReads.ReadAsync(found, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        return CaseSummaryView.Of(CaseSummaries.Summarise(data));
    }
}
