using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Insolvia.Domain;
using Insolvia.Domain.Events;
using Insolvia.Domain.Fields;

namespace Insolvia.Api.Calendar;

/// <summary>
/// An event as written (<c>POST</c> or <c>PUT</c> on <c>…/events</c>), whole.
/// <c>end</c> defaults to <c>start</c> (all-day) or an hour later (timed);
/// <c>allDay</c> defaults to whether <c>start</c> is a bare date. A span over
/// <see cref="CalendarEvents.MaxEventSpanDays"/> days is refused.
/// </summary>
/// <param name="Title">The title — required.</param>
/// <param name="Start">All-day: <c>YYYY-MM-DD</c>. Otherwise an RFC 3339 instant with an offset (or Z).</param>
/// <param name="End">As <c>start</c>; absent means the start day (all-day) or an hour after the start.</param>
/// <param name="AllDay">Whether the span is whole days; absent means "a bare date in <c>start</c> is all-day".</param>
/// <param name="Description">Free text.</param>
/// <param name="Location">Where.</param>
/// <param name="Attendees">Firm users, by subject; each must be a member of the caller's firm.</param>
public sealed record EventRequest(
    [property: Required(ErrorMessage = "A title is required."), Text(CalendarRules.MaxTitle)] string? Title,
    [property: Required(ErrorMessage = "A start is required.")] string? Start,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? End = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AllDay = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Narrative(CalendarRules.MaxDescription)] string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Text(CalendarRules.MaxLocation)] string? Location = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), MaxLength(CalendarRules.MaxAttendees, ErrorMessage = "At most 50 attendees.")] IReadOnlyList<string>? Attendees = null)
    : IValidatableObject
{
    /// <summary>The span, normalised: form dates when all-day, else UTC instants to the second.</summary>
    /// <returns>The start, the end and whether whole days.</returns>
    public (string Start, string End, bool AllDay) Span()
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        return Resolve(errors) ?? throw new FieldValidationException(errors);
    }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        Resolve(errors);
        var attendees = Attendees ?? [];
        for (var index = 0; index < attendees.Count; index++)
        {
            if (attendees[index] is not { } subject || !CalendarRules.ValidSubject(subject))
            {
                errors[$"attendees[{index}]"] = "Must be a firm-user subject.";
            }
            else if (attendees.Take(index).Contains(subject, StringComparer.Ordinal))
            {
                errors[$"attendees[{index}]"] = "Duplicate attendee.";
            }
        }

        return errors.Select(error => new ValidationResult(error.Value, [error.Key]));
    }

    private (string Start, string End, bool AllDay)? Resolve(Dictionary<string, string> errors)
    {
        var allDay = AllDay ?? Start?.Length == 10;
        var spanMessage = $"An event may span at most {CalendarEvents.MaxEventSpanDays} days.";
        if (allDay)
        {
            var startDay = Day(Start, "start", "A start date is required.", errors);
            var endDay = End is null ? startDay : Day(End, "end", null, errors);
            if (startDay is null || endDay is null)
            {
                return null;
            }

            var span = endDay.Value.DayNumber - startDay.Value.DayNumber;
            return Checked(span < 0, span > CalendarEvents.MaxEventSpanDays, CalendarEvents.FormatDay(startDay.Value), CalendarEvents.FormatDay(endDay.Value), allDay: true);
        }

        var startAt = Instant(Start, "start", "A start time is required.", errors);
        var endAt = End is null ? startAt?.AddHours(1) : Instant(End, "end", null, errors);
        if (startAt is null || endAt is null)
        {
            return null;
        }

        return Checked(endAt < startAt, endAt - startAt > TimeSpan.FromDays(CalendarEvents.MaxEventSpanDays), CalendarEvents.FormatInstant(startAt.Value), CalendarEvents.FormatInstant(endAt.Value), allDay: false);

        (string, string, bool)? Checked(bool backwards, bool tooLong, string start, string end, bool allDay)
        {
            if (backwards || tooLong)
            {
                errors["end"] = backwards ? "The end cannot precede the start." : spanMessage;
                return null;
            }

            return (start, end, allDay);
        }
    }

    private static DateOnly? Day(string? value, string path, string? required, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            errors[path] = required ?? "Must be a date in YYYY-MM-DD form.";
            return null;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day;
        }

        errors[path] = "Must be a date in YYYY-MM-DD form.";
        return null;
    }

    // Python's datetime.fromisoformat(value.replace("Z", "+00:00")): an
    // offset (or Z) is required, the result normalised to UTC and truncated
    // to the second.
    private static DateTimeOffset? Instant(string? value, string path, string? required, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            errors[path] = required ?? "Must be a date-time.";
            return null;
        }

        var match = CalendarRules.InstantPattern().Match(value);
        if (!match.Success || !DateTimeOffset.TryParse(match.Value.Replace(' ', 'T') + (match.Groups["offset"].Success ? string.Empty : "Z"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
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
}

/// <summary>Dismiss or restore an event (<c>PATCH …/events/{eventId}</c>). The member must be alone: a client that meant PUT finds out rather than losing its edits.</summary>
/// <param name="Dismissed">Whether it is dismissed.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventDismissal([property: Required(ErrorMessage = "Must be true or false.")] bool? Dismissed);

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

    /// <summary>A new hand-made event.</summary>
    public static CalendarEvent Create(EventRequest draft, EventScope scope, string createdBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(scope);
        var now = Clock.Now(clock);
        return Apply(new CalendarEvent(Guid.NewGuid().ToString(), scope.FirmId, scope.CaseId, draft.Title!, string.Empty, string.Empty, false, createdBy, now, now), draft);
    }

    /// <summary>A hand-made event with the draft applied — id, scope, author and created_at kept. A generated one is refused: its dates are the rule's.</summary>
    public static CalendarEvent Replace(CalendarEvent existing, EventRequest draft, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(draft);
        if (existing.Generated)
        {
            throw new Insolvia.Domain.ValidationException("a generated deadline cannot be edited — dismiss it, or change the case's dates and it is regenerated");
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

    private static CalendarEvent Apply(CalendarEvent target, EventRequest draft)
    {
        var (start, end, allDay) = draft.Span();
        return target with
        {
            Title = draft.Title!,
            Description = draft.Description,
            Start = start,
            End = end,
            AllDay = allDay,
            Location = draft.Location,
            Attendees = (draft.Attendees ?? []).ToValueCollection(),
        };
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d{1,6})?)?(?<offset>Z|[+-]\d{2}:?\d{2})?$", RegexOptions.CultureInvariant)]
    internal static partial Regex InstantPattern();

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
