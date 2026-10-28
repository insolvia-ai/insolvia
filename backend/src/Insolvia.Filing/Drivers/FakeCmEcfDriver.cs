using System.Collections.Frozen;
using System.Text;
using Insolvia.Filing.Http;

namespace Insolvia.Filing.Drivers;

/// <summary>
/// The driver for the fake CM/ECF (from <c>drivers/fake.py</c>) — the one
/// court a laptop has, and the reference shape PR 10's real drivers follow.
/// A real driver in every way that matters: every screen it acts on is
/// fingerprinted (<see cref="Known"/>), any other is a stop; each redirect is
/// followed by hand through the fenced client; the court's own list of
/// uploads is checked against what was sent before the submit; and anything
/// but a recognised confirmation after the final submit is
/// <see cref="SubmitUncertainException"/>. It never retries.
/// </summary>
/// <remarks>
/// Composed only where the fake exists (<c>INSOLVIA_ENV=local</c> with
/// <c>FAKE_CMECF_URL</c>), and even then it reaches nothing but loopback —
/// the fence is the second wall. <paramref name="caseUpload"/> picks the
/// fake court's configuration: <c>false</c> (the default here) is the
/// screen-entry court, which takes no Debtor.txt; <c>true</c> asks for the
/// Case Upload file, which the worker hands back (<c>case_upload_invalid</c>)
/// until ADR 0024 PR 9's builder is ported (#530 report).
/// </remarks>
public sealed class FakeCmEcfDriver(string baseUrl, bool caseUpload = false) : ICourtDriver
{
    /// <summary>The recorded driver id.</summary>
    public const string Id = "fake-cmecf/1";

    /// <summary>The fingerprints — every screen this driver acts on. The fake's markup and these move together.</summary>
    public static readonly FrozenDictionary<string, ScreenSpec> Known = new Dictionary<string, ScreenSpec>(StringComparer.Ordinal)
    {
        ["login"] = new("PACER Central Sign-On", ("/login", ["login", "password", "redaction_compliance"])),
        ["mfa"] = new("Multifactor Authentication", ("/mfa", ["otp"])),
        ["menu"] = new("CM/ECF Bankruptcy - Main Menu", ("/open-case", [])),
        ["open_case"] = new("Open Bankruptcy Case", ("/open-case", ["chapter", "joint", "office"])),
        ["duplicate"] = new("Possible Duplicate Case", ("/open-case/continue", ["proceed"])),
        ["upload"] = new("Upload Documents", ("/upload", ["event", "file", "file_name", "position"]), ("/review", [])),
        ["review"] = new("Review Filing", ("/submit", ["token"])),
        ["confirmation"] = new("Notice of Bankruptcy Case Filing", ("/payment", [])),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly string _base = baseUrl.TrimEnd('/') + "/";

    /// <inheritdoc />
    public string DriverId => Id;

    /// <inheritdoc />
    public IReadOnlyList<string> BaseUrls => [_base];

    /// <inheritdoc />
    public bool CaseUpload { get; } = caseUpload;

    /// <inheritdoc />
    public ICourtSession Start(ICourtHttpClient http) => new Session(_base, http);

    private sealed class Session(string baseUrl, ICourtHttpClient http) : ICourtSession
    {
        private const int MaxRedirects = 5;
        private string? _token;

        public async Task SignInAsync(string login, string password, Func<string> totp, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(totp);
            Expect(await GetAsync("/login", cancellationToken).ConfigureAwait(false), "login");
            var (name, screen) = Expect(
                await PostAsync(
                    "/login",
                    [
                        ("login", login),
                        ("password", password),

                        // The redaction-compliance acknowledgement PACER asks of every
                        // filer at sign-in — given in the authorization they signed.
                        ("redaction_compliance", "on"),
                    ],
                    cancellationToken).ConfigureAwait(false),
                "mfa",
                "login");
            if (name == "login")
            {
                throw new HandBackException("sign_in_failed", Said(screen));
            }

            (name, screen) = Expect(await PostAsync("/mfa", [("otp", totp())], cancellationToken).ConfigureAwait(false), "menu", "mfa");
            if (name == "mfa")
            {
                throw new HandBackException("mfa_rejected", Said(screen));
            }
        }

        public async Task OpenCaseAsync(CaseOpening opening, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(opening);
            Expect(await GetAsync("/open-case", cancellationToken).ConfigureAwait(false), "open_case");
            var (name, screen) = Expect(
                await PostAsync(
                    "/open-case",
                    [("chapter", opening.Chapter.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("office", opening.Division), ("joint", opening.Joint ? "yes" : "no")],
                    cancellationToken).ConfigureAwait(false),
                "upload",
                "duplicate");
            if (name == "duplicate")
            {
                throw new HandBackException("duplicate_case", Said(screen));
            }
        }

        public async Task UploadAsync(IReadOnlyList<FilingFile> files, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(files);
            foreach (var file in files)
            {
                var boundary = Guid.NewGuid().ToString("N");
                var body = Multipart(
                    boundary,
                    [("position", file.Position.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("event", file.Handling), ("file_name", file.FileName)],
                    ("file", file.FileName, file.Content));
                var (_, screen) = Expect(
                    await SendAsync(new CourtRequest("POST", Join(baseUrl, "upload"), body, $"multipart/form-data; boundary={boundary}"), cancellationToken).ConfigureAwait(false),
                    "upload");
                if (screen.Messages.Count > 0)
                {
                    // The court refused this document: stop, and say what it
                    // said — never alter the file and try again.
                    throw new HandBackException("court_message", Said(screen));
                }
            }

            var (_, review) = Expect(await GetAsync("/review", cancellationToken).ConfigureAwait(false), "review");
            var expected = files.Select(f => $"{f.Position}|{f.FileName}|{f.Sha256}").ToList();
            if (!review.Entries.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new HandBackException("upload_mismatch");
            }

            if (!review.Forms[0].Hidden.TryGetValue("token", out var token) || token.Length == 0)
            {
                throw new HandBackException("unrecognised_screen", "no submit token");
            }

            _token = token;
        }

        public async Task<CourtConfirmation> FinalSubmitAsync(CancellationToken cancellationToken = default)
        {
            // A programming error, caught before the request: nothing sent.
            var token = _token ?? throw new InvalidOperationException("final_submit before a reviewed upload");
            CourtResponse response;
            try
            {
                response = await http.SendAsync(new CourtRequest("POST", Join(baseUrl, "submit"), Form([("token", token)]), "application/x-www-form-urlencoded"), cancellationToken).ConfigureAwait(false);
            }
            catch (CourtTimeoutException error)
            {
                throw new SubmitUncertainException("submit_timeout", null, error);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new SubmitUncertainException("submit_error", null, error);
            }

            if (response.Status != 200)
            {
                throw new SubmitUncertainException("submit_error", $"HTTP {response.Status}");
            }

            var screen = Screens.Parse(response.Text);
            if (Screens.Recognise(screen, new Dictionary<string, ScreenSpec> { ["confirmation"] = Known["confirmation"] }) is null)
            {
                throw new SubmitUncertainException("unrecognised_confirmation", Screens.CourtText(screen.Title));
            }

            var caseNumber = screen.ById.GetValueOrDefault("case-number", string.Empty).Trim();
            var filedAt = screen.ById.GetValueOrDefault("filed-at", string.Empty).Trim();
            if (caseNumber.Length == 0 || filedAt.Length == 0)
            {
                throw new SubmitUncertainException("receipt_missing");
            }

            return new CourtConfirmation(
                Screens.CourtText(caseNumber, 40),
                Screens.CourtText(filedAt, 40),
                [.. screen.Entries.Select(entry => Screens.CourtText(entry))],
                screen.ById.GetValueOrDefault("receipt-number") is { Length: > 0 } receipt ? receipt : null,
                screen.ById.GetValueOrDefault("fee-due") is { Length: > 0 } fee ? fee : null,
                response.Body);
        }

        // ── transport ──

        /// <summary>One request and its redirects, each hop through the fenced client. A transport failure before the submit is a hand-back.</summary>
        private async Task<CourtResponse> SendAsync(CourtRequest request, CancellationToken cancellationToken)
        {
            CourtResponse response;
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                for (var hop = 0; hop < MaxRedirects; hop++)
                {
                    var location = response.Header("Location");
                    if (response.Status is not (301 or 302 or 303 or 307) || location is null)
                    {
                        break;
                    }

                    response = await http.SendAsync(new CourtRequest("GET", Join(response.Url, location)), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (CourtTimeoutException error)
            {
                throw new HandBackException("court_timeout", null, error);
            }
            catch (CourtTransportException error)
            {
                throw new HandBackException("court_error", null, error);
            }

            return response.Status >= 500 ? throw new HandBackException("court_error", $"HTTP {response.Status}") : response;
        }

        private Task<CourtResponse> GetAsync(string path, CancellationToken cancellationToken) =>
            SendAsync(new CourtRequest("GET", Join(baseUrl, path.TrimStart('/'))), cancellationToken);

        private Task<CourtResponse> PostAsync(string path, IReadOnlyList<(string Name, string Value)> fields, CancellationToken cancellationToken) =>
            SendAsync(new CourtRequest("POST", Join(baseUrl, path.TrimStart('/')), Form(fields), "application/x-www-form-urlencoded"), cancellationToken);

        private static (string Name, Screen Screen) Expect(CourtResponse response, params string[] allowed)
        {
            var screen = Screens.Parse(response.Text);
            var name = Screens.Recognise(screen, allowed.ToDictionary(key => key, key => Known[key], StringComparer.Ordinal));
            return name is null ? throw new HandBackException("unrecognised_screen", Screens.CourtText(screen.Title)) : (name, screen);
        }

        private static string? Said(Screen screen) => screen.Messages.Count > 0 ? Screens.CourtText(string.Join(' ', screen.Messages)) : null;
    }

    /// <summary>Python's <c>urljoin</c> for the shapes the fake uses.</summary>
    internal static string Join(string from, string location) => new Uri(new Uri(from), location).ToString();

    /// <summary>Python's <c>urlencode</c> (quote_plus).</summary>
    internal static byte[] Form(IReadOnlyList<(string Name, string Value)> fields) =>
        Encoding.UTF8.GetBytes(string.Join('&', fields.Select(field => Plus(field.Name) + "=" + Plus(field.Value))));

    private static string Plus(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    private static byte[] Multipart(string boundary, IReadOnlyList<(string Name, string Value)> fields, (string Field, string FileName, byte[] Content) file)
    {
        var output = new MemoryStream();
        void Line(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\r\n");
            output.Write(bytes);
        }

        foreach (var (name, value) in fields)
        {
            Line($"--{boundary}");
            Line($"Content-Disposition: form-data; name=\"{name}\"");
            Line(string.Empty);
            Line(value);
        }

        Line($"--{boundary}");
        Line($"Content-Disposition: form-data; name=\"{file.Field}\"; filename=\"{file.FileName}\"");
        Line("Content-Type: application/octet-stream");
        Line(string.Empty);
        output.Write(file.Content);
        output.Write("\r\n"u8);
        Line($"--{boundary}--");
        return output.ToArray();
    }
}
