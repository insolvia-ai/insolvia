using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core.Fields;

namespace Insolvia.Core.CaseTasks;

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

/// <summary>A validated creation request, before server-generated identity.</summary>
public sealed record CaseTaskDraft(string Subject, string? Description, DateOnly? DueDate, string? AssigneeSubject, string? FormSeries);

/// <summary>
/// A validated PATCH body. Unset means "leave unchanged"; set-to-null on a
/// nullable member means "clear it". <see cref="Subject"/> can be replaced,
/// never cleared.
/// </summary>
public sealed record CaseTaskChanges
{
    /// <summary>A new title.</summary>
    public PatchValue<string> Subject { get; init; }

    /// <summary>A new description, or null to clear.</summary>
    public PatchValue<string?> Description { get; init; }

    /// <summary>A new due date, or null to clear.</summary>
    public PatchValue<DateOnly?> DueDate { get; init; }

    /// <summary>A new assignee, or null to unassign.</summary>
    public PatchValue<string?> AssigneeSubject { get; init; }

    /// <summary>A new form anchor, or null to clear.</summary>
    public PatchValue<string?> FormSeries { get; init; }

    /// <summary>Completion — a state transition <c>completed_at</c> follows.</summary>
    public PatchValue<bool> Done { get; init; }
}

/// <summary>The task rules.</summary>
public static partial class CaseTaskRules
{
    /// <summary>The cap on a title.</summary>
    public const int MaxSubject = 200;

    /// <summary>The cap on a description.</summary>
    public const int MaxDescription = 2000;

    /// <summary>
    /// Validate POST /v1/cases/&lt;case_id&gt;/tasks; unknown keys are ignored.
    /// Only <c>subject</c> is required; <c>done</c> is never accepted here.
    /// </summary>
    public static CaseTaskDraft ParseCreation(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var subject = ParseSubject(payload["subject"], errors);
        var description = FieldParsers.Narrative(payload["description"], "description", errors, limit: MaxDescription);
        var dueDate = FieldParsers.FormDate(payload["dueDate"], "dueDate", errors);
        var assignee = ParseAssigneeSubject(payload["assigneeSubject"], errors);
        var formSeries = ParseFormSeries(payload["formSeries"], errors);
        if (errors.Count > 0 || subject is null)
        {
            throw new FieldValidationException(errors);
        }

        return new CaseTaskDraft(subject, description, dueDate, assignee, formSeries);
    }

    /// <summary>
    /// Validate PATCH …/tasks/&lt;task_id&gt;. A key's ABSENCE leaves its member
    /// unchanged; a JSON null on a nullable member clears it.
    /// </summary>
    public static CaseTaskChanges ParseUpdate(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var changes = new CaseTaskChanges();
        var any = false;

        if (payload.ContainsKey("subject") && ParseSubject(payload["subject"], errors) is { } subject)
        {
            changes = changes with { Subject = PatchValue.Of(subject) };
            any = true;
        }

        if (payload.TryGetPropertyValue("description", out var description))
        {
            changes = changes with
            {
                Description = PatchValue.Of(description is null ? null : FieldParsers.Narrative(description, "description", errors, limit: MaxDescription)),
            };
            any = true;
        }

        if (payload.TryGetPropertyValue("dueDate", out var dueDate))
        {
            changes = changes with { DueDate = PatchValue.Of(dueDate is null ? null : FieldParsers.FormDate(dueDate, "dueDate", errors)) };
            any = true;
        }

        if (payload.TryGetPropertyValue("assigneeSubject", out var assignee))
        {
            changes = changes with { AssigneeSubject = PatchValue.Of(assignee is null ? null : ParseAssigneeSubject(assignee, errors)) };
            any = true;
        }

        if (payload.TryGetPropertyValue("formSeries", out var formSeries))
        {
            changes = changes with { FormSeries = PatchValue.Of(formSeries is null ? null : ParseFormSeries(formSeries, errors)) };
            any = true;
        }

        if (payload.TryGetPropertyValue("done", out var done))
        {
            if (Json.AsBool(done) is { } flag)
            {
                changes = changes with { Done = PatchValue.Of(flag) };
                any = true;
            }
            else
            {
                errors["done"] = "Must be true or false.";
            }
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return any ? changes : throw new ValidationException("no supported fields to update");
    }

    /// <summary>Stamp a draft with server-generated identity; every task starts not-done.</summary>
    public static CaseTask Create(CaseTaskDraft draft, string caseId, string createdBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.Now(clock);
        return new CaseTask(Guid.NewGuid().ToString(), caseId, draft.Subject, draft.Description, draft.DueDate, draft.AssigneeSubject, draft.FormSeries, false, null, now, now, createdBy);
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
        var updated = task;
        if (changes.Subject.IsSet)
        {
            updated = updated with { Subject = changes.Subject.Value };
        }

        if (changes.Description.IsSet)
        {
            updated = updated with { Description = changes.Description.Value };
        }

        if (changes.DueDate.IsSet)
        {
            updated = updated with { DueDate = changes.DueDate.Value };
        }

        if (changes.AssigneeSubject.IsSet)
        {
            updated = updated with { AssigneeSubject = changes.AssigneeSubject.Value };
        }

        if (changes.FormSeries.IsSet)
        {
            updated = updated with { FormSeries = changes.FormSeries.Value };
        }

        if (changes.Done.IsSet && changes.Done.Value != task.Done)
        {
            updated = updated with { Done = changes.Done.Value, CompletedAt = changes.Done.Value ? Clock.Now(clock) : null };
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

    private static string? ParseSubject(JsonNode? value, Dictionary<string, string> errors)
    {
        var raw = Json.AsString(value);
        if (raw is null || TextRules.Strip(raw).Length == 0)
        {
            errors["subject"] = "A subject is required.";
            return null;
        }

        var subject = TextRules.Strip(raw);
        if (TextRules.Length(subject) > MaxSubject)
        {
            errors["subject"] = $"Must be {MaxSubject} characters or fewer.";
            return null;
        }

        if (subject.Contains('\n', StringComparison.Ordinal) || subject.Contains('\r', StringComparison.Ordinal))
        {
            errors["subject"] = "Must be a single line.";
            return null;
        }

        return subject;
    }

    private static string? ParseAssigneeSubject(JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return null;
        }

        var raw = Json.AsString(value);
        if (raw is null || !SubjectPattern().IsMatch(TextRules.Strip(raw)))
        {
            errors["assigneeSubject"] = "Must be a firm user's subject.";
            return null;
        }

        return TextRules.Strip(raw);
    }

    private static string? ParseFormSeries(JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return null;
        }

        var raw = Json.AsString(value);
        if (raw is null || !FormSeriesPattern().IsMatch(TextRules.Lower(TextRules.Strip(raw))))
        {
            errors["formSeries"] = "Must be a form key, like \"b106d\" — lowercase letters and digits.";
            return null;
        }

        return TextRules.Lower(TextRules.Strip(raw));
    }

    [GeneratedRegex(@"^[a-z][a-z0-9]{1,31}\z")]
    private static partial Regex FormSeriesPattern();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex SubjectPattern();
}
