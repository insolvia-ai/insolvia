using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Api.Mail;
using Insolvia.Api.Tests.Support;
using Insolvia.Api.Waitlist;
using Insolvia.Core;
using Insolvia.Data.Waitlist;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_waitlist.py: the submission's validation and <c>POST /v1/waitlist</c>.
/// (Its record-shape tests moved to the store's conformance suite in
/// Insolvia.Data.Tests, where the shape now lives.)
/// </summary>
public sealed class WaitlistTests : IDisposable
{
    private const string ValidBody = """
        {"name": "Ada Lovelace", "firm": "Lovelace & Byron LLP", "email": "ada@lovelace-law.example",
         "currentSoftware": "Best Case", "message": "Two attorneys, chapter 7 heavy.", "host": "www.insolvia.ai"}
        """;

    private readonly FirmWorld _world = new();

    public static TheoryData<string> MalformedEmails => ["not-an-email", "a@b", "a b@c.d", "@example.com"];

    public static TheoryData<string, int> Caps => new()
    {
        { "name", 200 },
        { "firm", 200 },
        { "email", 320 },
        { "currentSoftware", 100 },
        { "message", 2000 },
    };

    public void Dispose() => _world.Dispose();

    // ── Validation ───────────────────────────────────────────────────

    [Fact]
    public void A_valid_submission_is_trimmed_and_parsed()
    {
        var padded = new JsonObject();
        foreach (var (key, value) in Body(ValidBody))
        {
            padded[key] = $"  {value!.GetValue<string>()}  ";
        }

        Assert.Equal(
            new WaitlistSubmission("Ada Lovelace", "Lovelace & Byron LLP", "ada@lovelace-law.example", "Best Case", "Two attorneys, chapter 7 heavy.", "www.insolvia.ai"),
            WaitlistSubmissions.Parse(padded));
    }

    [Fact]
    public void Optional_fields_default_to_empty()
    {
        var submission = WaitlistSubmissions.Parse(Body("""{"name": "Ada", "firm": "Lovelace LLP", "email": "ada@example.com"}"""));

        Assert.Equal(("", "", ""), (submission.CurrentSoftware, submission.Message, submission.Host));
    }

    [Fact]
    public void Missing_required_fields_are_reported_per_field()
    {
        var error = Assert.Throws<FieldValidationException>(() => WaitlistSubmissions.Parse(new JsonObject()));

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["name"] = "Please tell us your name.",
                ["firm"] = "Please tell us your firm's name.",
                ["email"] = "A work email is required.",
            },
            error.Fields);
    }

    [Fact]
    public void Non_string_values_count_as_missing()
    {
        var error = Assert.Throws<FieldValidationException>(() => WaitlistSubmissions.Parse(Body("""{"name": 42, "firm": null, "email": ["a@b.c"]}""")));

        Assert.Equal(["email", "firm", "name"], error.Fields.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(MalformedEmails))]
    public void A_malformed_email_is_rejected(string email)
    {
        var body = Body("""{"name": "Ada", "firm": "LLP"}""");
        body["email"] = email;

        var error = Assert.Throws<FieldValidationException>(() => WaitlistSubmissions.Parse(body));

        Assert.Equal(new Dictionary<string, string> { ["email"] = "That doesn't look like a valid email address." }, error.Fields);
    }

    [Theory]
    [MemberData(nameof(Caps))]
    public void Overlong_fields_are_rejected_with_the_marketing_caps(string field, int cap)
    {
        var body = Body(ValidBody);
        body[field] = field == "email" ? "a@" + new string('x', cap) + ".com" : new string('x', cap + 1);

        var error = Assert.Throws<FieldValidationException>(() => WaitlistSubmissions.Parse(body));

        Assert.Equal(new Dictionary<string, string> { [field] = $"Please keep this under {cap} characters." }, error.Fields);
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        // The honeypot is the marketing action's; an unexpected key must not fail here.
        var body = Body(ValidBody);
        body["website"] = "http://spam.example";
        body["extra"] = 1;

        Assert.Equal("Ada Lovelace", WaitlistSubmissions.Parse(body).Name);
    }

    [Fact]
    public void A_record_is_a_uuid4_stamped_to_the_millisecond()
    {
        var record = WaitlistSubmissions.Create(WaitlistSubmissions.Parse(Body(ValidBody)));

        Assert.Equal(4, Guid.Parse(record.Id).Version);
        Assert.Equal(0, record.SubmittedAt.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(TimeSpan.Zero, record.SubmittedAt.Offset);
    }

    // ── The route ────────────────────────────────────────────────────

    [Fact]
    public async Task Post_waitlist_returns_201_and_stores_the_record()
    {
        var answer = await _world.Post("/v1/waitlist", ValidBody);

        Assert.Equal(201, answer.Status);
        Assert.Equal(["id", "submittedAt"], Answer.Keys(answer.Json));
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$", answer.Str("submittedAt")!);

        var record = Assert.Single(_world.Host.Waitlist.Records);
        Assert.Equal(answer.Str("id"), record.Id);
        Assert.Equal(answer.Str("submittedAt"), Http.FlaskJson.Millis(record.SubmittedAt));
        Assert.Equal("ada@lovelace-law.example", record.Submission.Email);
    }

    [Fact]
    public async Task Post_waitlist_reports_field_errors_as_400()
    {
        var answer = await _world.Post("/v1/waitlist", """{"name": "", "firm": "LLP", "email": "nope"}""");

        Assert.Equal(400, answer.Status);
        Assert.Equal("""{"error":"ValidationError","fields":{"email":"That doesn't look like a valid email address.","name":"Please tell us your name."}}""", answer.Body);
        Assert.Empty(_world.Host.Waitlist.Records);
    }

    [Fact]
    public async Task Post_waitlist_rejects_non_json_bodies()
    {
        var answer = await _world.SendAsync(HttpMethod.Post, "/v1/waitlist", json: "name=Ada", contentType: "text/plain");

        Assert.Equal(400, answer.Status);
        Assert.Equal("ValidationError", answer.Str("error"));
        Assert.Contains("JSON object", answer.Str("message"), StringComparison.Ordinal);
        Assert.Empty(_world.Host.Waitlist.Records);
    }

    [Fact]
    public async Task Post_waitlist_rejects_json_arrays()
    {
        Assert.Equal(400, (await _world.Post("/v1/waitlist", """["not", "an", "object"]""")).Status);
        Assert.Empty(_world.Host.Waitlist.Records);
    }

    [Fact]
    public async Task Post_waitlist_is_public_and_ignores_an_authorization_header()
    {
        var answer = await _world.SendAsync(HttpMethod.Post, "/v1/waitlist", json: ValidBody, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer not-a-token" });

        Assert.Equal(201, answer.Status);
    }

    [Fact]
    public async Task A_body_over_64_kib_is_refused()
    {
        var answer = await _world.Post("/v1/waitlist", "{\"message\": \"" + new string('x', 70_000) + "\"}");

        Assert.Equal(HttpAssert.ErrorBody("ValidationError", "request body exceeds 64 KiB"), answer.Body);
    }

    [Fact]
    public async Task The_log_line_carries_the_id_and_no_submitted_value()
    {
        var answer = await _world.Post("/v1/waitlist", ValidBody);

        var entry = Assert.Single(_world.Host.Logs.In("insolvia_api.api.routes.waitlist"));
        Assert.Equal(answer.Str("id"), entry.Fields["waitlist_id"]);
        Assert.DoesNotContain(_world.Host.Logs.Entries, line => line.Rendered.Contains("lovelace", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();
}

/// <summary>
/// test_unsubscribe.py: the token (what the verifier REFUSES, mostly) and
/// <c>POST /v1/unsubscribe</c>.
/// </summary>
public sealed class UnsubscribeTests : IDisposable
{
    private const string Secret = "test-unsubscribe-secret";
    private const string OtherSecret = "a-different-secret";
    private const string Address = "ada@example.com";

    private FirmWorld? _world;

    public static TheoryData<string> MalformedTokens => ["", "not-a-token", "v1.only-two-parts", "v2.abc.def", "v1.!!!.###", "v1..", "v1.abc.def.ghi"];

    public static TheoryData<string> MalformedBodies => ["{}", """{"token": null}""", """{"token": 42}""", """{"token": ""}""", "[]"];

    public void Dispose() => _world?.Dispose();

    // ── The token ────────────────────────────────────────────────────

    [Fact]
    public void A_token_round_trips_an_address() =>
        Assert.Equal(Address, UnsubscribeTokens.Verify(UnsubscribeTokens.Mint(Address, Secret), Secret));

    [Fact]
    public void Case_and_whitespace_are_normalized() =>
        Assert.Equal(Address, UnsubscribeTokens.Verify(UnsubscribeTokens.Mint("  Ada@Example.COM ", Secret), Secret));

    [Fact]
    public void Tokens_differ_per_address() =>
        Assert.NotEqual(UnsubscribeTokens.Mint(Address, Secret, 1_700_000_000), UnsubscribeTokens.Mint("bob@example.com", Secret, 1_700_000_000));

    [Fact]
    public void A_token_is_the_python_tokens_bytes()
    {
        // Minted by services/api core/unsubscribe.py: mint_token("ada@example.com",
        // secret="test-unsubscribe-secret", issued_at=1700000000).
        Assert.Equal("v1." + B64("1:1700000000:ada@example.com") + ".", UnsubscribeTokens.Mint(Address, Secret, 1_700_000_000)[..(3 + B64("1:1700000000:ada@example.com").Length + 1)]);
        Assert.Equal(Address, UnsubscribeTokens.Verify(UnsubscribeTokens.Mint(Address, Secret, 1_700_000_000), Secret));
    }

    [Fact]
    public void A_token_signed_with_another_secret_is_rejected() =>
        Assert.Equal("unsubscribe token is invalid", Assert.Throws<ValidationException>(() => UnsubscribeTokens.Verify(UnsubscribeTokens.Mint(Address, OtherSecret), Secret)).Message);

    [Fact]
    public void A_swapped_address_is_rejected()
    {
        // The attack the HMAC exists to stop: edit the address inside your own token.
        var parts = UnsubscribeTokens.Mint(Address, Secret).Split('.');
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/') + new string('=', (4 - (parts[1].Length % 4)) % 4)));
        var forged = string.Join(".", parts[0], B64(payload.Replace(Address, "victim@example.com", StringComparison.Ordinal)), parts[2]);

        Assert.Throws<ValidationException>(() => UnsubscribeTokens.Verify(forged, Secret));
    }

    [Theory]
    [MemberData(nameof(MalformedTokens))]
    public void Malformed_tokens_are_rejected(string token) =>
        Assert.Equal("unsubscribe token is invalid", Assert.Throws<ValidationException>(() => UnsubscribeTokens.Verify(token, Secret)).Message);

    [Fact]
    public void An_absurdly_long_token_is_rejected() =>
        Assert.Throws<ValidationException>(() => UnsubscribeTokens.Verify("v1." + new string('a', 5000) + ".b", Secret));

    [Fact]
    public void Minting_without_a_secret_is_refused_and_is_not_the_callers_fault()
    {
        Assert.Throws<UnsubscribeSecretMissingException>(() => UnsubscribeTokens.Mint(Address, ""));
        Assert.False(typeof(ApiException).IsAssignableFrom(typeof(UnsubscribeSecretMissingException)));
    }

    [Fact]
    public void Verifying_without_a_secret_is_refused() =>
        Assert.Throws<UnsubscribeSecretMissingException>(() => UnsubscribeTokens.Verify(UnsubscribeTokens.Mint(Address, Secret), ""));

    [Fact]
    public void An_address_that_would_shift_the_payload_fields_is_refused() =>
        Assert.Equal("address is not a valid unsubscribe subject", Assert.Throws<ValidationException>(() => UnsubscribeTokens.Mint("a:b@example.com", Secret)).Message);

    [Fact]
    public void A_token_does_not_expire() =>
        Assert.Equal(Address, UnsubscribeTokens.Verify(UnsubscribeTokens.Mint(Address, Secret, issuedAt: 1), Secret));

    [Fact]
    public void A_tokens_age_is_reported() =>
        Assert.Equal(60, UnsubscribeTokens.AgeSeconds(UnsubscribeTokens.Mint(Address, Secret, 1_700_000_000), Secret, now: 1_700_000_060));

    [Fact]
    public void The_link_is_on_the_marketing_origin()
    {
        var url = UnsubscribeTokens.Url(Address, Secret, "https://staging-www.insolvia.ai/");

        Assert.StartsWith("https://staging-www.insolvia.ai/unsubscribe?token=v1.", url, StringComparison.Ordinal);
        Assert.Equal(Address, UnsubscribeTokens.Verify(url.Split("token=", 2)[1], Secret));
    }

    // ── The endpoint ─────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_token_suppresses()
    {
        var world = World();

        var answer = await world.Post("/v1/unsubscribe", Token(UnsubscribeTokens.Mint(Address, Secret)));

        HttpAssert.Json((answer.Status, answer.Body, answer.Response), 202, """{"status":"unsubscribed"}""");
        Assert.Equal([(Address, "unsubscribe")], world.Host.Mailer.Suppressed);
    }

    [Fact]
    public async Task It_is_idempotent()
    {
        var world = World();
        var token = Token(UnsubscribeTokens.Mint(Address, Secret));

        var first = await world.Post("/v1/unsubscribe", token);
        var second = await world.Post("/v1/unsubscribe", token);

        Assert.Equal((202, 202), (first.Status, second.Status));
        Assert.Equal([(Address, "unsubscribe"), (Address, "unsubscribe")], world.Host.Mailer.Suppressed);
    }

    [Fact]
    public async Task A_forged_token_is_rejected_without_suppressing()
    {
        var world = World();

        var answer = await world.Post("/v1/unsubscribe", Token(UnsubscribeTokens.Mint(Address, OtherSecret)));

        Assert.Equal(HttpAssert.ErrorBody("ValidationError", "unsubscribe token is invalid"), answer.Body);
        Assert.Empty(world.Host.Mailer.Suppressed);
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task A_malformed_body_is_rejected(string body)
    {
        var world = World();

        Assert.Equal(400, (await world.Post("/v1/unsubscribe", body)).Status);
        Assert.Empty(world.Host.Mailer.Suppressed);
    }

    [Fact]
    public async Task The_response_reveals_nothing_about_the_address()
    {
        var world = World();

        var answer = await world.Post("/v1/unsubscribe", Token(UnsubscribeTokens.Mint(Address, Secret)));

        Assert.DoesNotContain(Address, answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(world.Host.Logs.Entries, entry => entry.Rendered.Contains(Address, StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_fails_loudly_when_no_secret_is_configured()
    {
        var world = World(secret: null);

        var answer = await world.Post("/v1/unsubscribe", Token("v1.a.b"));

        Assert.Equal(500, answer.Status);
        Assert.Equal("""{"error":"InternalError","message":"request failed"}""", answer.Body);
        Assert.Empty(world.Host.Mailer.Suppressed);
    }

    private static string Token(string token) => $$"""{"token": "{{token}}"}""";

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private FirmWorld World(string? secret = Secret) =>
        _world = new FirmWorld(new Dictionary<string, string?> { ["UNSUBSCRIBE_SECRET"] = secret });
}

/// <summary>test_mailer_client.py's memory half: what the in-memory mailer records.</summary>
public sealed class MemoryMailerTests
{
    private static readonly OutboundEmail Welcome = new("welcome", "transactional", "ada@example.com", "Welcome to Insolvia", "<p>Hi</p>", "Hi");

    [Fact]
    public async Task It_records_the_send()
    {
        var mailer = new MemoryMailer();

        await mailer.SendAsync(Welcome, "key-123", HttpAssert.Ct);

        Assert.Equal([(Welcome, "key-123")], mailer.Sent);
    }

    [Fact]
    public async Task It_records_multiple_sends_in_order()
    {
        var mailer = new MemoryMailer();

        await mailer.SendAsync(Welcome, "key-1", HttpAssert.Ct);
        await mailer.SendAsync(Welcome, "key-2", HttpAssert.Ct);

        Assert.Equal(["key-1", "key-2"], mailer.Sent.Select(sent => sent.IdempotencyKey));
    }

    [Fact]
    public async Task It_records_the_suppression()
    {
        var mailer = new MemoryMailer();

        await mailer.SuppressAsync("ada@example.com", "unsubscribe", HttpAssert.Ct);

        Assert.Equal([("ada@example.com", "unsubscribe")], mailer.Suppressed);
    }
}
