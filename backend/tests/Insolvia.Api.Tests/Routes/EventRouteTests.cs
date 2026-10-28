using Insolvia.Api.Calendar;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.Events;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_event_routes.py: events at case and firm scope, the calendar window,
/// and the ICS feed with its token. The cast: Alice admin; Dana sees every
/// case with <c>events: view_only</c>; Bob linked-only; Greg with events hidden;
/// Carol the other firm. The deadline engine's five tests (a filed date
/// populating deadlines, the § 341 date regenerating, clearing, the generated
/// deadline from a rule) wait for Insolvia.Engines (18.8); the "generated
/// cannot be edited or deleted" rule is tested here on a generated event
/// written straight to the store.
/// </summary>
public sealed class EventRouteTests : IAsyncLifetime, IDisposable
{
    private const string Hearing = """{"title": "Hearing on the motion", "start": "2026-03-02", "location": "Courtroom 8A"}""";

    private readonly CaseWorld _world = new();

    public async ValueTask InitializeAsync()
    {
        await _world.SeedAsync();
        await _world.ReplaceAsync(FirmWorld.Member(Dana, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.Events, PermissionLevel.ViewOnly)));
        await _world.ReplaceAsync(FirmWorld.Member(Greg, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.Events, PermissionLevel.Hidden)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("GET", "/v1/cases/any-id/events")]
    [InlineData("POST", "/v1/cases/any-id/events")]
    [InlineData("GET", "/v1/cases/any-id/events/any-event")]
    [InlineData("PUT", "/v1/cases/any-id/events/any-event")]
    [InlineData("PATCH", "/v1/cases/any-id/events/any-event")]
    [InlineData("DELETE", "/v1/cases/any-id/events/any-event")]
    [InlineData("GET", "/v1/firm/events")]
    [InlineData("POST", "/v1/firm/events")]
    [InlineData("GET", "/v1/calendar?from=2026-03-01&to=2026-03-31")]
    [InlineData("GET", "/v1/me/calendar.ics")]
    [InlineData("POST", "/v1/me/calendar-token")]
    [InlineData("DELETE", "/v1/me/calendar-token")]
    public async Task Every_route_refuses_an_unauthenticated_caller(string method, string path)
    {
        var answer = await _world.World.SendAsync(new HttpMethod(method), path, json: "{}");

        Assert.Equal(401, answer.Status);
        Assert.Equal(ErrorBodies.Unauthorized, answer.Body);
    }

    [Fact]
    public async Task Hidden_cannot_even_list_and_view_only_lists_but_cannot_add()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(403, (await _world.Get($"/v1/cases/{caseId}/events", Greg)).Status);
        Assert.Equal(403, (await _world.Get("/v1/firm/events", Greg)).Status);
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}/events", Dana)).Status);
        Assert.Equal(403, (await Add(caseId, subject: Dana)).Status);
    }

    [Fact]
    public async Task Another_firms_case_and_an_unlinked_one_are_not_found()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(404, (await Add(caseId, subject: Carol)).Status);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/events", Carol)).Status);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/events", Bob)).Status);
    }

    [Fact]
    public async Task A_created_event_is_returned_and_then_listed()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var created = await Add(caseId);

        Assert.Equal(201, created.Status);
        Assert.Equal(("Hearing on the motion", caseId, "2026-03-02", "2026-03-02"), (created.Str("title"), created.Str("case_id"), created.Str("start"), created.Str("end")));
        Assert.True(created.Object["all_day"]!.GetValue<bool>());
        Assert.False(created.Object["generated"]!.GetValue<bool>());
        Assert.Equal(["all_day", "attendees", "case_id", "created_at", "created_by", "dismissed", "end", "generated", "id", "location", "start", "title", "updated_at"], Answer.Keys(created.Json));
        Assert.Equal([created.Str("id")], (await _world.Get($"/v1/cases/{caseId}/events")).Object["events"]!.AsArray().Select(e => e!.AsObject().Str("id")));
    }

    [Fact]
    public async Task A_missing_title_and_an_attendee_outside_the_firm_are_refused()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Contains("title", (await Add(caseId, """{"start": "2026-03-02"}""")).FieldErrors);
        var outsider = await Add(caseId, $$"""{"title": "x", "start": "2026-03-02", "attendees": ["{{Carol}}"]}""");
        Assert.Equal(400, outsider.Status);
        Assert.Contains("attendees[0]", outsider.Str("message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replace_keeps_the_id_normalises_the_time_and_a_delete_removes_it()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var eventId = (await Add(caseId)).Str("id");
        var url = $"/v1/cases/{caseId}/events/{eventId}";

        var replaced = await _world.Put(url, $$"""{"title": "Moved", "start": "2026-03-03T14:00:00-05:00", "attendees": ["{{Bob}}"]}""");

        Assert.Equal(200, replaced.Status);
        Assert.Equal((eventId, "2026-03-03T19:00:00Z", "2026-03-03T20:00:00Z", $"[\"{Bob}\"]"), (replaced.Str("id"), replaced.Str("start"), replaced.Str("end"), replaced.Object["attendees"]!.ToJsonString()));
        Assert.Equal(204, (await _world.Delete(url)).Status);
        Assert.Equal("""{"error":"NotFoundError","message":"event not found"}""", (await _world.Get(url)).Body);
    }

    [Fact]
    public async Task A_dismissal_is_the_one_flag_alone_and_survives()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var url = $"/v1/cases/{caseId}/events/{(await Add(caseId)).Str("id")}";

        Assert.True((await _world.Patch(url, """{"dismissed": true}""")).Object["dismissed"]!.GetValue<bool>());
        Assert.Equal(["dismissed"], (await _world.Patch(url, """{"dismissed": false, "title": "x"}""")).FieldErrors);
        Assert.True((await _world.Get(url)).Object["dismissed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Firm_events_are_scoped_to_the_callers_firm()
    {
        var created = await _world.Post("/v1/firm/events", """{"title": "Office closed", "start": "2026-07-03"}""");
        var eventId = created.Str("id");

        Assert.Equal(201, created.Status);
        Assert.False(created.Object.ContainsKey("case_id"));
        Assert.Equal([eventId], (await _world.Get("/v1/firm/events", Dana)).Object["events"]!.AsArray().Select(e => e!.AsObject().Str("id")));
        Assert.Equal("""{"events":[]}""", (await _world.Get("/v1/firm/events", Carol)).Body);
        Assert.Equal(404, (await _world.Get($"/v1/firm/events/{eventId}", Carol)).Status);
        Assert.Equal(200, (await _world.Put($"/v1/firm/events/{eventId}", """{"title": "Office closed early", "start": "2026-07-03"}""")).Status);
        Assert.Equal(204, (await _world.Delete($"/v1/firm/events/{eventId}")).Status);
    }

    [Fact]
    public async Task A_meeting_before_the_petition_is_refused()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Contains("meeting_341_at", (await _world.Patch($"/v1/cases/{caseId}", """{"filed_at": "2026-03-02", "meeting_341_at": "2026-03-01"}""")).FieldErrors);
    }

    [Fact]
    public async Task A_generated_deadline_cannot_be_edited_or_deleted_only_dismissed()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var deadline = new CalendarEvent(Guid.NewGuid().ToString(), Insolvia.Api.Tests.Support.Ids.FirmA, caseId, "Schedules, statements and other documents due", "2026-03-16", "2026-03-16", AllDay: true, "deadline-engine", FirmWorld.Seeded, FirmWorld.Seeded)
        {
            RuleId = "frbp-1007c1-schedules",
            RuleCitation = "Fed. R. Bankr. P. 1007(c)(1)",
        };
        await _world.Service<ICalendarEventStore>().CreateAsync(deadline, HttpAssert.Ct);
        var url = $"/v1/cases/{caseId}/events/{deadline.Id}";

        Assert.Equal(400, (await _world.Put(url, """{"title": "Moved", "start": "2026-12-25"}""")).Status);
        Assert.Equal(400, (await _world.Delete(url)).Status);
        var dismissed = await _world.Patch(url, """{"dismissed": true}""");
        Assert.Equal(("2026-03-16", true, "frbp-1007c1-schedules"), (dismissed.Str("start"), dismissed.Object["generated"]!.GetValue<bool>(), dismissed.Str("rule_id")));
    }

    // ── The calendar ────────────────────────────────────────────────

    [Fact]
    public async Task The_calendar_answers_a_window_across_cases_and_the_firm_in_start_order()
    {
        var first = await _world.OpenCaseIdAsync();
        var second = await _world.OpenCaseIdAsync();
        await Add(first, """{"title": "Meeting of creditors", "start": "2026-03-25T14:00:00Z"}""");
        await Add(second, """{"title": "Client call", "start": "2026-03-10T15:00:00Z"}""");
        await Add(second, """{"title": "In May", "start": "2026-05-04"}""");
        await _world.Post("/v1/firm/events", """{"title": "Office closed", "start": "2026-03-20"}""");

        var march = (await _world.Get("/v1/calendar?from=2026-03-01&to=2026-03-31", Dana)).Object;

        Assert.Equal(("2026-03-01", "2026-03-31"), (march.Str("from"), march.Str("to")));
        Assert.Equal(["Client call", "Office closed", "Meeting of creditors"], march["events"]!.AsArray().Select(e => e!.AsObject().Str("title")));
        var onlySecond = (await _world.Get($"/v1/calendar?from=2026-03-01&to=2026-03-31&case_id={second}")).Object["events"]!.AsArray();
        Assert.Equal(["Client call"], onlySecond.Select(e => e!.AsObject().Str("title")));
    }

    [Fact]
    public async Task The_calendar_hides_cases_the_caller_cannot_see()
    {
        var hidden = await _world.OpenCaseIdAsync();
        await Add(hidden, """{"title": "Not Bob's", "start": "2026-03-04"}""");
        var visible = await _world.OpenCaseIdAsync(Bob);
        await Add(visible, """{"title": "Bob's hearing", "start": "2026-03-05"}""", Bob);

        Assert.Equal(["Bob's hearing"], (await _world.Get("/v1/calendar?from=2026-03-01&to=2026-03-31", Bob)).Object["events"]!.AsArray().Select(e => e!.AsObject().Str("title")));
        var probe = await _world.Get($"/v1/calendar?from=2026-03-01&to=2026-03-31&case_id={hidden}", Bob);
        Assert.Equal((200, "[]"), (probe.Status, probe.Object["events"]!.ToJsonString()));
    }

    [Fact]
    public async Task The_calendar_filters_by_attendee_and_includes_a_span_overlapping_its_start()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Add(caseId, Hearing[..^1] + $$""", "attendees": ["{{Bob}}"]}""");
        await Add(caseId, """{"title": "Span", "start": "2026-02-25", "end": "2026-03-02"}""");

        Assert.Equal("[]", (await _world.Get("/v1/calendar?from=2026-03-01&to=2026-03-31&attendee=me", Bob)).Object["events"]!.ToJsonString());
        Assert.Equal(["Hearing on the motion"], (await _world.Get($"/v1/calendar?from=2026-03-01&to=2026-03-31&attendee={Bob}")).Object["events"]!.AsArray().Select(e => e!.AsObject().Str("title")));
        Assert.Contains("Span", (await _world.Get("/v1/calendar?from=2026-03-01&to=2026-03-31")).Object["events"]!.AsArray().Select(e => e!.AsObject().Str("title")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("from=2026-03-01")]
    [InlineData("from=2026-03-31&to=2026-03-01")]
    [InlineData("from=2026-01-01&to=2028-01-01")]
    [InlineData("from=2026-3-1&to=2026-03-31")]
    public async Task A_bad_window_is_a_400(string query)
    {
        Assert.Equal(400, (await _world.Get($"/v1/calendar?{query}")).Status);
    }

    // ── The feed ────────────────────────────────────────────────────

    [Fact]
    public async Task The_session_feed_is_an_ics_attachment_that_is_never_cached()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var today = Insolvia.Core.Clock.Today();
        await Add(caseId, $$"""{"title": "Hearing", "start": "{{Http.FlaskJson.Date(today.AddDays(3))}}"}""");

        var feed = await _world.Get("/v1/me/calendar.ics");

        Assert.Equal(200, feed.Status);
        Assert.Equal("text/calendar; charset=utf-8", feed.ContentType);
        Assert.Equal("attachment; filename=\"insolvia.ics\"", feed.Response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal("private, no-store", feed.Response.Headers.NonValidated["Cache-Control"].ToString());
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", feed.Body, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Hearing\r\n", feed.Body, StringComparison.Ordinal);
        Assert.Equal(403, (await _world.Get("/v1/me/calendar.ics", Greg)).Status);
        Assert.Equal(403, (await _world.Get("/v1/me/calendar.ics", Frank)).Status);
    }

    [Fact]
    public async Task A_feed_token_opens_the_feed_and_only_that_users_feed()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Add(caseId, $$"""{"title": "Hearing", "start": "{{Http.FlaskJson.Date(Insolvia.Core.Clock.Today().AddDays(1))}}"}""");

        var minted = await _world.Post("/v1/me/calendar-token", null);
        var token = minted.Str("token")!;

        Assert.Equal(201, minted.Status);
        Assert.Equal($"/v1/me/calendar.ics?token={token}", minted.Str("feedPath"));
        Assert.StartsWith(Alice + ".", token, StringComparison.Ordinal);
        var feed = await _world.Get($"/v1/me/calendar.ics?token={token}", null);
        Assert.Equal((200, "text/calendar; charset=utf-8"), (feed.Status, feed.ContentType));
        Assert.Contains("SUMMARY:Hearing", feed.Body, StringComparison.Ordinal);

        var bobs = (await _world.Post("/v1/me/calendar-token", null, Bob)).Str("token");
        var bobFeed = await _world.Get($"/v1/me/calendar.ics?token={bobs}", null);
        Assert.Equal(200, bobFeed.Status);
        Assert.DoesNotContain("SUMMARY:Hearing", bobFeed.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_revoked_or_rotated_token_is_the_ordinary_401()
    {
        Assert.Equal(ErrorBodies.Unauthorized, (await _world.Get("/v1/me/calendar.ics?token=garbage", null)).Body);
        Assert.Equal(401, (await _world.Get("/v1/me/calendar.ics?token=", null)).Status);
        var first = (await _world.Post("/v1/me/calendar-token", null)).Str("token");
        Assert.Equal(401, (await _world.Get($"/v1/me/calendar.ics?token={first}x", null)).Status);

        var second = (await _world.Post("/v1/me/calendar-token", null)).Str("token");
        Assert.NotEqual(first, second);
        Assert.Equal(401, (await _world.Get($"/v1/me/calendar.ics?token={first}", null)).Status);
        Assert.Equal(200, (await _world.Get($"/v1/me/calendar.ics?token={second}", null)).Status);

        Assert.Equal(204, (await _world.Delete("/v1/me/calendar-token")).Status);
        Assert.Equal(401, (await _world.Get($"/v1/me/calendar.ics?token={second}", null)).Status);
        Assert.Equal(404, (await _world.Delete("/v1/me/calendar-token")).Status);
    }

    [Fact]
    public async Task A_hidden_user_cannot_mint_and_a_token_stops_working_when_the_feature_goes()
    {
        Assert.Equal(403, (await _world.Post("/v1/me/calendar-token", null, Greg)).Status);
        var token = (await _world.Post("/v1/me/calendar-token", null, Dana)).Str("token");
        Assert.Equal(200, (await _world.Get($"/v1/me/calendar.ics?token={token}", null)).Status);

        await _world.ReplaceAsync(FirmWorld.Member(Dana, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.Events, PermissionLevel.Hidden)));

        Assert.Equal(401, (await _world.Get($"/v1/me/calendar.ics?token={token}", null)).Status);
    }

    private Task<Answer> Add(string caseId, string body = Hearing, string subject = Alice) =>
        _world.Post($"/v1/cases/{caseId}/events", body, subject);
}

/// <summary>
/// test_events.py's pure half: the event parser, the wire shape, replace and
/// dismissal, the feed token and the ICS rendering. (Its item-shape tests are
/// the calendar store conformance suite's; its deadline tests wait for 18.8.)
/// </summary>
public sealed class CalendarRulesTests
{
    private const string Firm = "00000000-0000-4000-8000-00000000f18a";
    private const string Alice = "00000000-0000-4000-8000-00000000a11c";

    [Fact]
    public void A_title_and_a_start_are_required()
    {
        var error = Assert.Throws<Insolvia.Core.FieldValidationException>(() => CalendarRules.Parse(new System.Text.Json.Nodes.JsonObject()));

        Assert.Equal(["start", "title"], error.Fields.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_bare_date_start_means_all_day_and_end_defaults_to_start()
    {
        var draft = Parse("""{"title": "Hearing", "start": "2026-03-02"}""");

        Assert.Equal((true, "2026-03-02", "2026-03-02"), (draft.AllDay, draft.Start, draft.End));
    }

    [Fact]
    public void A_timed_event_is_normalised_to_utc_truncated_to_the_second_and_ends_an_hour_later()
    {
        var draft = Parse("""{"title": "Call", "start": "2026-03-02T10:00:00.750-05:00"}""");

        Assert.Equal((false, "2026-03-02T15:00:00Z", "2026-03-02T16:00:00Z"), (draft.AllDay, draft.Start, draft.End));
    }

    [Theory]
    [InlineData("""{"title": "Call", "start": "2026-03-02T15:00:00", "all_day": false}""", "start")]
    [InlineData("""{"title": "Call", "start": "next tuesday", "all_day": false}""", "start")]
    [InlineData("""{"title": "Call", "start": "2026-03-02", "end": "2026-03-01"}""", "end")]
    [InlineData("""{"title": "Long", "start": "2026-01-01", "end": "2026-03-05"}""", "end")]
    [InlineData("""{"title": "Long", "start": "2026-01-01T00:00:00Z", "end": "2026-03-05T00:00:00Z"}""", "end")]
    [InlineData("""{"title": "x", "start": "2026-01-01", "all_day": "yes"}""", "all_day")]
    [InlineData("""{"title": "x", "start": "2026-01-01", "attendees": ["not-a-subject"]}""", "attendees[0]")]
    [InlineData("""{"title": "x", "start": "2026-01-01", "attendees": ["00000000-0000-4000-8000-00000000a11c", "00000000-0000-4000-8000-00000000a11c"]}""", "attendees[1]")]
    [InlineData("""{"title": "x", "start": "2026-01-01", "attendees": "00000000-0000-4000-8000-00000000a11c"}""", "attendees")]
    public void A_bad_event_is_refused_naming_the_field(string body, string field)
    {
        var error = Assert.Throws<Insolvia.Core.FieldValidationException>(() => Parse(body));

        Assert.Contains(field, error.Fields.Keys);
    }

    [Fact]
    public void A_span_of_exactly_the_cap_is_allowed()
    {
        Assert.Equal("2026-03-04", Parse("""{"title": "Long", "start": "2026-01-01", "end": "2026-03-04"}""").End);
    }

    [Theory]
    [InlineData("""{"dismissed": true}""", true)]
    [InlineData("""{"dismissed": false}""", false)]
    public void A_dismissal_body_is_the_one_flag_alone(string body, bool expected)
    {
        Assert.Equal(expected, CalendarRules.ParseDismissal(System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject()));
        Assert.Throws<Insolvia.Core.FieldValidationException>(() => CalendarRules.ParseDismissal(System.Text.Json.Nodes.JsonNode.Parse("""{"dismissed": true, "title": "x"}""")!.AsObject()));
        Assert.Throws<Insolvia.Core.FieldValidationException>(() => CalendarRules.ParseDismissal(System.Text.Json.Nodes.JsonNode.Parse("""{"dismissed": "yes"}""")!.AsObject()));
    }

    [Fact]
    public void Replacing_keeps_identity_and_refuses_a_generated_event()
    {
        var original = CalendarRules.Create(Parse("""{"title": "Hearing", "start": "2026-03-02"}"""), new EventScope(Firm, "case-1"), Alice);

        var replaced = CalendarRules.Replace(original, Parse("""{"title": "Moved", "start": "2026-03-03T09:00:00Z"}"""));

        Assert.Equal((original.Id, original.CaseId, original.CreatedBy, original.CreatedAt), (replaced.Id, replaced.CaseId, replaced.CreatedBy, replaced.CreatedAt));
        Assert.Equal(("Moved", false), (replaced.Title, replaced.AllDay));
        Assert.Throws<Insolvia.Core.ValidationException>(() => CalendarRules.Replace(original with { RuleId = "rule" }, Parse("""{"title": "x", "start": "2026-03-02"}""")));
    }

    [Fact]
    public void The_feed_token_splits_and_hashes()
    {
        var token = CalendarFeed.FeedToken(Alice, "s3cret");
        var stored = new CalendarToken(Firm, Alice, CalendarFeed.HashSecret("s3cret"), FirmWorld.Seeded);

        Assert.Equal((Alice, "s3cret"), CalendarFeed.Split(token));
        Assert.Null(CalendarFeed.Split("nonsense"));
        Assert.Null(CalendarFeed.Split("not-a-subject.x"));
        Assert.Null(CalendarFeed.Split(Alice + "."));
        Assert.True(CalendarFeed.SecretMatches(stored, "s3cret"));
        Assert.False(CalendarFeed.SecretMatches(stored, "S3CRET"));
        Assert.Equal("4b1a7b1c0f6d0a7b4c1b5a1e9b3d6f6f0c1b2a3d4e5f60718293a4b5c6d7e8f9".Length, stored.SecretHash.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", CalendarFeed.MintSecret());
    }

    [Fact]
    public void The_ics_renders_all_day_and_timed_events_and_skips_dismissed()
    {
        var allDay = CalendarRules.Create(Parse("""{"title": "Hearing", "start": "2026-03-02", "description": "Bring the file, and the plan; both"}"""), new EventScope(Firm, "case-1"), Alice);
        var timed = CalendarRules.Create(Parse("""{"title": "Call", "start": "2026-03-02T15:00:00Z"}"""), new EventScope(Firm), Alice);
        var gone = CalendarRules.SetDismissed(CalendarRules.Create(Parse("""{"title": "Not this one", "start": "2026-03-02"}"""), new EventScope(Firm), Alice), dismissed: true);
        var deadline = allDay with { Id = "d-1", Title = new string('x', 100), RuleId = "r", RuleCitation = "Rule 1", Description = null };

        var ics = CalendarFeed.RenderIcs([allDay, timed, gone, deadline], new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART;VALUE=DATE:20260302\r\nDTEND;VALUE=DATE:20260303", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART:20260302T150000Z\r\nDTEND:20260302T160000Z", ics, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:Bring the file\\, and the plan\\; both", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTAMP:20260101T000000Z", ics, StringComparison.Ordinal);
        Assert.DoesNotContain("Not this one", ics, StringComparison.Ordinal);
        Assert.Contains($"UID:{allDay.Id}@insolvia", ics, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:Rule 1\r\nCATEGORIES:Deadline", ics, StringComparison.Ordinal);
        Assert.EndsWith("END:VCALENDAR\r\n", ics, StringComparison.Ordinal);
        Assert.All(ics.Split("\r\n"), line => Assert.True(line.Length <= 75, line));
        Assert.Contains("\r\n x", ics, StringComparison.Ordinal);
    }

    private static EventDraft Parse(string body) => CalendarRules.Parse(System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject());
}
