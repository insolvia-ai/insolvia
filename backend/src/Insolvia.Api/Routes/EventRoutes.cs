using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Calendar;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Events;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Routes;

/// <summary>
/// Calendar events (routes/events.py; issue 14.6 / #358), gated by
/// <c>events</c>: the same six routes at CASE scope (reached through the case,
/// access-logged) and at FIRM scope. A generated deadline cannot be edited
/// (400) or deleted (400) — it is dismissed (<c>PATCH {"dismissed": …}</c>).
/// Attendees must be members of the caller's firm.
/// </summary>
/// <remarks>
/// Not here yet: <c>regenerate_deadlines</c>, the deadline engine's writer
/// (Insolvia.Engines, 18.8). Nothing in this slice generates an event.
/// </remarks>
public sealed class EventRoutes : IEndpointModule
{
    private const int MaxRequestBytes = 32 * 1024;
    private const string Logger = "insolvia_api.api.routes.events";
    private const string EventNotFound = "event not found";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/events", ListCaseAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/cases/{caseId}/events", CreateCaseAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/events/{eventId}", GetCaseAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/cases/{caseId}/events/{eventId}", PutCaseAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapPatch("/v1/cases/{caseId}/events/{eventId}", DismissCaseAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/events/{eventId}", DeleteCaseAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapGet("/v1/firm/events", ListFirmAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/firm/events", CreateFirmAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapGet("/v1/firm/events/{eventId}", GetFirmAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/firm/events/{eventId}", PutFirmAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapPatch("/v1/firm/events/{eventId}", DismissFirmAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/firm/events/{eventId}", DeleteFirmAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
    }

    // ── Case scope ───────────────────────────────────────────────────

    private static async Task<IResult> ListCaseAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        return await ListAsync(context, new EventScope(target.FirmId, target.Id), cancellationToken);
    }

    private static async Task<IResult> CreateCaseAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var draft = CalendarRules.Parse(await context.BodyAsync(MaxRequestBytes));
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        return await CreateAsync(context, draft, new EventScope(target.FirmId, target.Id), "case event created", cancellationToken);
    }

    private static async Task<IResult> GetCaseAsync(HttpContext context, string caseId, string eventId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        return FlaskJson.Result(CaseViews.Event(await EventOr404Async(context, new EventScope(target.FirmId, target.Id), eventId, cancellationToken)));
    }

    private static async Task<IResult> PutCaseAsync(HttpContext context, string caseId, string eventId, CancellationToken cancellationToken)
    {
        var draft = CalendarRules.Parse(await context.BodyAsync(MaxRequestBytes));
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        return await PutAsync(context, draft, new EventScope(target.FirmId, target.Id), eventId, "case event updated", cancellationToken);
    }

    private static async Task<IResult> DismissCaseAsync(HttpContext context, string caseId, string eventId, CancellationToken cancellationToken)
    {
        var dismissed = CalendarRules.ParseDismissal(await context.BodyAsync(MaxRequestBytes));
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        return await DismissAsync(context, dismissed, new EventScope(target.FirmId, target.Id), eventId, "case event dismissal changed", cancellationToken);
    }

    private static async Task<IResult> DeleteCaseAsync(HttpContext context, string caseId, string eventId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var scope = new EventScope(target.FirmId, target.Id);
        var found = await EventOr404Async(context, scope, eventId, cancellationToken);
        if (found.Generated)
        {
            throw new ValidationException("a generated deadline cannot be deleted — dismiss it");
        }

        return await DeleteAsync(context, scope, found, "case event deleted", cancellationToken);
    }

    // ── Firm scope ───────────────────────────────────────────────────

    private static async Task<IResult> ListFirmAsync(HttpContext context, CancellationToken cancellationToken) =>
        await ListAsync(context, new EventScope((await context.CurrentAccessorAsync()).FirmId), cancellationToken);

    private static async Task<IResult> CreateFirmAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var draft = CalendarRules.Parse(await context.BodyAsync(MaxRequestBytes));
        return await CreateAsync(context, draft, new EventScope(accessor.FirmId), "firm event created", cancellationToken);
    }

    private static async Task<IResult> GetFirmAsync(HttpContext context, string eventId, CancellationToken cancellationToken) =>
        FlaskJson.Result(CaseViews.Event(await EventOr404Async(context, new EventScope((await context.CurrentAccessorAsync()).FirmId), eventId, cancellationToken)));

    private static async Task<IResult> PutFirmAsync(HttpContext context, string eventId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var draft = CalendarRules.Parse(await context.BodyAsync(MaxRequestBytes));
        return await PutAsync(context, draft, new EventScope(accessor.FirmId), eventId, "firm event updated", cancellationToken);
    }

    private static async Task<IResult> DismissFirmAsync(HttpContext context, string eventId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var dismissed = CalendarRules.ParseDismissal(await context.BodyAsync(MaxRequestBytes));
        return await DismissAsync(context, dismissed, new EventScope(accessor.FirmId), eventId, "firm event dismissal changed", cancellationToken);
    }

    private static async Task<IResult> DeleteFirmAsync(HttpContext context, string eventId, CancellationToken cancellationToken)
    {
        var scope = new EventScope((await context.CurrentAccessorAsync()).FirmId);
        return await DeleteAsync(context, scope, await EventOr404Async(context, scope, eventId, cancellationToken), "firm event deleted", cancellationToken);
    }

    // ── Shared ───────────────────────────────────────────────────────

    private static async Task<IResult> ListAsync(HttpContext context, EventScope scope, CancellationToken cancellationToken)
    {
        var events = await context.Port<ICalendarEventStore>().ListForScopeAsync(scope, cancellationToken);
        return FlaskJson.Result(new JsonObject { ["events"] = new JsonArray([.. events.Select(found => (JsonNode)CaseViews.Event(found))]) });
    }

    private static async Task<IResult> CreateAsync(HttpContext context, EventDraft draft, EventScope scope, string message, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await CheckAttendeesAsync(context, accessor, draft, cancellationToken);
        var created = CalendarRules.Create(draft, scope, accessor.Subject, context.Clock());
        await context.Port<ICalendarEventStore>().CreateAsync(created, cancellationToken);
        Log(context, message, created);
        return FlaskJson.Result(CaseViews.Event(created), StatusCodes.Status201Created);
    }

    private static async Task<IResult> PutAsync(HttpContext context, EventDraft draft, EventScope scope, string eventId, string message, CancellationToken cancellationToken)
    {
        await CheckAttendeesAsync(context, await context.CurrentAccessorAsync(), draft, cancellationToken);
        var updated = CalendarRules.Replace(await EventOr404Async(context, scope, eventId, cancellationToken), draft, context.Clock());
        if (!await context.Port<ICalendarEventStore>().PutAsync(updated, cancellationToken))
        {
            throw new NotFoundException(EventNotFound);
        }

        Log(context, message, updated);
        return FlaskJson.Result(CaseViews.Event(updated));
    }

    private static async Task<IResult> DismissAsync(HttpContext context, bool dismissed, EventScope scope, string eventId, string message, CancellationToken cancellationToken)
    {
        var updated = CalendarRules.SetDismissed(await EventOr404Async(context, scope, eventId, cancellationToken), dismissed, context.Clock());
        if (!await context.Port<ICalendarEventStore>().PutAsync(updated, cancellationToken))
        {
            throw new NotFoundException(EventNotFound);
        }

        Log(context, message, updated);
        return FlaskJson.Result(CaseViews.Event(updated));
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, EventScope scope, CalendarEvent found, string message, CancellationToken cancellationToken)
    {
        if (!await context.Port<ICalendarEventStore>().DeleteAsync(scope, found.Id, cancellationToken))
        {
            throw new NotFoundException(EventNotFound);
        }

        Log(context, message, found);
        return FlaskJson.NoContent();
    }

    private static async Task<CalendarEvent> EventOr404Async(HttpContext context, EventScope scope, string eventId, CancellationToken cancellationToken) =>
        await context.Port<ICalendarEventStore>().GetAsync(scope, eventId, cancellationToken) ?? throw new NotFoundException(EventNotFound);

    // Every attendee a member of the caller's firm (any status), named by index.
    private static async Task CheckAttendeesAsync(HttpContext context, Accessor accessor, EventDraft draft, CancellationToken cancellationToken)
    {
        if (draft.Attendees.Count == 0)
        {
            return;
        }

        var members = (await context.FirmStore().ListUsersAsync(accessor.FirmId, cancellationToken)).Select(user => user.Subject).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < draft.Attendees.Count; index++)
        {
            if (!members.Contains(draft.Attendees[index]))
            {
                throw new ValidationException($"attendees[{index}] is not a member of your firm");
            }
        }
    }

    private static void Log(HttpContext context, string message, CalendarEvent calendarEvent) =>
        context.Log(Logger, message, ("case_id", calendarEvent.CaseId), ("id", calendarEvent.Id));
}

/// <summary>
/// The calendar (routes/calendar.py; issue 14.6 / #358): the firm's window
/// (<c>GET /v1/calendar</c>), the ICS feed, and its token. Every read filters
/// case events to the cases the caller may see — the store is firm-keyed and
/// knows nothing of access.
/// </summary>
/// <remarks>
/// <para><b>The feed authenticates one of two ways</b>, as Flask did: with
/// <c>?token=</c> (a calendar client cannot send a bearer token) the token
/// alone decides, and a bad one is the ordinary 401; without it, a staff
/// session with <c>events &gt;= view_only</c>. The token is the subject and a
/// secret whose hash alone is stored; it confers exactly what the person's
/// session would, re-checked (active user, active firm, the feature) on every fetch.</para>
/// </remarks>
public sealed class CalendarRoutes : IEndpointModule
{
    private const int MaxWindowDays = 400;
    private const int FeedLookbackDays = 62;
    private const int FeedLookaheadDays = 366;
    private const string Logger = "insolvia_api.api.routes.calendar";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/calendar", WindowAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);

        // Not RequireAuth(): the token path has no bearer token. The handler
        // runs the same two checks itself when there is no token.
        routes.MapGet("/v1/me/calendar.ics", FeedAsync).WithMetadata(PrincipalClass.Staff);
        routes.MapPost("/v1/me/calendar-token", MintTokenAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
        routes.MapDelete("/v1/me/calendar-token", RevokeTokenAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
    }

    private static async Task<IResult> WindowAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var starting = ParseDate(CaseRoutes.Query(context, "from"), "from");
        var ending = ParseDate(CaseRoutes.Query(context, "to"), "to");
        if (ending < starting)
        {
            throw new ValidationException("to must not precede from");
        }

        if (ending.DayNumber - starting.DayNumber > MaxWindowDays)
        {
            throw new ValidationException($"the window may span at most {MaxWindowDays} days");
        }

        var attendee = CaseRoutes.Query(context, "attendee") is { Length: > 0 } named ? named : null;
        if (attendee == "me")
        {
            attendee = accessor.Subject;
        }

        var caseId = CaseRoutes.Query(context, "case_id") is { Length: > 0 } given ? given : null;
        var events = await WindowEventsAsync(context, accessor, starting, ending, attendee, caseId, cancellationToken);
        return FlaskJson.Result(new JsonObject
        {
            ["from"] = FlaskJson.Date(starting),
            ["to"] = FlaskJson.Date(ending),
            ["events"] = new JsonArray([.. events.Select(found => (JsonNode)CaseViews.Event(found))]),
        });
    }

    private static async Task<IResult> FeedAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var token = context.Request.Query["token"];
        Accessor? accessor;
        if (token.Count > 0)
        {
            accessor = await AccessorFromTokenAsync(context, token[0] ?? string.Empty, cancellationToken);
            if (accessor is null)
            {
                context.Log(Logger, "calendar feed rejected", ("reason", "bad_token"));
                return JsonBodyResult.Unauthorized;
            }
        }
        else
        {
            if (!await StaffAuth.AuthenticateHereAsync(context))
            {
                return JsonBodyResult.Unauthorized;
            }

            accessor = await StaffAuth.RequirePermissionAsync(context, Feature.Events, PermissionLevel.ViewOnly);
        }

        var today = context.Today();
        var events = await WindowEventsAsync(context, accessor, today.AddDays(-FeedLookbackDays), today.AddDays(FeedLookaheadDays), null, null, cancellationToken);
        return new CalendarResult(CalendarFeed.RenderIcs(events, context.Clock().GetUtcNow()));
    }

    // Rotation: a new secret replaces the person's old one.
    private static async Task<IResult> MintTokenAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var secret = CalendarFeed.MintSecret();
        await context.Port<ICalendarTokenStore>().PutAsync(
            new CalendarToken(accessor.FirmId, accessor.Subject, CalendarFeed.HashSecret(secret), Clock.Now(context.Clock())),
            cancellationToken);
        context.Log(Logger, "calendar token minted");
        var token = CalendarFeed.FeedToken(accessor.Subject, secret);
        return FlaskJson.Result(new JsonObject { ["token"] = token, ["feedPath"] = $"/v1/me/calendar.ics?token={token}" }, StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeTokenAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        if (!await context.Port<ICalendarTokenStore>().DeleteAsync(accessor.FirmId, accessor.Subject, cancellationToken))
        {
            throw new NotFoundException("no calendar token to revoke");
        }

        context.Log(Logger, "calendar token revoked");
        return FlaskJson.NoContent();
    }

    private static async Task<Accessor?> AccessorFromTokenAsync(HttpContext context, string token, CancellationToken cancellationToken)
    {
        if (CalendarFeed.Split(token) is not var (subject, secret))
        {
            return null;
        }

        var firms = context.FirmStore();
        var user = await firms.FindUserAsync(subject, cancellationToken);
        if (user is null || user.Status != FirmUserStatus.Active)
        {
            return null;
        }

        var firm = await firms.GetFirmAsync(user.FirmId, cancellationToken);
        if (firm is null || firm.Status != FirmStatus.Active)
        {
            return null;
        }

        var stored = await context.Port<ICalendarTokenStore>().GetAsync(user.FirmId, subject, cancellationToken);
        if (stored is null || !CalendarFeed.SecretMatches(stored, secret))
        {
            return null;
        }

        var accessor = new Accessor(firm, user);
        return accessor.May(Feature.Events, PermissionLevel.ViewOnly) ? accessor : null;
    }

    // The store reads starts from `from - MAX_EVENT_SPAN_DAYS`; the end, the
    // caller's visibility and the filters are applied here.
    private static async Task<List<CalendarEvent>> WindowEventsAsync(HttpContext context, Accessor accessor, DateOnly starting, DateOnly ending, string? attendee, string? caseId, CancellationToken cancellationToken)
    {
        var lower = new DateTimeOffset(starting.AddDays(-CalendarEvents.MaxEventSpanDays).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var upper = new DateTimeOffset(ending.ToDateTime(new TimeOnly(23, 59, 59)), TimeSpan.Zero);
        var candidates = await context.Port<ICalendarEventStore>().ListForFirmAsync(accessor.FirmId, lower, upper, cancellationToken);
        HashSet<string>? visible = accessor.SeesEveryCase
            ? null
            : (await TaskRoutes.ReachableCaseIdsAsync(context.Cases(), accessor, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var windowStart = new DateTimeOffset(starting.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return [.. candidates.Where(found =>
            CalendarEvents.EndInstant(found) >= windowStart
            && (found.CaseId is null || visible is null || visible.Contains(found.CaseId))
            && (caseId is null || found.CaseId == caseId)
            && (attendee is null || found.Attendees.Contains(attendee)))];
    }

    // Python's date.fromisoformat after strip: YYYY-MM-DD.
    private static DateOnly ParseDate(string? raw, string name)
    {
        if (raw is null || PyText.Strip(raw).Length == 0)
        {
            throw new ValidationException($"{name} is required (YYYY-MM-DD)");
        }

        return DateOnly.TryParseExact(PyText.Strip(raw), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new ValidationException($"{name} must be a date in YYYY-MM-DD form");
    }

    /// <summary>Flask's <c>Response(body, mimetype="text/calendar")</c> with the feed's two headers.</summary>
    private sealed class CalendarResult(string body) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/calendar; charset=utf-8";
            response.Headers.ContentDisposition = "attachment; filename=\"insolvia.ics\"";
            response.Headers.CacheControl = "private, no-store";
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength = bytes.Length;
            if (!HttpMethods.IsHead(httpContext.Request.Method))
            {
                await response.Body.WriteAsync(bytes, httpContext.RequestAborted);
            }
        }
    }
}
