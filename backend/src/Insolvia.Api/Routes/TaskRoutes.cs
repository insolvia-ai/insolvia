using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.CaseTasks;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Routes;

/// <summary>
/// Case tasks (routes/tasks.py; issue #356), gated by <c>tasks</c>: CRUD under
/// a case, and <c>GET /v1/me/tasks</c> — the caller's (or, <c>?scope=firm</c>,
/// everyone's) tasks across the cases THEY MAY SEE, walked case by case
/// rather than through an index that would re-derive the access rule. An
/// assignee must be an active user of the caller's firm. <c>overdue</c> is
/// judged against one UTC "today" for the whole response.
/// </summary>
public sealed class TaskRoutes : IEndpointModule
{
    private const int MaxRequestBytes = 16 * 1024;
    private const string Logger = "insolvia_api.api.routes.tasks";
    private const string TaskNotFound = "task not found";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/cases/{caseId}/tasks", CreateAsync).RequireAuth().Requires(Feature.Tasks, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/tasks", ListAsync).RequireAuth().Requires(Feature.Tasks, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/tasks/{taskId}", GetAsync).RequireAuth().Requires(Feature.Tasks, PermissionLevel.ViewOnly);
        routes.MapPatch("/v1/cases/{caseId}/tasks/{taskId}", UpdateAsync).RequireAuth().Requires(Feature.Tasks, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/tasks/{taskId}", DeleteAsync).RequireAuth().Requires(Feature.Tasks, PermissionLevel.AddEdit);
        routes.MapGet("/v1/me/tasks", ListMineAsync).RequireAuth().Requires(Feature.Tasks, PermissionLevel.ViewOnly);
    }

    private static async Task<IResult> CreateAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var draft = CaseTaskRules.ParseCreation(await context.BodyAsync(MaxRequestBytes));
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        await ValidateAssigneeAsync(context, accessor, draft.AssigneeSubject, cancellationToken);
        var task = CaseTaskRules.Create(draft, caseId, accessor.Subject, context.Clock());
        await context.Port<ICaseTaskStore>().CreateAsync(task, cancellationToken);
        context.Log(Logger, "task created", ("case_id", caseId), ("id", task.Id));
        return FlaskJson.Result(CaseViews.Task(task, context.Today()), StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var today = context.Today();
        var tasks = await context.Port<ICaseTaskStore>().ListForCaseAsync(caseId, cancellationToken);
        return FlaskJson.Result(new JsonObject { ["tasks"] = new JsonArray([.. tasks.Select(task => (JsonNode)CaseViews.Task(task, today))]) });
    }

    private static async Task<IResult> GetAsync(HttpContext context, string caseId, string taskId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var task = await context.Port<ICaseTaskStore>().GetAsync(caseId, taskId, cancellationToken) ?? throw new NotFoundException(TaskNotFound);
        return FlaskJson.Result(CaseViews.Task(task, context.Today()));
    }

    private static async Task<IResult> UpdateAsync(HttpContext context, string caseId, string taskId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var store = context.Port<ICaseTaskStore>();
        var changes = CaseTaskRules.ParseUpdate(await context.BodyAsync(MaxRequestBytes));
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var stored = await store.GetAsync(caseId, taskId, cancellationToken) ?? throw new NotFoundException(TaskNotFound);
        if (changes.AssigneeSubject is { IsSet: true, Value: { } assignee })
        {
            await ValidateAssigneeAsync(context, accessor, assignee, cancellationToken);
        }

        var written = await store.UpdateAsync(CaseTaskRules.ApplyChanges(stored, changes, context.Clock()), cancellationToken) ?? throw new NotFoundException(TaskNotFound);
        context.Log(Logger, "task updated", ("case_id", caseId), ("id", taskId));
        return FlaskJson.Result(CaseViews.Task(written, context.Today()));
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, string caseId, string taskId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        if (!await context.Port<ICaseTaskStore>().DeleteAsync(caseId, taskId, cancellationToken))
        {
            throw new NotFoundException(TaskNotFound);
        }

        context.Log(Logger, "task deleted", ("case_id", caseId), ("id", taskId));
        return FlaskJson.NoContent();
    }

    // Due date first (undated last), then creation, then id.
    private static async Task<IResult> ListMineAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var scope = CaseRoutes.Query(context, "scope") is { Length: > 0 } given ? given : "mine";
        if (scope is not ("mine" or "firm"))
        {
            throw new ValidationException("scope must be 'mine' or 'firm'");
        }

        var store = context.Port<ICaseTaskStore>();
        var tasks = new List<CaseTask>();
        foreach (var caseId in await ReachableCaseIdsAsync(context.Cases(), accessor, cancellationToken))
        {
            tasks.AddRange((await store.ListForCaseAsync(caseId, cancellationToken)).Where(task => scope == "firm" || task.AssigneeSubject == accessor.Subject));
        }

        var today = context.Today();
        var ordered = tasks
            .OrderBy(task => task.DueDate is null)
            .ThenBy(task => task.DueDate)
            .ThenBy(task => task.CreatedAt)
            .ThenBy(task => task.Id, StringComparer.Ordinal);
        return FlaskJson.Result(new JsonObject { ["tasks"] = new JsonArray([.. ordered.Select(task => (JsonNode)CaseViews.Task(task, today))]) });
    }

    /// <summary>Every case the accessor may see, the working list paged to its end.</summary>
    internal static async Task<List<string>> ReachableCaseIdsAsync(ICaseStore cases, Accessor accessor, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await cases.ListForAccessorAsync(accessor, CaseRules.MaxListLimit, cursor, cancellationToken: cancellationToken);
            ids.AddRange(page.Cases.Select(found => found.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return ids;
    }

    private static async Task ValidateAssigneeAsync(HttpContext context, Accessor accessor, string? subject, CancellationToken cancellationToken)
    {
        if (subject is null)
        {
            return;
        }

        var colleague = await context.FirmStore().GetUserAsync(accessor.FirmId, subject, cancellationToken);
        if (colleague is null || colleague.Status != FirmUserStatus.Active)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["assigneeSubject"] = "Must be an active user of your firm." });
        }
    }
}
