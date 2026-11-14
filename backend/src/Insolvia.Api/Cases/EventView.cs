using System.Text.Json.Serialization;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Events;

namespace Insolvia.Api.Cases;

/// <summary>
/// A calendar event, a case's or the firm's own. <c>allDay</c> decides how
/// <c>start</c> and <c>end</c> read.
/// </summary>
/// <param name="Id">The event's id.</param>
/// <param name="Title">The title.</param>
/// <param name="Start">All-day: <c>YYYY-MM-DD</c>. Otherwise an instant, <c>YYYY-MM-DDTHH:MM:SSZ</c>.</param>
/// <param name="End">As <c>start</c>; an all-day end is inclusive.</param>
/// <param name="AllDay">Whether the span is whole days.</param>
/// <param name="Attendees">The firm users attending, by subject, in the order given.</param>
/// <param name="Generated">Whether the deadline engine made it. A generated deadline cannot be edited or deleted, only dismissed.</param>
/// <param name="Dismissed">Whether someone dismissed it; survives regeneration.</param>
/// <param name="CreatedBy">Who made it: a subject, or the deadline engine's actor.</param>
/// <param name="CreatedAt">When it was made, ISO 8601 UTC.</param>
/// <param name="UpdatedAt">When it was last written, ISO 8601 UTC.</param>
/// <param name="CaseId">The case; absent for a firm event.</param>
/// <param name="Description">Free text; absent when none.</param>
/// <param name="Location">Where; absent when none.</param>
/// <param name="RuleId">The deadline rule that generated it; absent for a hand-made event.</param>
/// <param name="RuleCitation">The rule's citation, with <c>ruleId</c>.</param>
public sealed record EventView(
    string Id,
    string Title,
    string Start,
    string End,
    bool AllDay,
    IReadOnlyList<string> Attendees,
    bool Generated,
    bool Dismissed,
    string CreatedBy,
    string CreatedAt,
    string UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CaseId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RuleId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RuleCitation)
{
    /// <summary>The view of <paramref name="calendarEvent"/>.</summary>
    /// <param name="calendarEvent">The stored event.</param>
    /// <returns>The view.</returns>
    public static EventView Of(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return new(
            calendarEvent.Id,
            calendarEvent.Title,
            calendarEvent.Start,
            calendarEvent.End,
            calendarEvent.AllDay,
            calendarEvent.Attendees,
            calendarEvent.Generated,
            calendarEvent.Dismissed,
            calendarEvent.CreatedBy,
            CaseRules.Iso(calendarEvent.CreatedAt),
            CaseRules.Iso(calendarEvent.UpdatedAt),
            calendarEvent.CaseId,
            calendarEvent.Description,
            calendarEvent.Location,
            calendarEvent.RuleId,
            calendarEvent.RuleCitation);
    }
}

/// <summary>Calendar events.</summary>
/// <param name="Events">The events.</param>
public sealed record EventList(IReadOnlyList<EventView> Events);

/// <summary>The firm's calendar over a window, filtered to the cases the caller may see.</summary>
/// <param name="From">The window's first day, <c>YYYY-MM-DD</c>.</param>
/// <param name="To">The window's last day, <c>YYYY-MM-DD</c>, inclusive.</param>
/// <param name="Events">Every event that overlaps the window.</param>
public sealed record CalendarWindow(string From, string To, IReadOnlyList<EventView> Events);

/// <summary>A new calendar-feed token. It replaces the caller's old one, and is shown only once.</summary>
/// <param name="Token">The token, for <c>?token=</c>.</param>
/// <param name="FeedPath">The feed's path with the token in place, for a calendar client to subscribe to.</param>
public sealed record CalendarFeedToken(string Token, string FeedPath);
