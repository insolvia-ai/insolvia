using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Filing.Drivers;
using Insolvia.Filing.Fence;
using Insolvia.Filing.Http;
using Insolvia.Filing.Tests.FakeCourt;
using Insolvia.Filing.Tests.Support;

namespace Insolvia.Filing.Tests;

// test_totp.py: RFC 6238's own SHA-1 vectors (eight digits; PACER's codes
// are the last six), and the fake court's independent implementation.
public sealed class TotpTests
{
    private static readonly string RfcSeed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"; // base32("12345678901234567890")

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void The_rfc_6238_vectors(long moment, string eightDigits) =>
        Assert.Equal(eightDigits[^6..], Totp.At(RfcSeed, DateTimeOffset.FromUnixTimeSeconds(moment)));

    [Fact]
    public void The_worker_and_the_fake_court_agree()
    {
        var seed = "MFXG65DIMVZC2ZTBNNSS243FMVSC2MRQ"; // base32("another-fake-seed-20")
        for (var moment = 0L; moment < 3000; moment += 30)
        {
            Assert.Equal(FakeCmEcf.Code(seed, moment / 30), Totp.At(seed, DateTimeOffset.FromUnixTimeSeconds(moment)));
        }
    }

    [Fact]
    public void A_seed_written_lowercase_or_spaced_reads_the_same() =>
        Assert.Equal(Totp.At(RfcSeed, DateTimeOffset.FromUnixTimeSeconds(59)), Totp.At("gezd gnbv gy3t qojq gezd gnbv gy3t qojq", DateTimeOffset.FromUnixTimeSeconds(59)));
}

// test_fence.py: the allowlist is the code's constant — nothing outside local,
// loopback by literal address locally — and the client refuses before any
// transport is touched; no other source in the host opens a connection.
public sealed class FenceTests
{
    [Theory]
    [InlineData("staging")]
    [InlineData("production")]
    public void Staging_and_production_allow_no_court_host_at_all(string environment) =>
        Assert.Empty(HostFence.For(environment).Allowed);

    [Fact]
    public void Local_allows_loopback_by_literal_address_only_and_an_unknown_environment_nothing()
    {
        Assert.Equal(["127.0.0.1", "::1"], HostFence.For("local").Allowed.Order(StringComparer.Ordinal));
        Assert.Empty(HostFence.For("prod").Allowed);
        Assert.Empty(HostFence.For(string.Empty).Allowed);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8790/login")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("https://127.0.0.1:8443/x?y=1")]
    [InlineData("http://[::1]:8790/")]
    public void The_fake_on_loopback_passes_locally(string url) => Assert.True(HostFence.For("local").Permits(url));

    [Theory]
    [InlineData("local", "http://localhost:8790/")]
    [InlineData("local", "https://ecf.flmb.uscourts.gov/")]
    [InlineData("local", "http://user:pass@127.0.0.1:8790/")]
    [InlineData("local", "http://127.0.0.1@ecf.flmb.uscourts.gov/")]
    [InlineData("local", "ftp://127.0.0.1/")]
    [InlineData("local", "file:///etc/passwd")]
    [InlineData("local", "/relative")]
    [InlineData("local", "http://127.0.0.2/")]
    [InlineData("staging", "https://ecf.flmb.uscourts.gov/")]
    [InlineData("staging", "http://127.0.0.1:8790/")]
    [InlineData("production", "https://ecf.flmb.uscourts.gov/")]
    [InlineData("production", "https://pacer.login.uscourts.gov/")]
    public void Everything_else_is_refused(string environment, string url) =>
        Assert.Throws<HostNotAllowedException>(() => HostFence.For(environment).Check(url));

    [Theory]
    [InlineData("local", "https://ecf.flmb.uscourts.gov/cgi-bin/login.pl?secret=1")]
    [InlineData("production", "https://ecf.flmb.uscourts.gov/")]
    public async Task The_client_refuses_a_court_host_before_any_transport(string environment, string url)
    {
        var transport = new CountingHandler();
        using var client = new FencedHttpClient(HostFence.For(environment), TimeSpan.FromSeconds(1), transport);

        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.SendAsync(new CourtRequest("POST", url, "login=x&password=y"u8.ToArray()), TestContext.Current.CancellationToken));

        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task The_connect_callback_is_the_second_wall()
    {
        // A request the first check would pass cannot reach a socket whose
        // endpoint the fence refuses: the default handler checks again.
        using var handler = FencedHttpClient.SocketsFor(HostFence.For("staging"));
        using var invoker = new HttpMessageInvoker(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9/"), TestContext.Current.CancellationToken));

        Assert.IsType<HostNotAllowedException>(error.InnerException);
    }

    [Fact]
    public void A_refusal_names_the_host_and_nothing_of_the_request()
    {
        var error = Assert.Throws<HostNotAllowedException>(() => HostFence.For("production").Check("https://ecf.flmb.uscourts.gov/login?password=hunter2"));

        Assert.Equal("ecf.flmb.uscourts.gov", error.Host);
        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/login", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_source_but_the_fenced_client_and_the_aws_composition_can_open_a_connection()
    {
        var root = Path.Combine(SourceRoot(), "src", "Insolvia.Filing");
        var network = new Regex(@"\b(HttpClient|HttpMessageInvoker|SocketsHttpHandler|TcpClient|Socket|WebRequest|WebClient|NetworkStream|ClientWebSocket)\b");
        var users = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadLines(path).Any(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && network.IsMatch(line)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

        Assert.Equal(["FilingComposition.cs", "Http/FencedHttpClient.cs"], users);
    }

    [Fact]
    public void The_fake_court_is_never_in_src()
    {
        var root = Path.Combine(SourceRoot(), "src");
        Assert.DoesNotContain(
            Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories),
            path => File.ReadAllText(path).Contains("Tests.FakeCourt", StringComparison.Ordinal));
    }

    internal static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Insolvia.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("no Insolvia.slnx above the test binaries");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

// screens.py: a screen's fingerprint is its structure, not its words; the
// driver's fingerprints are exactly the fake court's pages.
public sealed class ScreenTests
{
    [Fact]
    public void The_fingerprint_is_structure_not_words()
    {
        var page = Screens.Parse("<html><head><title>  PACER   Central Sign-On </title></head><body><p class='message'>Login &amp; failed</p>"
            + "<form action='/login' method='POST'><input name='password'><input name=login><input type=\"hidden\" name=\"t\" value=\"a&amp;b\"></form>"
            + "<p id='case-number'> 6:26-bk-10000 </p><ul id='entries'><li>1 Petition</li><li> 2  Schedules </li></ul></body></html>");

        Assert.Equal("PACER Central Sign-On", page.Title);
        Assert.Equal(["Login & failed"], page.Messages);
        Assert.Equal("post", page.Forms[0].Method);
        Assert.Equal("a&b", page.Forms[0].Hidden["t"]);
        Assert.Equal("6:26-bk-10000", page.ById["case-number"]);
        Assert.Equal(["1 Petition", "2 Schedules"], page.Entries);
        Assert.Equal(new ScreenSpec("PACER Central Sign-On", ("/login", ["login", "password", "t"])), page.Fingerprint);
        Assert.Null(Screens.Recognise(page, FakeCmEcfDriver.Known));
    }

    [Fact]
    public void A_court_message_is_collapsed_and_bounded() =>
        Assert.Equal(300, Screens.CourtText(new string('x', 1000)).Length);

    [Fact]
    public async Task The_drivers_fingerprints_are_the_fake_courts_pages()
    {
        await using var court = await FakeCmEcf.StartAsync();
        using var http = new HttpClient();
        var login = Screens.Parse(await http.GetStringAsync(court.BaseUrl + "/login", TestContext.Current.CancellationToken));

        Assert.Equal("login", Screens.Recognise(login, FakeCmEcfDriver.Known));
    }
}

// test_hand_back.py: every reason the code can stop for has an instruction;
// every outcome-unknown instruction says reconcile first; every hand-back
// lands on the filing-set checklist.
public sealed partial class HandBackTests
{
    [Fact]
    public void Every_reason_raised_in_the_source_has_an_instruction()
    {
        var root = Path.Combine(FenceTests.SourceRoot(), "src", "Insolvia.Filing");
        var source = string.Join("\n", Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains("/obj/", StringComparison.Ordinal)).Select(File.ReadAllText));
        var handBacks = HandBackRaised().Matches(source).Select(m => m.Groups["reason"].Value).ToHashSet(StringComparer.Ordinal);
        var unknowns = UnknownRaised().Matches(source).Select(m => m.Groups["reason"].Value).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(handBacks);
        Assert.NotEmpty(unknowns);
        Assert.All(handBacks, reason => Assert.True(HandBackReasons.HandBack.ContainsKey(reason), reason));
        Assert.All(unknowns, reason => Assert.True(HandBackReasons.OutcomeUnknown.ContainsKey(reason), reason));
        Assert.Contains("host_not_allowed", handBacks);
        Assert.Contains("interrupted_after_submit_mark", unknowns);
    }

    [Fact]
    public void Every_outcome_unknown_instruction_says_reconcile_first() =>
        Assert.All(HandBackReasons.OutcomeUnknown.Values, reason => Assert.Contains("Look the debtor up on the court's own case query before doing anything else", reason.Action, StringComparison.Ordinal));

    [Fact]
    public void Every_hand_back_lands_on_the_filing_set_checklist()
    {
        Assert.All(HandBackReasons.HandBack.Keys, reason => Assert.Equal("packet", HandBackReasons.HandBackNote(reason, Core.Filings.FilingState.Claimed).Link));
        Assert.All(HandBackReasons.HandBack.Where(p => p.Key != "document_mismatch").Select(p => p.Value), reason => Assert.Contains("Nothing reached the court", reason.Action, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"(?:HandBackException|StopException|HandBackAsync|StopBeforeSubmitAsync\(run,)\(?\s*""(?<reason>[a-z_]+)""")]
    private static partial Regex HandBackRaised();

    [GeneratedRegex(@"(?:SubmitUncertainException|OutcomeUnknownAsync)\(\s*""(?<reason>[a-z_]+)""")]
    private static partial Regex UnknownRaised();
}

// test_config.py: a deployed worker is never pointed at a fake, always has
// its kill switch parameter, and takes no local stand-in.
public sealed class ConfigTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Theory]
    [InlineData("staging")]
    [InlineData("production")]
    public void A_deployed_worker_cannot_be_pointed_at_a_fake_must_have_its_switch_and_takes_no_local_stand_in(string environment)
    {
        var parameter = ("FILING_KILL_SWITCH_PARAMETER", $"/insolvia/{environment}/filing/submissions-enabled");
        Assert.Throws<ValidationException>(() => FilingConfig.Load(Env(("INSOLVIA_ENV", environment), parameter, ("FAKE_CMECF_URL", "http://127.0.0.1:8790"))));
        Assert.Throws<ValidationException>(() => FilingConfig.Load(Env(("INSOLVIA_ENV", environment))));
        Assert.Throws<ValidationException>(() => FilingConfig.Load(Env(("INSOLVIA_ENV", environment), parameter, ("FILING_SUBMISSIONS_ENABLED", "true"))));
        Assert.Equal(parameter.Item2, FilingConfig.Load(Env(("INSOLVIA_ENV", environment), parameter)).KillSwitchParameter);
    }

    [Theory]
    [InlineData("https://127.0.0.1:8790")]
    [InlineData("http://localhost:8790")]
    [InlineData("http://ecf.flmb.uscourts.gov")]
    public void The_fake_must_be_on_plain_http_loopback(string url) =>
        Assert.Throws<ValidationException>(() => FilingConfig.Load(Env(("FAKE_CMECF_URL", url))));

    [Fact]
    public void Locally_submissions_are_off_unless_said_otherwise_and_an_unknown_environment_is_refused()
    {
        Assert.False(FilingConfig.Load(Env()).LocalSubmissionsEnabled);
        Assert.True(FilingConfig.Load(Env(("FILING_SUBMISSIONS_ENABLED", "true"))).LocalSubmissionsEnabled);
        Assert.Throws<ValidationException>(() => FilingConfig.Load(Env(("INSOLVIA_ENV", "prod"))));
    }

    [Fact]
    public void The_host_refuses_to_start_without_a_packet_reader_rather_than_claim_what_it_could_only_hand_back()
    {
        var error = Assert.Throws<InvalidOperationException>(() => FilingComposition.Services(FilingConfig.Load(Env()), Env()));
        Assert.Equal(FilingComposition.PacketReaderMissing, error.Message);
    }
}

// test_lambda_handler.py: the deploy smoke event is dropped and writes
// nothing; a real job runs through the worker.
public sealed class LambdaHandlerTests : IAsyncLifetime
{
    private FakeCmEcf _court = null!;
    private FilingWorld _world = null!;

    public async ValueTask InitializeAsync()
    {
        _court = await FakeCmEcf.StartAsync();
        _world = await FilingWorld.CreateAsync(_court);
    }

    public async ValueTask DisposeAsync()
    {
        await _world.DisposeAsync();
        await _court.DisposeAsync();
    }

    [Fact]
    public async Task The_deploy_smoke_event_is_dropped_and_a_real_job_runs_through_the_worker()
    {
        var (_, body) = await _world.ApproveAsync();
        var events = new JsonObject
        {
            ["Records"] = new JsonArray(
                new JsonObject { ["messageId"] = "smoke", ["body"] = "{}" },
                new JsonObject { ["messageId"] = "m-1", ["body"] = body },
                new JsonObject { ["messageId"] = "m-1-again", ["body"] = body }),
        };

        var answer = await FilingProgram.HandleAsync(events.ToJsonString(), (message, token) => FilingWorker.RunAsync(message, _world.Deps(), token), _world.Log, TestContext.Current.CancellationToken);

        Assert.Equal("""{"outcomes":["dropped","filed","noop"]}""", answer.ToJsonString());
        Assert.Equal(1, _court.Submissions);
        Assert.Contains(_world.Log.Lines, line => line.Contains("filing job done", StringComparison.Ordinal));
        Assert.DoesNotContain(_world.Log.Lines, line => line.Contains(_court.Password, StringComparison.Ordinal));
        _ = Encoding.UTF8;
    }
}
