using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Domain;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.Courts;
using Insolvia.Domain.Documents;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Forms;
using Insolvia.Domain.Ports;

namespace Insolvia.Api.Routes;

/// <summary>
/// The forms hub (routes/forms_hub.py; issue 13.2): <c>GET …/forms</c> lists
/// every form the case's chapter and pin require, with per-form status;
/// <c>GET …/forms/{form}/preview</c> renders exactly one of them IN THIS
/// REQUEST — one fill is sub-second, so it is not a pipeline job. Both
/// <c>VIEW_ONLY</c> on <c>CASES</c>: rendering a form projects records the
/// caller can already read, and writes nothing to the case. The rendered bytes
/// ARE written, under <c>form-previews/</c> in the document bucket (the
/// prefix's own expiry rule is infra's), and served by a short-lived URL.
/// </summary>
/// <remarks>
/// 200 either way, the creditor-matrix contract: a URL with empty
/// <c>problems</c>, or every reason the form cannot render and no URL —
/// never both, never a partial form.
/// </remarks>
public sealed class FormsHubRoutes : IEndpointModule
{
    // The document/packet download TTL: the app opens it the moment the
    // preparer clicks "preview".
    private const int DownloadUrlTtlSeconds = 5 * 60;
    private const string PreviewContentType = "application/pdf";
    private const string Logger = "insolvia_api.api.routes.forms_hub";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/forms", ListAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/forms/{form}/preview", PreviewAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly)
            .QueryParameter("draftWatermark", "A \"DRAFT\" watermark on every page; on unless empty, `0` or `false`.")
            .QueryParameter("printDate", "A date and time stamp in the top margin; on unless empty, `0` or `false`.")
            .QueryParameter("signElectronically", "`/s/ Name` on every debtor signature line; on unless empty, `0` or `false`.")
            .QueryParameter("amendedOnly", "Only the forms and records marked amended; on unless empty, `0` or `false`.")
            .QueryParameter("signaturePages", "`all`, `only` or `omit`.")
            .QueryParameter("forms", "Only these forms, by short key, comma-separated (`b101,b106a`).");
    }

    /// <summary>
    /// Open (not-done) tasks anchored to each form series, keyed by the SAME
    /// short form key a hub row carries; a task anchored to a series this case
    /// does not file simply never matches a row.
    /// </summary>
    internal static Dictionary<string, int> OpenTaskCounts(IEnumerable<Insolvia.Domain.CaseTasks.CaseTask> tasks)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (task.FormSeries is not { } series || task.Done)
            {
                continue;
            }

            counts[series] = counts.GetValueOrDefault(series) + 1;
        }

        return counts;
    }

    /// <summary>Every form the case files, each with its status.</summary>
    /// <remarks>
    /// Each form carries its metric and its own slice of the completeness
    /// gate's problems — packet assembly's own functions, grouped per form.
    /// </remarks>
    internal static async Task<FormList> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var data = await CaseDataReads.ReadAsync(target, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        var summaries = FormsHub.Summaries(data, context.Today());
        var counts = OpenTaskCounts(await context.Port<ICaseTaskStore>().ListForCaseAsync(caseId, cancellationToken));
        return new FormList([.. summaries.Select(summary => FormSummaryView.Of(summary, counts.GetValueOrDefault(summary.Form)))]);
    }

    /// <summary>Render one form and answer a short-lived URL to the PDF, or the reasons it cannot render yet.</summary>
    /// <remarks>
    /// <c>form</c> is the short key (<c>b101</c>), not the slashed series id; a
    /// form this case does not file is a 404. The output options ride in the
    /// query string (<c>draftWatermark</c>, <c>printDate</c>,
    /// <c>signElectronically</c>, <c>amendedOnly</c>, <c>signaturePages</c>,
    /// <c>forms</c> as a comma list).
    /// </remarks>
    internal static async Task<FormPreviewView> PreviewAsync(HttpContext context, string caseId, string form, CancellationToken cancellationToken)
    {
        // The order is part of the contract: it decides the access-log rows a
        // refused request leaves: the form_preview.render row, the series 404,
        // B121's taxid.read rows, and only then the options' 400.
        var target = await context.ReachableCaseAsync(caseId, AccessAction.FormPreviewRender, cancellationToken);
        var data = await CaseDataReads.ReadAsync(target, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        var seriesId = $"form/{form}";
        if (!PacketAssembly.PacketFormSeriesFor(data).Contains(seriesId))
        {
            throw new NotFoundException("form not found");
        }

        if (form == "b121")
        {
            // The one preview that prints the full tax identifier: the logged
            // full-value read against the previewing caller, purpose `b121`.
            var accessor = await context.CurrentAccessorAsync();
            data = await CaseDataReads.DiscloseTaxIdsAsync(data, accessor.Subject, "b121", context.Port<ITaxIdStore>(), context.Port<ITaxIdCipher>(), context.AccessLog(), cancellationToken);
        }

        var options = FormOverlay.ParseOutputOptionsQuery(QueryArgs(context.Request.Query));
        var now = context.Clock().GetUtcNow();
        var (pdf, problems) = FormsHub.RenderFormPreview(data, seriesId, DateOnly.FromDateTime(now.UtcDateTime), now, options);

        if (pdf is null)
        {
            // GLBA: the case, the form and how many problems — never a
            // message, which can name case facts.
            context.Log(Logger, "form preview blocked", ("case_id", target.Id), ("series", seriesId), ("problems", problems.Count));
            return new FormPreviewView([.. problems.Select(PacketProblemView.Of)], OutputOptionsView.Of(options), null, null, null);
        }

        var blobs = context.Port<IDocumentBlobStore>();
        var storageRef = FormsHub.FormPreviewObjectKey(target.Id, FormsHub.NewPreviewId());
        await blobs.PutBytesAsync(storageRef, pdf, PreviewContentType, cancellationToken);
        context.Log(Logger, "form preview rendered", ("case_id", target.Id), ("series", seriesId));
        return new FormPreviewView(
            [],
            OutputOptionsView.Of(options),
            await blobs.DownloadUrlAsync(storageRef, DownloadUrlTtlSeconds, cancellationToken),
            "GET",
            WireFormat.Millis(DocumentRules.ExpiryTimestamp(DownloadUrlTtlSeconds, context.Clock())));
    }

    // Werkzeug's MultiDict `args[key]`: the FIRST value of a repeated key.
    private static Dictionary<string, string> QueryArgs(IQueryCollection query)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in query)
        {
            args[key] = values.Count > 0 ? values[0] ?? string.Empty : string.Empty;
        }

        return args;
    }
}

/// <summary>
/// <c>GET /v1/cases/{caseId}/filing-set</c> (routes/filing_set.py; ADR 0024
/// build PR 3): the filing set and hand-off checklist for the case's
/// district. The forms hub's access path; it writes nothing, renders nothing
/// and reads no tax identifier — what the packet's files measured, the
/// assembly worker already recorded on the packet.
/// </summary>
public sealed class FilingSetRoutes : IEndpointModule
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/filing-set", GetAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
    }

    /// <summary>The filing set and hand-off checklist for the case's district.</summary>
    internal static async Task<FilingSetView> GetAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var data = await CaseDataReads.ReadAsync(target, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        var packets = await context.Port<IPacketStore>().ListForCaseAsync(caseId, cancellationToken);
        var today = context.Today();
        return FilingSetView.Of(FilingSets.Build(data, [.. packets.Select(Packets.FromItem)], CourtRegistry.Shipped.Resolve(today), today));
    }
}
