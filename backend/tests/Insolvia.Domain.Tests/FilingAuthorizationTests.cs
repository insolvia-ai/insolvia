using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Auth;
using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.Memory;

namespace Insolvia.Core.Tests;

/// <summary>An attorney's signed filing authorization, translated from test_filing_authorization.py.</summary>
public sealed class FilingAuthorizationTests
{
    private const string Firm = "firm-0001";
    private const string Attorney = "00000000-0000-4000-8000-00000000a771";
    private const string OtherAttorney = "00000000-0000-4000-8000-00000000b771";
    private const long SignedInAt = 4_102_444_800; // 2100-01-01T00:00:00Z

    private readonly MemoryFilingAuthorizationStore _store = new();
    private readonly MemoryAccessLog _log = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Digest => FilingAuthorizationRules.TextDigest(FilingAuthorizationRules.AuthorizationText());

    private static JsonObject SignatureBody(string? version = null, string? digest = null) =>
        new() { ["text_version"] = version ?? FilingAuthorizationRules.TextVersion, ["text_digest"] = digest ?? Digest };

    private Task<FilingAuthorization> SignAsync(JsonNode? payload = null, long? authenticatedAt = SignedInAt, long now = SignedInAt + 60, string attorney = Attorney) =>
        FilingAuthorizationRules.SignAsync(payload ?? SignatureBody(), Firm, attorney, authenticatedAt, DateTimeOffset.FromUnixTimeSeconds(now), _store, _log, cancellationToken: Ct);

    // ── The text ────────────────────────────────────────────────────

    [Fact]
    public void The_current_text_ships_in_the_assembly_and_the_digest_is_of_its_bytes()
    {
        using var stream = typeof(FilingAuthorizationRules).Assembly.GetManifestResourceStream($"Insolvia.Core.Agreements/filing-authorization/{FilingAuthorizationRules.TextVersion}.txt")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var shipped = buffer.ToArray();
        Assert.Equal(shipped, System.Text.Encoding.UTF8.GetBytes(FilingAuthorizationRules.AuthorizationText()));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(shipped)), Digest);
    }

    [Fact]
    public void The_text_is_marked_as_an_unreviewed_draft()
    {
        var text = FilingAuthorizationRules.AuthorizationText();
        Assert.StartsWith("DRAFT", text, StringComparison.Ordinal);
        Assert.Contains("not legal advice", text, StringComparison.Ordinal);
        Assert.EndsWith("-draft", FilingAuthorizationRules.TextVersion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("approved that specific filing")]
    [InlineData("signing in again at the time of approval")]
    [InlineData("withdraw this authorization at any time")]
    [InlineData("immediately destroys every court login")]
    [InlineData("forbid a filer from sharing their login")]
    [InlineData("PACER terms of use")]
    public void The_text_says_what_the_adr_requires_it_to_say(string phrase) =>
        Assert.Contains(phrase, FilingAuthorizationRules.AuthorizationText(), StringComparison.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData("../../errors")]
    [InlineData("2026-10-07.txt")]
    [InlineData("UPPER")]
    public void A_version_that_is_not_a_name_is_refused(string version) =>
        Assert.Contains("not an authorization text version", Assert.Throws<ArgumentException>(() => FilingAuthorizationRules.AuthorizationText(version)).Message, StringComparison.Ordinal);

    // ── Signing ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_fresh_sign_in_over_the_current_text_records_the_signature()
    {
        var signed = await SignAsync();
        Assert.Equal((Attorney, FilingAuthorizationRules.TextVersion, Digest, SignedInAt), (signed.AttorneyId, signed.TextVersion, signed.TextDigest, signed.AuthenticatedAt));
        Assert.Equal(signed, await _store.GetCurrentAsync(Firm, Attorney, Ct));
        Assert.Equal(signed, _store.History[signed.AuthorizationId]);
        Assert.True(FilingAuthorizationRules.IsCurrent(signed, Firm, Attorney));
    }

    [Fact]
    public async Task Signing_is_logged_under_the_signature()
    {
        var signed = await SignAsync();
        var recorded = Assert.Single(_log.Events);
        Assert.Equal(AccessAction.AuthorizationSign, recorded.Action);
        Assert.Equal((signed.AuthorizationId, Attorney), (recorded.AuthorizationId, recorded.Principal));
        Assert.Equal($"AUTHORIZATION#{signed.AuthorizationId}", recorded.SubjectKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SignedInAt - FilingAuthorizationRules.SignatureMaxAgeSeconds)]
    public async Task A_stale_sign_in_cannot_sign(long? authenticatedAt)
    {
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => SignAsync(authenticatedAt: authenticatedAt, now: SignedInAt + 1));
        Assert.Null(await _store.GetCurrentAsync(Firm, Attorney, Ct));
        Assert.Empty(_log.Events);
    }

    [Theory]
    [InlineData("2026-01-01-older", null)]
    [InlineData(null, "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task A_text_other_than_the_one_that_ships_cannot_be_signed(string? version, string? digest)
    {
        await Assert.ThrowsAsync<ConflictException>(() => SignAsync(SignatureBody(version, digest)));
        Assert.Null(await _store.GetCurrentAsync(Firm, Attorney, Ct));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"text_version": "2026-10-07-draft"}""")]
    [InlineData("\"x\"")]
    public async Task A_malformed_body_is_a_validation_error(string payload) =>
        await Assert.ThrowsAsync<ValidationException>(() => SignAsync(Body.Value(payload)));

    [Fact]
    public async Task Signing_twice_is_a_conflict()
    {
        await SignAsync();
        await Assert.ThrowsAsync<ConflictException>(() => SignAsync());
    }

    [Fact]
    public async Task One_attorneys_signature_is_not_anothers()
    {
        var signed = await SignAsync();
        Assert.Null(await _store.GetCurrentAsync(Firm, OtherAttorney, Ct));
        Assert.False(FilingAuthorizationRules.IsCurrent(signed, Firm, OtherAttorney));
        Assert.False(FilingAuthorizationRules.IsCurrent(signed, "firm-0002", Attorney));
    }

    [Fact]
    public async Task A_record_whose_digest_no_longer_matches_the_text_is_not_current()
    {
        var signed = await SignAsync();
        Assert.False(FilingAuthorizationRules.IsCurrent(signed with { TextDigest = new string('f', 64) }, Firm, Attorney));
    }

    // test_the_status_view_is_pinned (the JSON view) and test_the_item_round_trips
    // (the item shape): dropped. The two FakeDynamo tests' contract — the
    // current item conditional on what was read, a replaced signature
    // superseded, a withdrawal only of the current one — is the memory
    // store's, and 18.10's store is held to it:

    [Fact]
    public async Task The_store_withdraws_only_the_current_signature_and_keeps_its_history()
    {
        var signed = await SignAsync();
        Assert.True(await _store.WithdrawAsync(signed, DateTimeOffset.FromUnixTimeSeconds(SignedInAt + 300), Ct));
        Assert.Null(await _store.GetCurrentAsync(Firm, Attorney, Ct));
        Assert.Equal(AuthorizationStatus.Withdrawn, _store.History[signed.AuthorizationId].Status);
        Assert.False(await _store.WithdrawAsync(signed, DateTimeOffset.FromUnixTimeSeconds(SignedInAt + 360), Ct));
    }

    [Fact]
    public async Task The_store_refuses_a_lost_race_and_supersedes_what_it_replaced()
    {
        var first = new FilingAuthorization("a1", Firm, Attorney, FilingAuthorizationRules.TextVersion, Digest, DateTimeOffset.FromUnixTimeSeconds(SignedInAt), SignedInAt);
        await _store.PutCurrentAsync(first, null, Ct);
        var second = first with { AuthorizationId = "a2" };
        await Assert.ThrowsAsync<ConflictException>(() => _store.PutCurrentAsync(second, null, Ct));
        await _store.PutCurrentAsync(second, first, Ct);
        Assert.Equal(AuthorizationStatus.Superseded, _store.History["a1"].Status);
    }

    [Fact]
    public void The_authorization_statuses_keep_their_wire_strings() =>
        Assert.Equal(["signed", "superseded", "withdrawn"], Wire.Names<AuthorizationStatus>());
}
