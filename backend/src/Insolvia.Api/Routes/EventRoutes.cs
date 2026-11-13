using System.Globalization;
using System.Text;
using Insolvia.Api.Auth;
using Insolvia.Api.Calendar;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Domain;
using Insolvia.Domain.Access;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.Events;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Ports;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Insolvia.Api.Routes;

/// <summary>
/// Calendar events (routes/events.py; issue 14.6 / #358), gated by
/// <c>events</c>: the same six routes at CASE scope (reached through the case,
/// access-logged) and at FIRM scope. A generated deadline cannot be edited
/// (400) or deleted (400) — it is dismissed (<c>PATCH {"dismissed": …}</c>).
/// Attendees must be members of the caller's firm.
/// </summary>
/// <remarks>
/// The generated events are written by the case PATCH's hook
/// (<see cref="Calendar.CaseDeadlines"/>), never by these routes.
/// </remarks>
public sealed class EventRoutes : IEndpointModule
{
    private const int MaxRequestBytes = 32 * 1024;
    private static readonly RequestSizeLimitAttribute SizeLimit = new(MaxRequestBytes);
    private const string Logger = "insolvia_api.api.routes.events";
    private const string EventNotFound = "event not found";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/events", ListCaseAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/cases/{caseId}/events", CreateCaseAsync).WithMetadata(SizeLimit).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/events/{eventId}", GetCaseAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/cases/{caseId}/events/{eventId}", PutCaseAsync).WithMetadata(SizeLimit).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapPatch("/v1/cases/{caseId}/events/{eventId}", DismissCaseAsync).WithMetadata(SizeLimit).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/events/{eventId}", DeleteCaseAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapGet("/v1/firm/events", ListFirmAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/firm/events", CreateFirmAsync).WithMetadata(SizeLimit).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapGet("/v1/firm/events/{eventId}", GetFirmAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/firm/events/{eventId}", PutFirmAsync).WithMetadata(SizeLimit).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapPatch("/v1/firm/events/{eventId}", DismissFirmAsync).WithMetadata(SizeLimit).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/firm/events/{eventId}", DeleteFirmAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.AddEdit);
    }

    // ── Case scope ───────────────────────────────────────────────────

    /// <summary>A case's events.</summary>
    internal static async Task<EventList> ListCaseAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        return await ListAsync(context, new EventScope(target.FirmId, target.Id), cancellationToken);
    }

    /// <summary>Add an event to a case's calendar.</summary>
    /// <remarks>Attendees must be members of the caller's firm.</remarks>
    internal static async Task<Created<EventView>> CreateCaseAsync(HttpContext context, string caseId, EventRequest draft, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        return await CreateAsync(context, draft, new EventScope(target.FirmId, target.Id), "case event created", cancellationToken);
    }

    /// <summary>One event of a case.</summary>
    internal static async Task<EventView> GetCaseAsync(HttpContext context, string caseId, string eventId, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        return EventView.Of(await EventOr404Async(context, new EventScope(target.FirmId, target.Id), eventId, cancellationToken));
    }

    /// <summary>Replace a case event, whole.</summary>
    /// <remarks>A generated deadline cannot be edited (400) — dismiss it instead.</remarks>
    internal static async Task<EventView> PutCaseAsync(HttpContext context, string caseId, string eventId, EventRequest draft, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        return await PutAsync(context, draft, new EventScope(target.FirmId, target.Id), eventId, "case event updated", cancellationToken);
    }

    /// <summary>Dismiss or restore a case event.</summary>
    internal static async Task<EventView> DismissCaseAsync(HttpContext context, string caseId, string eventId, EventDismissal request, CancellationToken cancellationToken)
    {
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        return await DismissAsync(context, request.Dismissed!.Value, new EventScope(target.FirmId, target.Id), eventId, "case event dismissal changed", cancellationToken);
    }

    /// <summary>Delete a case event.</summary>
    /// <remarks>A generated deadline cannot be deleted (400) — dismiss it instead.</remarks>
    internal static async Task<NoContent> DeleteCaseAsync(HttpContext context, string caseId, string eventId, CancellationToken cancellationToken)
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

    /// <summary>The firm's own events.</summary>
    /// <remarks>The events on no case.</remarks>
    internal static async Task<EventList> ListFirmAsync(HttpContext context, CancellationToken cancellationToken) =>
        await ListAsync(context, new EventScope((await context.CurrentAccessorAsync()).FirmId), cancellationToken);

    /// <summary>Add an event to the firm's own calendar.</summary>
    /// <remarks>Attendees must be members of the caller's firm.</remarks>
    internal static async Task<Created<EventView>> CreateFirmAsync(HttpContext context, EventRequest draft, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        return await CreateAsync(context, draft, new EventScope(accessor.FirmId), "firm event created", cancellationToken);
    }

    /// <summary>One of the firm's own events.</summary>
    internal static async Task<EventView> GetFirmAsync(HttpContext context, string eventId, CancellationToken cancellationToken) =>
        EventView.Of(await EventOr404Async(context, new EventScope((await context.CurrentAccessorAsync()).FirmId), eventId, cancellationToken));

    /// <summary>Replace one of the firm's own events, whole.</summary>
    internal static async Task<EventView> PutFirmAsync(HttpContext context, string eventId, EventRequest draft, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        return await PutAsync(context, draft, new EventScope(accessor.FirmId), eventId, "firm event updated", cancellationToken);
    }

    /// <summary>Dismiss or restore one of the firm's own events.</summary>
    internal static async Task<EventView> DismissFirmAsync(HttpContext context, string eventId, EventDismissal request, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        return await DismissAsync(context, request.Dismissed!.Value, new EventScope(accessor.FirmId), eventId, "firm event dismissal changed", cancellationToken);
    }

    /// <summary>Delete one of the firm's own events.</summary>
    internal static async Task<NoContent> DeleteFirmAsync(HttpContext context, string eventId, CancellationToken cancellationToken)
    {
        var scope = new EventScope((await context.CurrentAccessorAsync()).FirmId);
        return await DeleteAsync(context, scope, await EventOr404Async(context, scope, eventId, cancellationToken), "firm event deleted", cancellationToken);
    }

    // ── Shared ───────────────────────────────────────────────────────

    private static async Task<EventList> ListAsync(HttpContext context, EventScope scope, CancellationToken cancellationToken)
    {
        var events = await context.Port<ICalendarEventStore>().ListForScopeAsync(scope, cancellationToken);
        return new EventList([.. events.Select(EventView.Of)]);
    }

    private static async Task<Created<EventView>> CreateAsync(HttpContext context, EventRequest draft, EventScope scope, string message, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await CheckAttendeesAsync(context, accessor, draft, cancellationToken);
        var created = CalendarRules.Create(draft, scope, accessor.Subject, context.Clock());
        await context.Port<ICalendarEventStore>().CreateAsync(created, cancellationToken);
        Log(context, message, created);
        return TypedResults.Created((string?)null, EventView.Of(created));
    }

    private static async Task<EventView> PutAsync(HttpContext context, EventRequest draft, EventScope scope, string eventId, string message, CancellationToken cancellationToken)
    {
        await CheckAttendeesAsync(context, await context.CurrentAccessorAsync(), draft, cancellationToken);
        var updated = CalendarRules.Replace(await EventOr404Async(context, scope, eventId, cancellationToken), draft, context.Clock());
        if (!await context.Port<ICalendarEventStore>().PutAsync(updated, cancellationToken))
        {
            throw new NotFoundException(EventNotFound);
        }

        Log(context, message, updated);
        return EventView.Of(updated);
    }

    private static async Task<EventView> DismissAsync(HttpContext context, bool dismissed, EventScope scope, string eventId, string message, CancellationToken cancellationToken)
    {
        var updated = CalendarRules.SetDismissed(await EventOr404Async(context, scope, eventId, cancellationToken), dismissed, context.Clock());
        if (!await context.Port<ICalendarEventStore>().PutAsync(updated, cancellationToken))
        {
            throw new NotFoundException(EventNotFound);
        }

        Log(context, message, updated);
        return EventView.Of(updated);
    }

    private static async Task<NoContent> DeleteAsync(HttpContext context, EventScope scope, CalendarEvent found, string message, CancellationToken cancellationToken)
    {
        if (!await context.Port<ICalendarEventStore>().DeleteAsync(scope, found.Id, cancellationToken))
        {
            throw new NotFoundException(EventNotFound);
        }

        Log(context, message, found);
        return TypedResults.NoContent();
    }

    private static async Task<CalendarEvent> EventOr404Async(HttpContext context, EventScope scope, string eventId, CancellationToken cancellationToken) =>
        await context.Port<ICalendarEventStore>().GetAsync(scope, eventId, cancellationToken) ?? throw new NotFoundException(EventNotFound);

    // Every attendee a member of the caller's firm (any status), named by index.
    private static async Task CheckAttendeesAsync(HttpContext context, Accessor accessor, EventRequest draft, CancellationToken cancellationToken)
    {
        var attendees = draft.Attendees ?? [];
        if (attendees.Count == 0)
        {
            return;
        }

        var members = (await context.FirmStore().ListUsersAsync(accessor.FirmId, cancellationToken)).Select(user => user.Subject).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < attendees.Count; index++)
        {
            if (!members.Contains(attendees[index]))
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
        routes.MapGet("/v1/calendar", WindowAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly)
            .QueryParameter("from", "The window's first day, YYYY-MM-DD.", required: true)
            .QueryParameter("to", "The window's last day, YYYY-MM-DD; not before from, and at most the route's maximum span after it.", required: true)
            .QueryParameter("attendee", "Only this subject's events; `me` for the caller's.")
            .QueryParameter("case_id", "Only this case's events.");

        // Not RequireAuth(): the token path has no bearer token. The handler
        // runs the same two checks itself when there is no token.
        routes.MapGet("/v1/me/calendar.ics", FeedAsync).WithMetadata(PrincipalClass.Staff).Produces<string>(StatusCodes.Status200OK, "text/calendar")
            .QueryParameter("token", "A calendar feed token (POST /v1/me/calendar-token), for a calendar client that cannot send a bearer token. Without it, a staff session is required.");
        routes.MapPost("/v1/me/calendar-token", MintTokenAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
        routes.MapDelete("/v1/me/calendar-token", RevokeTokenAsync).RequireAuth().Requires(Feature.Events, PermissionLevel.ViewOnly);
    }

    /// <summary>The firm's calendar.</summary>
    /// <remarks>
    /// Every event in a window of at most 400 days, filtered to the cases the
    /// caller may see.
    /// <c>from</c> and <c>to</c> (<c>YYYY-MM-DD</c>, inclusive) are required.
    /// <c>attendee</c> narrows to one subject (<c>me</c> for the caller);
    /// <c>case_id</c> to one case.
    /// </remarks>
    internal static async Task<CalendarWindow> WindowAsync(HttpContext context, CancellationToken cancellationToken)
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
        return new CalendarWindow(WireFormat.Date(starting), WireFormat.Date(ending), [.. events.Select(EventView.Of)]);
    }

    /// <summary>The caller's calendar feed.</summary>
    /// <remarks>
    /// An ICS file covering two months back and a year ahead.
    /// Authenticated by <c>?token=</c> (a calendar client cannot send a bearer
    /// token) or, without one, by a staff session with <c>events</c>.
    /// </remarks>
    internal static async Task<IResult> FeedAsync(HttpContext context, CancellationToken cancellationToken)
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

    /// <summary>Mint the caller's calendar-feed token.</summary>
    /// <remarks>Rotation: a new token replaces the caller's old one, which stops working.</remarks>
    internal static async Task<Created<CalendarFeedToken>> MintTokenAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var secret = CalendarFeed.MintSecret();
        await context.Port<ICalendarTokenStore>().PutAsync(
            new CalendarToken(accessor.FirmId, accessor.Subject, CalendarFeed.HashSecret(secret), Clock.Now(context.Clock())),
            cancellationToken);
        context.Log(Logger, "calendar token minted");
        var token = CalendarFeed.FeedToken(accessor.Subject, secret);
        return TypedResults.Created((string?)null, new CalendarFeedToken(token, $"/v1/me/calendar.ics?token={token}"));
    }

    /// <summary>Revoke the caller's calendar-feed token.</summary>
    internal static async Task<NoContent> RevokeTokenAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        if (!await context.Port<ICalendarTokenStore>().DeleteAsync(accessor.FirmId, accessor.Subject, cancellationToken))
        {
            throw new NotFoundException("no calendar token to revoke");
        }

        context.Log(Logger, "calendar token revoked");
        return TypedResults.NoContent();
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
        if (raw is null || TextRules.Strip(raw).Length == 0)
        {
            throw new ValidationException($"{name} is required (YYYY-MM-DD)");
        }

        return DateOnly.TryParseExact(TextRules.Strip(raw), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
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
