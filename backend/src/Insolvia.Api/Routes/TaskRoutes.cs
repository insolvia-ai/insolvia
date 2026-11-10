using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Domain;
using Insolvia.Domain.Access;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.Cases;
using Insolvia.Domain.CaseTasks;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Ports;
using Microsoft.AspNetCore.Http.HttpResults;

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
        routes.MapPost("/v1/cases/{caseId}/tasks", CreateAsync).RequestBody<TaskCreation>().RequireAuth().Requires(Feature.Tasks, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/tasks", ListAsync).RequireAuth().Requires(Feature.Tasks, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/tasks/{taskId}", GetAsync).RequireAuth().Requires(Feature.Tasks, PermissionLevel.ViewOnly);
        routes.MapPatch("/v1/cases/{caseId}/tasks/{taskId}", UpdateAsync).RequestBody<TaskChange>().RequireAuth().Requires(Feature.Tasks, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/tasks/{taskId}", DeleteAsync).RequireAuth().Requires(Feature.Tasks, PermissionLevel.AddEdit);
        routes.MapGet("/v1/me/tasks", ListMineAsync).RequireAuth().Requires(Feature.Tasks, PermissionLevel.ViewOnly);
    }

    /// <summary>Create a task on a case.</summary>
    /// <remarks>An assignee must be an active user of the caller's firm.</remarks>
    internal static async Task<Created<TaskView>> CreateAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var draft = CaseTaskRules.ParseCreation(await context.BodyAsync(MaxRequestBytes));
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        await ValidateAssigneeAsync(context, accessor, draft.AssigneeSubject, cancellationToken);
        var task = CaseTaskRules.Create(draft, caseId, accessor.Subject, context.Clock());
        await context.Port<ICaseTaskStore>().CreateAsync(task, cancellationToken);
        context.Log(Logger, "task created", ("case_id", caseId), ("id", task.Id));
        return TypedResults.Created((string?)null, TaskView.Of(task, context.Today()));
    }

    /// <summary>A case's tasks.</summary>
    internal static async Task<TaskList> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var today = context.Today();
        var tasks = await context.Port<ICaseTaskStore>().ListForCaseAsync(caseId, cancellationToken);
        return new TaskList([.. tasks.Select(task => TaskView.Of(task, today))]);
    }

    /// <summary>One task.</summary>
    internal static async Task<TaskView> GetAsync(HttpContext context, string caseId, string taskId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var task = await context.Port<ICaseTaskStore>().GetAsync(caseId, taskId, cancellationToken) ?? throw new NotFoundException(TaskNotFound);
        return TaskView.Of(task, context.Today());
    }

    /// <summary>Change a task.</summary>
    /// <remarks>Only the members given change. A new assignee must be an active user of the caller's firm.</remarks>
    internal static async Task<TaskView> UpdateAsync(HttpContext context, string caseId, string taskId, CancellationToken cancellationToken)
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
        return TaskView.Of(written, context.Today());
    }

    /// <summary>Delete a task.</summary>
    internal static async Task<NoContent> DeleteAsync(HttpContext context, string caseId, string taskId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        if (!await context.Port<ICaseTaskStore>().DeleteAsync(caseId, taskId, cancellationToken))
        {
            throw new NotFoundException(TaskNotFound);
        }

        context.Log(Logger, "task deleted", ("case_id", caseId), ("id", taskId));
        return TypedResults.NoContent();
    }

    /// <summary>The caller's tasks.</summary>
    /// <remarks>
    /// Across every case the caller may see — or, with <c>?scope=firm</c>,
    /// everyone's. Ordered by due date (undated last), then creation, then id.
    /// </remarks>
    internal static async Task<TaskList> ListMineAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var scope = CaseRoutes.Query(context, "scope") is { Length: > 0 } given ? given : "mine";
        if (scope is not ("mine" or "firm"))
        {
            throw new ValidationException("scope must be 'mine' or 'firm'");
        }

        var store = context.Port<ICaseTaskStore>();
        var reachable = await ReachableCaseIdsAsync(context.Cases(), accessor, cancellationToken);
        var tasks = (await store.ListForCasesAsync(reachable, cancellationToken))
            .Where(task => scope == "firm" || task.AssigneeSubject == accessor.Subject);

        var today = context.Today();
        var ordered = tasks
            .OrderBy(task => task.DueDate is null)
            .ThenBy(task => task.DueDate)
            .ThenBy(task => task.CreatedAt)
            .ThenBy(task => task.Id, StringComparer.Ordinal);
        return new TaskList([.. ordered.Select(task => TaskView.Of(task, today))]);
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
