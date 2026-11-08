using Insolvia.Core.Cases;

namespace Insolvia.Engines.Deadlines;

/// <summary>One rule applied to one case: what the calendar event will say.</summary>
public sealed record GeneratedDeadline(DeadlineRule Rule, DateOnly Start, DateOnly End)
{
    /// <summary>The event's title — the rule's.</summary>
    public string Title => Rule.Title;

    /// <summary>The rule's description, with the 9006(a)(6)(B)/(C) caveat on a counted deadline.</summary>
    public string Description()
    {
        var parts = new List<string> { Rule.Description };
        if (Rule.Computation == "rule_9006a")
        {
            parts.Add(DeadlineEngine.HolidayCaveat);
        }

        return string.Join("\n\n", parts);
    }
}

/// <summary>
/// The deadline engine's pure half (from <c>core/deadlines.py</c>,
/// issue 14.6 / #358): a case's anchors against the rule table give its
/// deadlines. <c>reconcile</c> — the diff against stored calendar events —
/// waits for the events port; it needs the event record, which Core does not
/// carry yet. No filed date, no deadlines: a calendar from a plan is a
/// calendar of dates that will all be wrong.
/// </summary>
public static class DeadlineEngine
{
    /// <summary>Appended to every rolled deadline, so the reader knows what the arithmetic did not check.</summary>
    public const string HolidayCaveat =
        "Counted under Fed. R. Bankr. P. 9006(a)(1) against the federal legal "
        + "holidays in 5 U.S.C. § 6103(a); a day the President, Congress or the "
        + "district's state has declared a holiday (Rule 9006(a)(6)(B) and (C)) is not "
        + "in that table — check the court's calendar.";

    /// <summary>Every deadline the rule table produces for this case today — none without a filed date, none for a rule whose anchor is unset.</summary>
    public static IReadOnlyList<GeneratedDeadline> Generate(Case @case, RulesRelease release, HolidayRelease holidays)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(holidays);
        if (@case.FiledAt is null)
        {
            return [];
        }

        var produced = new List<GeneratedDeadline>();
        foreach (var rule in release.Rules)
        {
            if (!rule.Chapters.Contains(@case.Chapter))
            {
                continue;
            }

            if (Apply(rule, @case, holidays) is { } deadline)
            {
                produced.Add(deadline);
            }
        }

        return produced;
    }

    private static DateOnly? AnchorDate(Case @case, string anchor) => anchor == "petition" ? @case.FiledAt : @case.Meeting341At;

    private static GeneratedDeadline? Apply(DeadlineRule rule, Case @case, HolidayRelease holidays)
    {
        if (AnchorDate(@case, rule.Anchor) is not { } anchor)
        {
            return null;
        }

        if (rule.UntilAnchor is not null && AnchorDate(@case, rule.UntilAnchor) is not null)
        {
            // A window the court's actual date has replaced.
            return null;
        }

        if (rule.Kind == "window")
        {
            return new GeneratedDeadline(rule, anchor.AddDays(rule.WindowStartDays ?? 0), anchor.AddDays(rule.CountDays));
        }

        var last = rule.Computation == "rule_9006a" ? holidays.AddDays(anchor, rule.CountDays) : anchor.AddDays(rule.CountDays);
        if (rule.NotLaterThanAnchor is not null && AnchorDate(@case, rule.NotLaterThanAnchor) is { } cap && cap < last)
        {
            last = cap;
        }

        return new GeneratedDeadline(rule, last, last);
    }
}
