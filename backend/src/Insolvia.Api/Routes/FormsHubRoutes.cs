using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Courts;
using Insolvia.Core.Documents;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using Insolvia.Forms;

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
        routes.MapGet("/v1/cases/{caseId}/forms/{form}/preview", PreviewAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
    }

    /// <summary>
    /// Open (not-done) tasks anchored to each form series, keyed by the SAME
    /// short form key a hub row carries; a task anchored to a series this case
    /// does not file simply never matches a row.
    /// </summary>
    internal static Dictionary<string, int> OpenTaskCounts(IEnumerable<Core.CaseTasks.CaseTask> tasks)
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

    /// <summary>One hub row; <c>openTaskCount</c> present even at zero, <c>metric</c> only where the form defines one.</summary>
    public static JsonObject FormSummaryJson(FormSummary summary, int openTaskCount)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var body = new JsonObject
        {
            ["series"] = summary.Series,
            ["form"] = summary.Form,
            ["title"] = summary.Title,
            ["officialNumber"] = summary.OfficialNumber,
            ["problems"] = new JsonArray([.. summary.Problems.Select(problem => (JsonNode)PacketAssembly.ProblemJson(problem))]),
            ["openTaskCount"] = openTaskCount,
        };
        if (summary.Metric is { } metric)
        {
            body["metric"] = FormsHub.FormMetricJson(metric);
        }

        return body;
    }

    /// <summary>
    /// Every form this case files, each with its metric and its own slice of
    /// the completeness gate's problems — packet assembly's own functions,
    /// grouped per form, never a second definition of either.
    /// </summary>
    private static async Task<IResult> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var data = await CaseDataReads.ReadAsync(target, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        var summaries = FormsHub.Summaries(data, context.Today());
        var counts = OpenTaskCounts(await context.Port<ICaseTaskStore>().ListForCaseAsync(caseId, cancellationToken));
        return FlaskJson.Result(new JsonObject
        {
            ["forms"] = new JsonArray([.. summaries.Select(summary => (JsonNode)FormSummaryJson(summary, counts.GetValueOrDefault(summary.Form)))]),
        });
    }

    /// <summary>
    /// Render one form and mint a short-lived URL to the PDF — or answer the
    /// reasons it cannot render yet. <paramref name="form"/> is the short key
    /// (<c>b101</c>, <c>release.form</c>), not the slashed series id; a form
    /// this case does not file is a 404, as <c>assemble</c> drops it.
    /// </summary>
    /// <remarks>
    /// The Python's order, which decides the access-log rows a refused request
    /// leaves: the <c>form_preview.render</c> row, the series 404, B121's
    /// <c>taxid.read</c> rows, and only then the options' 400.
    /// </remarks>
    private static async Task<IResult> PreviewAsync(HttpContext context, string caseId, string form, CancellationToken cancellationToken)
    {
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
            return FlaskJson.Result(new JsonObject
            {
                ["problems"] = new JsonArray([.. problems.Select(problem => (JsonNode)PacketAssembly.ProblemJson(problem))]),
                ["options"] = FormOverlay.OutputOptionsJson(options),
            });
        }

        var blobs = context.Port<IDocumentBlobStore>();
        var storageRef = FormsHub.FormPreviewObjectKey(target.Id, FormsHub.NewPreviewId());
        await blobs.PutBytesAsync(storageRef, pdf, PreviewContentType, cancellationToken);
        context.Log(Logger, "form preview rendered", ("case_id", target.Id), ("series", seriesId));
        return FlaskJson.Result(new JsonObject
        {
            ["url"] = await blobs.DownloadUrlAsync(storageRef, DownloadUrlTtlSeconds, cancellationToken),
            ["method"] = "GET",
            ["expiresAt"] = FlaskJson.Millis(DocumentRules.ExpiryTimestamp(DownloadUrlTtlSeconds, context.Clock())),
            ["problems"] = new JsonArray(),
            ["options"] = FormOverlay.OutputOptionsJson(options),
        });
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

    /// <summary>The wire shape: optional keys absent, never null.</summary>
    public static JsonObject FilingSetJson(FilingSet filingSet)
    {
        ArgumentNullException.ThrowIfNull(filingSet);
        var body = new JsonObject
        {
            ["registryRelease"] = filingSet.RegistryRelease,
            ["filingMethod"] = filingSet.FilingMethod,
            ["orderBasis"] = filingSet.OrderBasis,
            ["namesBasis"] = filingSet.NamesBasis,
            ["maxBytes"] = filingSet.MaxBytes,
            ["maxBytesBasis"] = filingSet.MaxBytesBasis,
            ["documents"] = new JsonArray([.. filingSet.Documents.Select(document => (JsonNode)DocumentJson(document))]),
            ["checklist"] = new JsonArray([.. filingSet.Checklist.Select(item => (JsonNode)ItemJson(item))]),
        };
        if (filingSet.CourtCode is { } code && filingSet.CourtName is { } name)
        {
            var court = new JsonObject { ["code"] = code, ["name"] = name };
            if (filingSet.DivisionName is { } division)
            {
                court["divisionName"] = division;
            }

            body["court"] = court;
        }

        if (filingSet.Packet is { } packet)
        {
            body["packet"] = new JsonObject { ["id"] = packet.Id, ["createdAt"] = packet.CreatedAt };
        }

        return body;
    }

    private static JsonObject DocumentJson(FilingDocument document)
    {
        var body = new JsonObject
        {
            ["key"] = document.Key,
            ["title"] = document.Title,
            ["fileName"] = document.FileName,
            ["source"] = document.Source,
            ["handling"] = document.Handling,
            ["checks"] = new JsonArray([.. document.Checks.Select(check => (JsonNode)new JsonObject
            {
                ["check"] = check.Check,
                ["outcome"] = check.Outcome,
                ["message"] = check.Message,
            })]),
        };
        if (document.Note.Length > 0)
        {
            body["note"] = document.Note;
        }

        return body;
    }

    private static JsonObject ItemJson(ChecklistItem item)
    {
        var body = new JsonObject
        {
            ["id"] = item.Id,
            ["status"] = item.Status,
            ["title"] = item.Title,
            ["detail"] = item.Detail,
        };
        if (item.Link is { } link)
        {
            body["link"] = link;
        }

        return body;
    }

    private static async Task<IResult> GetAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var data = await CaseDataReads.ReadAsync(target, context.Port<IDebtorStore>(), context.Port<ICaseEntityStore>(), cancellationToken);
        var packets = await context.Port<IPacketStore>().ListForCaseAsync(caseId, cancellationToken);
        var today = context.Today();
        var filingSet = FilingSets.Build(data, [.. packets.Select(Packets.FromItem)], CourtRegistry.Shipped.Resolve(today), today);
        return FlaskJson.Result(FilingSetJson(filingSet));
    }
}
