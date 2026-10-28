using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Insolvia.Filing.Tests.FakeCourt;

/// <summary>
/// The fake CM/ECF (services/filing/fake/fake_cmecf, ported) — a TEST
/// FIXTURE, never in src/ and never in an image: PACER-style sign-in with a
/// REAL TOTP check, the Open BK Case screens, upload, review, the final
/// submit and a confirmation with a case number, plus the fault modes the
/// worker must survive. Kestrel in-process on 127.0.0.1 — the only host the
/// local fence lets through.
/// </summary>
/// <remarks>
/// <para><b>The account</b>: one fake attorney, login <c>FAKE-ECF-USER</c>, a
/// password and a TOTP seed minted from the CSPRNG at start. Nothing real.</para>
/// <para><b>Counting</b>: <see cref="Submissions"/> counts final-submit
/// requests RECEIVED — incremented before anything else the handler does, so
/// a submit whose response is lost still counts. Every never-twice test
/// asserts on it.</para>
/// </remarks>
public sealed class FakeCmEcf : IAsyncDisposable
{
    /// <summary>The fault modes (one per run).</summary>
    public static readonly IReadOnlyList<string> Faults =
        ["none", "bad_login", "totp_rejected", "slow", "upload_500", "timeout_after_submit", "duplicate_case", "unexpected_screen", "receipt_missing"];

    /// <summary>The fake attorney's login.</summary>
    public const string Login = "FAKE-ECF-USER";

    private const string SessionCookie = "FAKEECF";

    private const string LoginForm =
        "<form action='/login' method='post'><input name='login'><input name='password' type='password'>"
        + "<label><input name='redaction_compliance' type='checkbox'> I comply with the redaction rules</label><button>Sign in</button></form>";

    private const string MfaForm = "<form action='/mfa' method='post'><input name='otp'><button>Verify</button></form>";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly List<string> _caseNumbers = [];
    private readonly WebApplication _app;
    private string _fault = "none";

    private FakeCmEcf(WebApplication app, TimeSpan slow, TimeSpan hang)
    {
        _app = app;
        SlowFor = slow;
        HangFor = hang;
        Password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        TotpSeed = Base32(RandomNumberGenerator.GetBytes(20));
    }

    /// <summary>The fake attorney's password.</summary>
    public string Password { get; }

    /// <summary>The fake attorney's TOTP seed (base32).</summary>
    public string TotpSeed { get; }

    /// <summary>How long the <c>slow</c> fault delays each response.</summary>
    public TimeSpan SlowFor { get; }

    /// <summary>How long <c>timeout_after_submit</c> holds the submit's response.</summary>
    public TimeSpan HangFor { get; }

    /// <summary>Where it listens: <c>http://127.0.0.1:&lt;port&gt;</c>.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Final-submit requests received.</summary>
    public int Submissions { get; private set; }

    /// <summary>Documents accepted.</summary>
    public int Uploads { get; private set; }

    /// <summary>Case Upload files (Debtor.txt) the court ACCEPTED — the count only; the bytes are never kept.</summary>
    public int CaseUploads { get; private set; }

    /// <summary>Completed sign-ins.</summary>
    public int SignIns { get; private set; }

    /// <summary>The case numbers it issued.</summary>
    public IReadOnlyList<string> CaseNumbers
    {
        get
        {
            lock (_gate)
            {
                return [.. _caseNumbers];
            }
        }
    }

    /// <summary>The current fault.</summary>
    public string Fault
    {
        get
        {
            lock (_gate)
            {
                return _fault;
            }
        }

        set
        {
            if (!Faults.Contains(value))
            {
                throw new ArgumentException($"unknown fault '{value}'", nameof(value));
            }

            lock (_gate)
            {
                _fault = value;
            }
        }
    }

    /// <summary>Start a court on 127.0.0.1, an ephemeral port.</summary>
    public static async Task<FakeCmEcf> StartAsync(TimeSpan? slow = null, TimeSpan? hang = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var court = new FakeCmEcf(app, slow ?? TimeSpan.FromSeconds(2), hang ?? TimeSpan.FromSeconds(2));
        app.Run(court.HandleAsync);
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        court.BaseUrl = address.Replace("localhost", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/');
        return court;
    }

    /// <summary>The independent RFC 6238 check (written apart from the worker's, so the two cross-check).</summary>
    public static string Code(string seed, long counter)
    {
        var key = FromBase32(seed);
        var message = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(message);
        }

#pragma warning disable CA5350 // RFC 6238 is HMAC-SHA1, as the court issues it.
        var mac = HMACSHA1.HashData(key, message);
#pragma warning restore CA5350
        var offset = mac[19] & 0xF;
        var code = (((mac[offset] & 0x7F) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3]) % 1_000_000;
        return code.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Reset the counters.</summary>
    public void ResetCounts()
    {
        lock (_gate)
        {
            Submissions = 0;
            Uploads = 0;
            CaseUploads = 0;
            SignIns = 0;
            _caseNumbers.Clear();
            _sessions.Clear();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static byte[] Page(string title, string body) =>
        Encoding.UTF8.GetBytes($"<!doctype html><html><head><meta charset='utf-8'><title>{WebUtility.HtmlEncode(title)}</title></head><body><h1>{WebUtility.HtmlEncode(title)}</h1>{body}</body></html>");

    private static string Message(string text) => $"<p class='message'>{WebUtility.HtmlEncode(text)}</p>";

    private static async Task SendAsync(HttpContext context, int status, byte[] body, string contentType = "text/html; charset=utf-8")
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body).ConfigureAwait(false);
    }

    private static Task RedirectAsync(HttpContext context, string location, string? cookie = null)
    {
        context.Response.StatusCode = 303;
        context.Response.Headers.Location = location;
        if (cookie is not null)
        {
            context.Response.Headers.SetCookie = cookie;
        }

        return Task.CompletedTask;
    }

    private Session? SessionOf(HttpContext context)
    {
        foreach (var part in context.Request.Headers.Cookie.ToString().Split(';'))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length == 2 && pair[0] == SessionCookie)
            {
                return _sessions.GetValueOrDefault(pair[1]);
            }
        }

        return null;
    }

    private bool VerifyOtp(string code)
    {
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        return new[] { -1, 0, 1 }.Any(drift => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(Code(TotpSeed, step + drift))));
    }

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path == "/__fake/state")
        {
            await SendAsync(context, 200, Encoding.UTF8.GetBytes(new JsonObject { ["fault"] = Fault, ["submissions"] = Submissions, ["caseNumbers"] = new JsonArray([.. CaseNumbers.Select(n => (JsonNode)n)]) }.ToJsonString()), "application/json").ConfigureAwait(false);
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method) && path == "/submit")
        {
            await SubmitAsync(context).ConfigureAwait(false);
            return;
        }

        if (Fault == "slow")
        {
            await Task.Delay(SlowFor).ConfigureAwait(false);
        }

        if (HttpMethods.IsGet(context.Request.Method))
        {
            await GetAsync(context, path).ConfigureAwait(false);
            return;
        }

        switch (path)
        {
            case "/login":
                await LoginAsync(context).ConfigureAwait(false);
                break;
            case "/mfa":
                await MfaAsync(context).ConfigureAwait(false);
                break;
            case "/open-case":
                await OpenCaseAsync(context).ConfigureAwait(false);
                break;
            case "/upload":
                await UploadAsync(context).ConfigureAwait(false);
                break;
            default:
                await SendAsync(context, 404, Page("Not Found", string.Empty)).ConfigureAwait(false);
                break;
        }
    }

    private async Task GetAsync(HttpContext context, string path)
    {
        if (path == "/login")
        {
            await SendAsync(context, 200, Page("PACER Central Sign-On", LoginForm)).ConfigureAwait(false);
            return;
        }

        byte[] page;
        lock (_gate)
        {
            var session = SessionOf(context);
            if (path == "/mfa" && session is not null)
            {
                page = Page("Multifactor Authentication", MfaForm);
            }
            else if (session is null || session.Stage != "in")
            {
                context.Response.StatusCode = 303;
                context.Response.Headers.Location = "/login";
                return;
            }
            else
            {
                page = path switch
                {
                    "/menu" => Page("CM/ECF Bankruptcy - Main Menu", "<form action='/open-case' method='get'><button>Open BK Case</button></form>"),
                    "/notice" => Page("Important Notice", Message("Updated terms of use. Acknowledge to continue.") + "<form action='/notice' method='post'><input name='acknowledge' type='checkbox'></form>"),
                    "/open-case" => Page("Open Bankruptcy Case", "<form action='/open-case' method='post'><select name='chapter'><option>7</option></select><input name='office'><select name='joint'><option>no</option><option>yes</option></select><button>Next</button></form>"),
                    "/upload" when session.Opened => UploadPage(session),
                    "/review" when session.Opened => Page(
                        "Review Filing",
                        "<ul id='entries'>" + string.Concat(session.Uploads.Select(u => $"<li>{u.Position}|{WebUtility.HtmlEncode(u.Name)}|{u.Digest}</li>")) + "</ul>"
                            + $"<form action='/submit' method='post'><input type='hidden' name='token' value='{session.Token}'><button>Submit</button></form>"),
                    _ => [],
                };
            }
        }

        await SendAsync(context, page.Length == 0 ? 404 : 200, page.Length == 0 ? Page("Not Found", string.Empty) : page).ConfigureAwait(false);
    }

    private static byte[] UploadPage(Session session, string? message = null) => Page(
        "Upload Documents",
        (message is null ? string.Empty : Message(message))
            + "<ol id='uploaded'>" + string.Concat(session.Uploads.Select(u => $"<li>{u.Position}. {WebUtility.HtmlEncode(u.Name)} ({u.Size} bytes)</li>")) + "</ol>"
            + "<form action='/upload' method='post' enctype='multipart/form-data'><input name='position'><input name='event'><input name='file_name'><input name='file' type='file'><button>Upload</button></form>"
            + "<form action='/review' method='get'><button>Continue</button></form>");

    private async Task LoginAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
        string token;
        lock (_gate)
        {
            var good = _fault != "bad_login"
                && form["login"] == Login
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(form["password"].ToString()), Encoding.UTF8.GetBytes(Password))
                && form["redaction_compliance"] == "on";
            if (!good)
            {
                token = string.Empty;
            }
            else
            {
                token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                _sessions[token] = new Session();
            }
        }

        if (token.Length == 0)
        {
            await SendAsync(context, 200, Page("PACER Central Sign-On", Message("Login failed: invalid username or password.") + LoginForm)).ConfigureAwait(false);
            return;
        }

        await RedirectAsync(context, "/mfa", $"{SessionCookie}={token}; HttpOnly").ConfigureAwait(false);
    }

    private async Task MfaAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
        string? target;
        lock (_gate)
        {
            var session = SessionOf(context);
            if (session is null)
            {
                target = "/login";
            }
            else if (_fault == "totp_rejected" || !VerifyOtp(form["otp"].ToString()))
            {
                target = null;
            }
            else
            {
                session.Stage = "in";
                SignIns++;
                target = _fault == "unexpected_screen" ? "/notice" : "/menu";
            }
        }

        if (target is null)
        {
            await SendAsync(context, 200, Page("Multifactor Authentication", Message("The code is not valid.") + MfaForm)).ConfigureAwait(false);
            return;
        }

        await RedirectAsync(context, target).ConfigureAwait(false);
    }

    private async Task OpenCaseAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
        byte[]? page = null;
        string? target = null;
        lock (_gate)
        {
            var session = SessionOf(context);
            if (session is null || session.Stage != "in")
            {
                target = "/login";
            }
            else if (_fault == "duplicate_case")
            {
                page = Page("Possible Duplicate Case", Message("A case was filed for a debtor with the same identifiers within the last 30 days.") + "<form action='/open-case/continue' method='post'><input name='proceed' type='checkbox'></form>");
            }
            else if (form["chapter"] != "7" || string.IsNullOrEmpty(form["office"]))
            {
                page = Page("Open Bankruptcy Case", Message("Chapter and office are required."));
            }
            else
            {
                session.Opened = true;
                target = "/upload";
            }
        }

        if (page is not null)
        {
            await SendAsync(context, 200, page).ConfigureAwait(false);
            return;
        }

        await RedirectAsync(context, target!).ConfigureAwait(false);
    }

    private async Task UploadAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
        if (Fault == "upload_500")
        {
            await SendAsync(context, 500, "Internal Server Error"u8.ToArray(), "text/plain").ConfigureAwait(false);
            return;
        }

        byte[] content = [];
        if (form.Files.Count > 0)
        {
            using var stream = new MemoryStream();
            await form.Files[0].CopyToAsync(stream).ConfigureAwait(false);
            content = stream.ToArray();
        }

        var caseUpload = form["event"].ToString() == "case_upload";
        var refusal = caseUpload ? CaseUploadRefusal(content) : null;
        byte[] page;
        lock (_gate)
        {
            var session = SessionOf(context);
            if (session is null || !session.Opened)
            {
                context.Response.StatusCode = 303;
                context.Response.Headers.Location = "/login";
                return;
            }

            if (refusal is not null)
            {
                // The court's own refusal: nothing is accepted, the page says why.
                page = UploadPage(session, refusal);
            }
            else
            {
                if (caseUpload)
                {
                    CaseUploads++;
                }

                session.Uploads.Add((int.Parse(form["position"].ToString(), System.Globalization.CultureInfo.InvariantCulture), form["file_name"].ToString(), Convert.ToHexStringLower(SHA256.HashData(content)), content.Length));
                Uploads++;
                page = UploadPage(session);
            }
        }

        await SendAsync(context, 200, page).ConfigureAwait(false);
    }

    /// <summary>The statistics-record field counts NextGen 1.5.4-1.7.1 accept (the AO's Case Upload spec, ADR 0024 source S22, pp. 11-12).</summary>
    public static readonly IReadOnlyList<int> CaseUploadStatCounts = [73, 78, 80];

    /// <summary>The court's own words when the statistics record's count is wrong.</summary>
    public const string CaseUploadWrongCount =
        "The statistics record (type \"stat\") in the case information file (Debtor.txt) does not have the correct number of data items. "
        + "Be sure you are using the correct Case Upload specification for this court.";

    /// <summary>
    /// What the fake court says about a Debtor.txt, or null to accept it — the
    /// checks a court makes before it opens anything: ASCII, a first
    /// <c>stat</c> record with an accepted field count, and one or two
    /// <c>debt</c> records each carrying an nnn-nn-nnnn tax id. Not the spec
    /// validator (Insolvia.Forms' <c>CaseUploadSpecs</c> is that); a court's
    /// own refusal.
    /// </summary>
    public static string? CaseUploadRefusal(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Any(b => b > 0x7F))
        {
            return "The case information file (Debtor.txt) could not be read.";
        }

        var lines = Encoding.ASCII.GetString(content).Split('\n');
        if (!lines[0].StartsWith("stat|", StringComparison.Ordinal) || !CaseUploadStatCounts.Contains(lines[0].Split('|').Length))
        {
            return CaseUploadWrongCount;
        }

        var debtors = lines.Where(line => line.StartsWith("debt|", StringComparison.Ordinal)).Select(line => line.Split('|')).ToList();
        if (debtors.Count is not (1 or 2))
        {
            return "The case information file (Debtor.txt) has no debtor record.";
        }

        foreach (var debtor in debtors)
        {
            var ssn = debtor.Length > 7 ? debtor[7] : string.Empty;
            if (ssn.Length != 11 || !ssn.Replace("-", string.Empty, StringComparison.Ordinal).All(char.IsAsciiDigit) || ssn[3] != '-' || ssn[6] != '-')
            {
                return "A debtor record's SSN/ITIN is not in nnn-nn-nnnn format.";
            }
        }

        return null;
    }

    private async Task SubmitAsync(HttpContext context)
    {
        // COUNTED FIRST — before the form is read, before the fault, so a
        // submit whose response never arrives still counts.
        string fault;
        lock (_gate)
        {
            Submissions++;
            fault = _fault;
        }

        var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
        string number;
        string docs;
        lock (_gate)
        {
            var session = SessionOf(context);
            if (session is null || form["token"] != session.Token)
            {
                number = string.Empty;
                docs = string.Empty;
            }
            else
            {
                session.Submitted = true;
                number = $"6:26-bk-{10000 + _caseNumbers.Count:D5}";
                _caseNumbers.Add(number);
                docs = string.Concat(session.Uploads.Select(u => $"<li>{u.Position} {WebUtility.HtmlEncode(u.Name)}</li>"));
            }
        }

        if (number.Length == 0)
        {
            await SendAsync(context, 200, Page("Session Expired", string.Empty)).ConfigureAwait(false);
            return;
        }

        if (fault == "timeout_after_submit")
        {
            await Task.Delay(HangFor).ConfigureAwait(false);
        }

        var filedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var caseNumber = fault == "receipt_missing" ? string.Empty : $"<p id='case-number'>{number}</p>";
        await SendAsync(
            context,
            200,
            Page(
                "Notice of Bankruptcy Case Filing",
                caseNumber + $"<p id='filed-at'>{filedAt}</p><ul id='entries'>{docs}</ul>"
                    + $"<p id='receipt-number'>FAKE-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}</p><p id='fee-due'>$338.00</p>"
                    + "<form action='/payment' method='get'><button>Pay filing fee</button></form>")).ConfigureAwait(false);
    }

    private static string Base32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(buffer >> bits) & 31]);
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    private static byte[] FromBase32(string seed)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in seed.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. output];
    }

    private sealed class Session
    {
        public string Stage { get; set; } = "mfa";

        public bool Opened { get; set; }

        public List<(int Position, string Name, string Digest, int Size)> Uploads { get; } = [];

        public string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

        public bool Submitted { get; set; }
    }
}
