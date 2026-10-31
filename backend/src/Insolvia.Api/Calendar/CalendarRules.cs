using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Core.Events;
using Insolvia.Core.Fields;

namespace Insolvia.Api.Calendar;

/// <summary>A validated whole-record event save — POST to create, PUT to replace.</summary>
/// <param name="Title">The title.</param>
/// <param name="Description">Free text.</param>
/// <param name="Start">A form date or a normalised instant.</param>
/// <param name="End">The same kind as <paramref name="Start"/>.</param>
/// <param name="AllDay">Whether whole days.</param>
/// <param name="Location">Where.</param>
/// <param name="Attendees">Firm-user subjects, de-duplicated, in order.</param>
public sealed record EventDraft(string Title, string? Description, string Start, string End, bool AllDay, string? Location, ValueCollection<string> Attendees);

/// <summary>
/// Calendar events' validation and identity (services/api core/events.py; issue
/// 14.6 / #358). A HAND-MADE event is written whole and removed; a GENERATED
/// one (a deadline) is the engine's — nobody edits it, anyone who may edit
/// events may dismiss it, and the dismissal survives regeneration.
/// </summary>
public static partial class CalendarRules
{
    /// <summary>The longest title.</summary>
    public const int MaxTitle = 200;

    /// <summary>The longest location.</summary>
    public const int MaxLocation = 200;

    /// <summary>The longest description.</summary>
    public const int MaxDescription = 2000;

    /// <summary>The most attendees.</summary>
    public const int MaxAttendees = 50;

    /// <summary>
    /// Validate POST/PUT of an event; unknown keys ignored. <c>title</c> and
    /// <c>start</c> are required; <c>end</c> defaults to <c>start</c> (all-day)
    /// or an hour later (timed); <c>all_day</c> defaults to whether <c>start</c>
    /// reads as a bare date. A span over <see cref="CalendarEvents.MaxEventSpanDays"/> is refused.
    /// </summary>
    public static EventDraft Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var title = FieldParsers.Text(payload["title"], "title", errors, MaxTitle);
        if (title is null && !errors.ContainsKey("title"))
        {
            errors["title"] = "A title is required.";
        }

        var description = FieldParsers.Narrative(payload["description"], "description", errors, MaxDescription);
        var location = FieldParsers.Text(payload["location"], "location", errors, MaxLocation);
        var attendees = ParseAttendees(payload["attendees"], errors);

        var rawStart = payload["start"];
        bool allDay;
        if (payload["all_day"] is null)
        {
            allDay = Json.AsString(rawStart) is { } text && TextRules.Strip(text).Length == 10;
        }
        else if (Json.AsBool(payload["all_day"]) is { } given)
        {
            allDay = given;
        }
        else
        {
            errors["all_day"] = "Must be true or false.";
            allDay = false;
        }

        string? start;
        string? end;
        var spanMessage = $"An event may span at most {CalendarEvents.MaxEventSpanDays} days.";
        if (allDay)
        {
            var startDay = FieldParsers.FormDate(rawStart, "start", errors);
            var endDay = FieldParsers.FormDate(payload["end"], "end", errors);
            if (startDay is null && !errors.ContainsKey("start"))
            {
                errors["start"] = "A start date is required.";
            }

            if (startDay is not null && endDay is null && !errors.ContainsKey("end"))
            {
                endDay = startDay;
            }

            if (startDay is not null && endDay is not null)
            {
                if (endDay < startDay)
                {
                    errors["end"] = "The end cannot precede the start.";
                }
                else if (endDay.Value.DayNumber - startDay.Value.DayNumber > CalendarEvents.MaxEventSpanDays)
                {
                    errors["end"] = spanMessage;
                }
            }

            start = startDay is { } s ? CalendarEvents.FormatDay(s) : null;
            end = endDay is { } e ? CalendarEvents.FormatDay(e) : null;
        }
        else
        {
            var startAt = ParseInstant(rawStart, "start", errors);
            var endAt = ParseInstant(payload["end"], "end", errors);
            if (startAt is null && !errors.ContainsKey("start"))
            {
                errors["start"] = "A start time is required.";
            }

            if (startAt is not null && endAt is null && !errors.ContainsKey("end"))
            {
                endAt = startAt.Value.AddHours(1);
            }

            if (startAt is not null && endAt is not null)
            {
                if (endAt < startAt)
                {
                    errors["end"] = "The end cannot precede the start.";
                }
                else if (endAt - startAt > TimeSpan.FromDays(CalendarEvents.MaxEventSpanDays))
                {
                    errors["end"] = spanMessage;
                }
            }

            start = startAt is { } s ? CalendarEvents.FormatInstant(s) : null;
            end = endAt is { } e ? CalendarEvents.FormatInstant(e) : null;
        }

        if (errors.Count > 0 || title is null || start is null || end is null)
        {
            throw new FieldValidationException(errors);
        }

        return new EventDraft(title, description, start, end, allDay, location, attendees);
    }

    /// <summary>PATCH's one field, alone — a client that meant PUT finds out rather than losing its edits.</summary>
    public static bool ParseDismissal(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Count == 1 && payload.ContainsKey("dismissed") && Json.AsBool(payload["dismissed"]) is { } dismissed
            ? dismissed
            : throw new FieldValidationException(new Dictionary<string, string> { ["dismissed"] = "Must be true or false, and alone." });
    }

    /// <summary>A new hand-made event.</summary>
    public static CalendarEvent Create(EventDraft draft, EventScope scope, string createdBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(scope);
        var now = Clock.Now(clock);
        return Apply(new CalendarEvent(Guid.NewGuid().ToString(), scope.FirmId, scope.CaseId, draft.Title, draft.Start, draft.End, draft.AllDay, createdBy, now, now), draft);
    }

    /// <summary>A hand-made event with the draft applied — id, scope, author and created_at kept. A generated one is refused: its dates are the rule's.</summary>
    public static CalendarEvent Replace(CalendarEvent existing, EventDraft draft, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(draft);
        if (existing.Generated)
        {
            throw new ValidationException("a generated deadline cannot be edited — dismiss it, or change the case's dates and it is regenerated");
        }

        return Apply(existing, draft) with { UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>The event dismissed or restored.</summary>
    public static CalendarEvent SetDismissed(CalendarEvent existing, bool dismissed, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return existing with { Dismissed = dismissed, UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>Whether <paramref name="value"/> is a firm-user subject (a lower-case uuid).</summary>
    public static bool ValidSubject(string value) => Subject().IsMatch(value);

    private static CalendarEvent Apply(CalendarEvent target, EventDraft draft) => target with
    {
        Title = draft.Title,
        Description = draft.Description,
        Start = draft.Start,
        End = draft.End,
        AllDay = draft.AllDay,
        Location = draft.Location,
        Attendees = draft.Attendees,
    };

    private static ValueCollection<string> ParseAttendees(JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return [];
        }

        if (value is not JsonArray list)
        {
            errors["attendees"] = "Must be a list of firm-user subjects.";
            return [];
        }

        if (list.Count > MaxAttendees)
        {
            errors["attendees"] = $"At most {MaxAttendees} attendees.";
            return [];
        }

        var seen = new List<string>();
        for (var index = 0; index < list.Count; index++)
        {
            var raw = Json.AsString(list[index]);
            if (raw is null || !ValidSubject(raw))
            {
                errors[$"attendees[{index}]"] = "Must be a firm-user subject.";
                continue;
            }

            if (seen.Contains(raw))
            {
                errors[$"attendees[{index}]"] = "Duplicate attendee.";
                continue;
            }

            seen.Add(raw);
        }

        return seen.ToValueCollection();
    }

    // Python's datetime.fromisoformat(value.replace("Z", "+00:00")): an
    // offset (or Z) is required, the result normalised to UTC and truncated
    // to the second (strftime("%Y-%m-%dT%H:%M:%SZ")).
    private static DateTimeOffset? ParseInstant(JsonNode? value, string path, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return null;
        }

        var text = Json.AsString(value);
        if (text is null || TextRules.Strip(text).Length == 0)
        {
            errors[path] = "Must be a date-time.";
            return null;
        }

        var match = Instant().Match(TextRules.Strip(text));
        if (!match.Success
            || !DateTimeOffset.TryParse(
                match.Groups["offset"].Success ? match.Value.Replace(' ', 'T') : match.Value.Replace(' ', 'T') + "Z",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            errors[path] = "Must be an RFC 3339 date-time, like 2026-03-02T15:00:00Z.";
            return null;
        }

        if (!match.Groups["offset"].Success)
        {
            errors[path] = "Must carry a time zone offset (or Z).";
            return null;
        }

        var utc = parsed.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d{1,6})?)?(?<offset>Z|[+-]\d{2}:?\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Instant();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Subject();
}

/// <summary>
/// The calendar feed (services/api core/calendar_feed.py): a per-person
/// secret whose hash alone is stored, the <c>subject.secret</c> token, and the
/// RFC 5545 rendering — dismissed events left out, deadlines categorised,
/// lines folded at 75 characters.
/// </summary>
public static class CalendarFeed
{
    private const int TokenBytes = 32;

    /// <summary>A fresh secret — Python's <c>secrets.token_urlsafe(32)</c>.</summary>
    public static string MintSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The stored form: lower-case hex SHA-256.</summary>
    public static string HashSecret(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>The feed token: <c>&lt;subject&gt;.&lt;secret&gt;</c>.</summary>
    public static string FeedToken(string subject, string secret) => $"{subject}.{secret}";

    /// <summary>The token's two halves, or null when it is not one (no dot, no secret, or no subject).</summary>
    public static (string Subject, string Secret)? Split(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return null;
        }

        var subject = token[..dot];
        var secret = token[(dot + 1)..];
        return secret.Length == 0 || !CalendarRules.ValidSubject(subject) ? null : (subject, secret);
    }

    /// <summary>Whether <paramref name="secret"/> is the one <paramref name="stored"/> was minted from — compared in constant time.</summary>
    public static bool SecretMatches(CalendarToken stored, string secret)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored.SecretHash), Encoding.ASCII.GetBytes(HashSecret(secret)));
    }

    /// <summary>The ICS body, CRLF line ends, dismissed events left out.</summary>
    public static string RenderIcs(IEnumerable<CalendarEvent> events, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Insolvia//Case calendar//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "X-WR-CALNAME:Insolvia",
        };
        foreach (var calendarEvent in events.Where(candidate => !candidate.Dismissed))
        {
            lines.AddRange(VEvent(calendarEvent, stamp));
        }

        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.SelectMany(Fold)) + "\r\n";
    }

    private static List<string> VEvent(CalendarEvent calendarEvent, string stamp)
    {
        var lines = new List<string> { "BEGIN:VEVENT", $"UID:{calendarEvent.Id}@insolvia", $"DTSTAMP:{stamp}" };
        if (calendarEvent.AllDay)
        {
            var exclusiveEnd = CalendarEvents.ParseDay(calendarEvent.End).AddDays(1);
            lines.Add($"DTSTART;VALUE=DATE:{calendarEvent.Start.Replace("-", string.Empty, StringComparison.Ordinal)}");
            lines.Add($"DTEND;VALUE=DATE:{CalendarEvents.FormatDay(exclusiveEnd).Replace("-", string.Empty, StringComparison.Ordinal)}");
        }
        else
        {
            lines.Add($"DTSTART:{Compact(calendarEvent.Start)}");
            lines.Add($"DTEND:{Compact(calendarEvent.End)}");
        }

        lines.Add($"SUMMARY:{Escape(calendarEvent.Title)}");
        var description = new[] { calendarEvent.RuleCitation, calendarEvent.Description }.Where(part => part is not null).ToList();
        if (description.Count > 0)
        {
            lines.Add($"DESCRIPTION:{Escape(string.Join('\n', description))}");
        }

        if (calendarEvent.Location is not null)
        {
            lines.Add($"LOCATION:{Escape(calendarEvent.Location)}");
        }

        if (calendarEvent.RuleId is not null)
        {
            lines.Add("CATEGORIES:Deadline");
        }

        lines.Add("END:VEVENT");
        return lines;
    }

    private static string Compact(string instant) =>
        instant.Replace("-", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal);

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    // RFC 5545 §3.1 folding, as the contract fixes it: 75 characters (code points),
    // then continuation lines of a space and 74.
    private static IEnumerable<string> Fold(string line)
    {
        var runes = line.EnumerateRunes().Select(rune => rune.ToString()).ToList();
        if (runes.Count <= 75)
        {
            yield return line;
            yield break;
        }

        yield return string.Concat(runes.Take(75));
        for (var offset = 75; offset < runes.Count; offset += 74)
        {
            yield return " " + string.Concat(runes.Skip(offset).Take(74));
        }
    }
}
