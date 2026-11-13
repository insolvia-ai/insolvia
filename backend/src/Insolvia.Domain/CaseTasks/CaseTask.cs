using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.CaseTasks;

/// <summary>
/// One task of a case — metadata only, no provenance (issue #356 / 14.4; from
/// <c>tasks.py</c>, where it is <c>Task</c>). <c>overdue</c> is NOT a field:
/// derived values are computed, never stored (<see cref="CaseTaskRules.IsOverdue"/>).
/// <see cref="CreatedBy"/> is an audit fact that grants nothing.
/// </summary>
/// <param name="Id">The server-minted uuid.</param>
/// <param name="CaseId">The case.</param>
/// <param name="Subject">The title — required.</param>
/// <param name="Description">The detail.</param>
/// <param name="DueDate">When it is due — a calendar date.</param>
/// <param name="AssigneeSubject">The firm user it is assigned to.</param>
/// <param name="FormSeries">The short form key it is anchored to (<c>b106d</c>).</param>
/// <param name="Done">Whether it is done.</param>
/// <param name="CompletedAt">When it was marked done — follows <paramref name="Done"/>.</param>
/// <param name="CreatedAt">When it was created — the list order.</param>
/// <param name="UpdatedAt">When it was last written.</param>
/// <param name="CreatedBy">Who created it.</param>
public sealed record CaseTask(
    string Id,
    string CaseId,
    string Subject,
    string? Description,
    DateOnly? DueDate,
    string? AssigneeSubject,
    string? FormSeries,
    bool Done,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CreatedBy);

/// <summary>
/// A new task (<c>POST /v1/cases/{caseId}/tasks</c>). Only <see cref="Subject"/>
/// is required; <c>done</c> is never accepted here — every task starts not-done.
/// </summary>
/// <param name="Subject">The title — required.</param>
/// <param name="Description">The detail.</param>
/// <param name="DueDate">When it is due.</param>
/// <param name="AssigneeSubject">An active user of the caller's firm.</param>
/// <param name="FormSeries">The short form key to anchor it to (<c>b106d</c>).</param>
public sealed record CaseTaskDraft(
    [property: Required(ErrorMessage = CaseTaskRules.SubjectRequired), Text(CaseTaskRules.MaxSubject)] string? Subject,
    [property: Narrative(CaseTaskRules.MaxDescription)] string? Description = null,
    DateOnly? DueDate = null,
    [property: RegularExpression(CaseTaskRules.SubjectPattern, ErrorMessage = CaseTaskRules.AssigneeMessage)] string? AssigneeSubject = null,
    [property: RegularExpression(CaseTaskRules.FormSeriesPattern, ErrorMessage = CaseTaskRules.FormSeriesMessage)] string? FormSeries = null);

/// <summary>
/// Changing a task (<c>PATCH …/tasks/{taskId}</c>): only the keys sent change,
/// and a body with none is a 400. A nullable member sent as null clears it
/// (<see cref="Names"/> tells that apart from a key not sent);
/// <see cref="Subject"/> can be replaced, never cleared.
/// </summary>
public sealed record CaseTaskChanges : IValidatableObject
{
    private readonly ImmutableHashSet<string> _named = [];

    /// <summary>A new title.</summary>
    [Text(CaseTaskRules.MaxSubject)]
    public string? Subject { get; init { field = value; _named = _named.Add(nameof(Subject)); } }

    /// <summary>A new description, or null to clear.</summary>
    [Narrative(CaseTaskRules.MaxDescription)]
    public string? Description { get; init { field = value; _named = _named.Add(nameof(Description)); } }

    /// <summary>A new due date, or null to clear.</summary>
    public DateOnly? DueDate { get; init { field = value; _named = _named.Add(nameof(DueDate)); } }

    /// <summary>A new assignee, or null to unassign.</summary>
    [RegularExpression(CaseTaskRules.SubjectPattern, ErrorMessage = CaseTaskRules.AssigneeMessage)]
    public string? AssigneeSubject { get; init { field = value; _named = _named.Add(nameof(AssigneeSubject)); } }

    /// <summary>A new form anchor, or null to clear.</summary>
    [RegularExpression(CaseTaskRules.FormSeriesPattern, ErrorMessage = CaseTaskRules.FormSeriesMessage)]
    public string? FormSeries { get; init { field = value; _named = _named.Add(nameof(FormSeries)); } }

    /// <summary>Completion — a state transition <c>completedAt</c> follows.</summary>
    public bool? Done { get; init; }

    /// <summary>Whether nothing would change.</summary>
    [JsonIgnore]
    public bool IsEmpty => _named.IsEmpty && Done is null;

    /// <summary>Whether the request named <paramref name="member"/> — a clearable member set to null clears it.</summary>
    /// <param name="member">The C# member's name.</param>
    /// <returns>Whether it was sent.</returns>
    public bool Names(string member) => _named.Contains(member);

    /// <summary>A title sent must be one.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Names(nameof(Subject)) && string.IsNullOrWhiteSpace(Subject))
        {
            yield return new ValidationResult(CaseTaskRules.SubjectRequired, [nameof(Subject)]);
        }
    }
}

/// <summary>The task rules.</summary>
public static class CaseTaskRules
{
    /// <summary>The cap on a title.</summary>
    public const int MaxSubject = 200;

    /// <summary>The cap on a description.</summary>
    public const int MaxDescription = 2000;

    /// <summary>What a missing title is told.</summary>
    public const string SubjectRequired = "A subject is required.";

    /// <summary>A firm user's subject: a lower-case uuid.</summary>
    public const string SubjectPattern = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

    /// <summary>What an assignee that is not a subject is told.</summary>
    public const string AssigneeMessage = "Must be a firm user's subject.";

    /// <summary>A short form key.</summary>
    public const string FormSeriesPattern = "(?i)[a-z][a-z0-9]{1,31}";

    /// <summary>What a malformed form key is told.</summary>
    public const string FormSeriesMessage = "Must be a form key, like \"b106d\" — lowercase letters and digits.";

    /// <summary>Stamp a draft with server-generated identity; every task starts not-done.</summary>
    public static CaseTask Create(CaseTaskDraft draft, string caseId, string createdBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.Now(clock);
        return new CaseTask(Guid.NewGuid().ToString(), caseId, draft.Subject!, draft.Description, draft.DueDate, draft.AssigneeSubject, draft.FormSeries?.ToLowerInvariant(), false, null, now, now, createdBy);
    }

    /// <summary>
    /// The task with <paramref name="changes"/> applied. <c>completed_at</c> is
    /// never accepted from a caller — it FOLLOWS <c>done</c>: on stamps it, off
    /// clears it, unchanged leaves it alone.
    /// </summary>
    public static CaseTask ApplyChanges(CaseTask task, CaseTaskChanges changes, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(changes);
        var updated = task with
        {
            Subject = changes.Subject ?? task.Subject,
            Description = changes.Names(nameof(CaseTaskChanges.Description)) ? changes.Description : task.Description,
            DueDate = changes.Names(nameof(CaseTaskChanges.DueDate)) ? changes.DueDate : task.DueDate,
            AssigneeSubject = changes.Names(nameof(CaseTaskChanges.AssigneeSubject)) ? changes.AssigneeSubject : task.AssigneeSubject,
            FormSeries = changes.Names(nameof(CaseTaskChanges.FormSeries)) ? changes.FormSeries?.ToLowerInvariant() : task.FormSeries,
        };
        if (changes.Done is { } done && done != task.Done)
        {
            updated = updated with { Done = done, CompletedAt = done ? Clock.Now(clock) : null };
        }

        return updated with { UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>
    /// Whether the task is late, computed rather than stored. A done task is
    /// never overdue; a task with no due date has nothing to be overdue against.
    /// </summary>
    public static bool IsOverdue(CaseTask task, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(task);
        return !task.Done && task.DueDate is { } due && due < today;
    }

    /// <summary>Creation order, id as the tiebreak — the sort key is a random uuid, so both stores sort by this.</summary>
    public static IComparer<CaseTask> ListOrder { get; } = Comparer<CaseTask>.Create((left, right) =>
    {
        var byTime = left.CreatedAt.CompareTo(right.CreatedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(left.Id, right.Id);
    });
}
