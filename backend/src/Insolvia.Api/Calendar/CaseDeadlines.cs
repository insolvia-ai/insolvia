using Insolvia.Api.Routes;
using Insolvia.Core;
using Insolvia.Core.Cases;
using Insolvia.Core.Events;
using Insolvia.Core.Ports;
using Insolvia.Engines.Deadlines;

namespace Insolvia.Api.Calendar;

/// <summary>The writes that make the store agree with the engine's answer.</summary>
/// <param name="Create">New generated events.</param>
/// <param name="Put">Generated events whose dates or text changed.</param>
/// <param name="Delete">Generated events whose rule no longer applies.</param>
public sealed record ReconcilePlan(IReadOnlyList<CalendarEvent> Create, IReadOnlyList<CalendarEvent> Put, IReadOnlyList<CalendarEvent> Delete);

/// <summary>
/// The deadline engine's store half (core/deadlines.py <c>reconcile</c> and
/// routes/events.py <c>regenerate_deadlines</c>; issue 14.6 / #358): the
/// generated deadlines (<see cref="DeadlineEngine.Generate"/>) diffed against
/// the case's stored events, keyed by rule. A kept event keeps its id, its
/// dismissal and its author; hand-made events are never touched.
/// </summary>
public static class CaseDeadlines
{
    /// <summary>
    /// Keyed by <c>rule_id</c>: a stored event for a rule that still applies
    /// is rewritten only when its dates or text changed; one for a rule that
    /// no longer applies is deleted; a rule with no stored event is created
    /// for <paramref name="attendees"/> (the case's assignees now).
    /// </summary>
    public static ReconcilePlan Reconcile(Case target, IReadOnlyList<GeneratedDeadline> generated, IReadOnlyList<CalendarEvent> existing, IReadOnlyList<string> attendees, string actor, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(existing);
        var stored = existing.Where(found => found.RuleId is not null).ToDictionary(found => found.RuleId!, StringComparer.Ordinal);
        var create = new List<CalendarEvent>();
        var put = new List<CalendarEvent>();
        foreach (var deadline in generated)
        {
            var rule = deadline.Rule;
            var draft = new EventDraft(deadline.Title, deadline.Description(), CalendarEvents.FormatDay(deadline.Start), CalendarEvents.FormatDay(deadline.End), AllDay: true, Location: null, attendees.ToValueCollection());
            if (!stored.Remove(rule.RuleId, out var current))
            {
                create.Add(CalendarRules.Create(draft, new EventScope(target.FirmId, target.Id), actor, clock) with { RuleId = rule.RuleId, RuleCitation = rule.Citation });
                continue;
            }

            if (current.Start != draft.Start || current.End != draft.End || current.Title != draft.Title || current.Description != draft.Description || current.RuleCitation != rule.Citation)
            {
                put.Add(current with
                {
                    Title = draft.Title,
                    Description = draft.Description,
                    Start = draft.Start,
                    End = draft.End,
                    AllDay = true,
                    RuleCitation = rule.Citation,
                    UpdatedAt = Clock.Now(clock),
                });
            }
        }

        return new ReconcilePlan(create, put, [.. stored.Values]);
    }

    /// <summary>
    /// Rewrite <paramref name="target"/>'s generated events for its anchors
    /// now — the case PATCH's hook when a date or the chapter changed. After
    /// the case write, so a failure leaves the case right and the calendar a
    /// request behind. Answers the number of writes.
    /// </summary>
    public static async Task<int> RegenerateAsync(HttpContext context, Case target, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        var store = context.Port<ICalendarEventStore>();
        var scope = new EventScope(target.FirmId, target.Id);
        var existing = await store.ListForScopeAsync(scope, cancellationToken);
        IReadOnlyList<GeneratedDeadline> generated = target.FiledAt is { } filed
            ? DeadlineEngine.Generate(target, DeadlineRules.Resolve(filed), LegalHolidays.Resolve(filed))
            : [];
        var attendees = (await context.Cases().AssigneesAsync(target.Id, cancellationToken)).Select(assignment => assignment.Subject).ToList();
        var plan = Reconcile(target, generated, existing, attendees, actor, context.Clock());
        foreach (var created in plan.Create)
        {
            await store.CreateAsync(created, cancellationToken);
        }

        foreach (var changed in plan.Put)
        {
            await store.PutAsync(changed, cancellationToken);
        }

        foreach (var gone in plan.Delete)
        {
            await store.DeleteAsync(scope, gone.Id, cancellationToken);
        }

        var written = plan.Create.Count + plan.Put.Count + plan.Delete.Count;
        if (written > 0)
        {
            context.Log("insolvia_api.api.routes.events", "case deadlines regenerated", ("case_id", target.Id), ("writes", written));
        }

        return written;
    }
}
