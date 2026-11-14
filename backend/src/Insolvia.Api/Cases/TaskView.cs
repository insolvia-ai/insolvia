using System.Text.Json.Serialization;
using Insolvia.Domain.Cases;
using Insolvia.Domain.CaseTasks;

namespace Insolvia.Api.Cases;

/// <summary>A case task, with whether it is overdue judged against one UTC day for the whole response.</summary>
/// <param name="Id">The task's id.</param>
/// <param name="CaseId">The case.</param>
/// <param name="Subject">The title.</param>
/// <param name="Done">Whether it is done.</param>
/// <param name="Overdue">Derived: not done and due before today (UTC).</param>
/// <param name="CreatedAt">When it was created, ISO 8601 UTC.</param>
/// <param name="UpdatedAt">When it was last written, ISO 8601 UTC.</param>
/// <param name="CreatedBy">Who created it, by subject. An audit fact; it grants nothing.</param>
/// <param name="Description">The detail; absent when none.</param>
/// <param name="DueDate">When it is due, <c>YYYY-MM-DD</c>; absent when undated.</param>
/// <param name="AssigneeSubject">The firm user it is assigned to; absent when unassigned.</param>
/// <param name="FormSeries">The short form key it is anchored to (<c>b106d</c>); absent when none.</param>
/// <param name="CompletedAt">When it was marked done, ISO 8601 UTC; absent while not done.</param>
public sealed record TaskView(
    string Id,
    string CaseId,
    string Subject,
    bool Done,
    bool Overdue,
    string CreatedAt,
    string UpdatedAt,
    string CreatedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DueDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AssigneeSubject,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FormSeries,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CompletedAt)
{
    /// <summary>The view of <paramref name="task"/>.</summary>
    /// <param name="task">The stored task.</param>
    /// <param name="today">The day every task in the response is judged against.</param>
    /// <returns>The view.</returns>
    public static TaskView Of(CaseTask task, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new(
            task.Id,
            task.CaseId,
            task.Subject,
            task.Done,
            CaseTaskRules.IsOverdue(task, today),
            CaseRules.Iso(task.CreatedAt),
            CaseRules.Iso(task.UpdatedAt),
            task.CreatedBy,
            task.Description,
            task.DueDate is { } due ? WireFormat.Date(due) : null,
            task.AssigneeSubject,
            task.FormSeries,
            task.CompletedAt is { } completed ? CaseRules.Iso(completed) : null);
    }
}

/// <summary>Tasks, in the order the route states.</summary>
/// <param name="Tasks">The tasks.</param>
public sealed record TaskList(IReadOnlyList<TaskView> Tasks);
