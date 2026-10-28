using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Fields;
using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;

namespace Insolvia.Core.FilingCredentials;

// The attorney's CM/ECF credential vault (ADR 0024, guardrail 3; from
// filing_credentials.py) — the DOMAIN half. The seal and the open are ports
// (IFilingCredentialSealer / IFilingCredentialOpener), split so the API's
// composition holds nothing that could open a credential; the KMS envelope
// behind them is 18.12's.

/// <summary>The plaintext the worker signs in with. Its text form is redacted, so it cannot reach a log line.</summary>
public sealed record CredentialSecret(string Password, string TotpSeed)
{
    /// <inheritdoc />
    public override string ToString() => "CredentialSecret(<redacted>)";
}

/// <summary>A parsed enrolment: the login, the secret to seal, and the courts the attorney says they e-file in.</summary>
public sealed record Enrolment(string Login, CredentialSecret Secret, ValueCollection<string> Courts)
{
    /// <inheritdoc />
    public override string ToString() => $"Enrolment {{ Login = {Login}, Courts = {Courts} }}";
}

/// <summary>
/// The stored item. Everything but <see cref="Envelope"/> is plain and is what
/// the status view is built from.
/// </summary>
/// <param name="CredentialId">The server-minted uuid.</param>
/// <param name="FirmId">The attorney's firm.</param>
/// <param name="AttorneyId">The attorney — the item's partition.</param>
/// <param name="Login">The PACER username.</param>
/// <param name="Courts">The courts the attorney e-files in.</param>
/// <param name="Status">Always active while it exists — revocation destroys it.</param>
/// <param name="Envelope">The sealed secret.</param>
/// <param name="CreatedAt">When it was enrolled.</param>
/// <param name="UpdatedAt">When it was last written.</param>
/// <param name="AuthorizationRef">The signature it was enrolled under.</param>
public sealed record FilingCredential(
    string CredentialId,
    string FirmId,
    string AttorneyId,
    string Login,
    ValueCollection<string> Courts,
    CredentialStatus Status,
    Envelope Envelope,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? AuthorizationRef = null);

/// <summary>Enrolment without a current signed authorization — a 403: the remedy is the caller's own.</summary>
public sealed class AuthorizationRequiredException : ForbiddenException
{
    /// <summary>Creates the exception with no message.</summary>
    public AuthorizationRequiredException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public AuthorizationRequiredException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public AuthorizationRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Nothing to open: never enrolled, revoked, or not openable. The worker
/// treats it as stop-and-hand-back, never a retry. Not an API failure.
/// </summary>
public sealed class CredentialUnavailableException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public CredentialUnavailableException()
    {
    }

    /// <summary>Creates the exception naming the credential.</summary>
    public CredentialUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public CredentialUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The vault's rules.</summary>
public static partial class FilingCredentialRules
{
    /// <summary>The encryption context's purpose.</summary>
    public const string Purpose = "filing-credential";

    /// <summary>The cap on a password.</summary>
    public const int MaxPassword = 128;

    /// <summary>A fresh credential id.</summary>
    public static string NewCredentialId() => Guid.NewGuid().ToString();

    /// <summary>The KMS encryption context AND the AES-GCM associated data for one credential.</summary>
    public static ValueDictionary<string, string> EncryptionContext(string firmId, string attorneyId, string credentialId) =>
        new Dictionary<string, string>
        {
            ["purpose"] = Purpose,
            ["firm_id"] = firmId,
            ["attorney_id"] = attorneyId,
            ["credential_id"] = credentialId,
        }.ToValueDictionary();

    /// <summary>
    /// Validate <c>{login, password, totp_seed, courts?}</c>. Every problem lands
    /// in one error, keyed by field, and no message echoes a submitted value.
    /// </summary>
    public static Enrolment ParseEnrolment(JsonNode? payload, Ports.ICourtRegistry? courts = null)
    {
        var registry = courts ?? Courts.CourtRegistry.Shipped;
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var raw = FieldParsers.Mapping(payload, "body", errors);

        var login = Json.AsString(raw["login"]);
        if (login is null || !Login().IsMatch(PyText.Strip(login)))
        {
            errors["login"] = "Enter the PACER username — letters, digits and . _ @ - only, at most 64 characters.";
        }

        var password = Json.AsString(raw["password"]);
        if (string.IsNullOrEmpty(password))
        {
            errors["password"] = "Enter the PACER password.";
        }
        else if (PyText.Length(password) > MaxPassword)
        {
            errors["password"] = $"At most {MaxPassword} characters.";
        }
        else if (password.Any(ch => ch < 0x20 || ch == 0x7F))
        {
            errors["password"] = "The password contains a control character.";
        }

        var seedRaw = Json.AsString(raw["totp_seed"]);
        var seed = seedRaw is null ? string.Empty : NormaliseSeed(seedRaw);
        if (!Seed().IsMatch(seed) || !SeedDecodes(seed))
        {
            errors["totp_seed"] = "Enter the authenticator key PACER showed you — the base32 text (letters A to Z and digits 2 to 7), at least 16 characters.";
        }

        var courtsNode = raw.ContainsKey("courts") ? raw["courts"] : new JsonArray();
        var chosen = new List<string>();
        if (courtsNode is not JsonArray courtList || courtList.Any(court => Json.AsString(court) is null))
        {
            errors["courts"] = "Must be a list of court codes.";
        }
        else
        {
            var codes = courtList.Select(court => Json.AsString(court)!).ToList();
            var unknown = codes.Where(code => !registry.HasDistrict(code)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                errors["courts"] = "Unknown court: " + string.Join(", ", unknown) + ".";
            }

            chosen = [.. codes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return new Enrolment(PyText.Strip(login!), new CredentialSecret(password!, seed), chosen.ToValueCollection());
    }

    /// <summary>
    /// Seal and store one credential for the signed-in attorney —
    /// <paramref name="attorneyId"/> is the CALLER's own subject. Refused first,
    /// before anything is sealed, without a current signed authorization (403);
    /// a login already enrolled is a 409 (rotation is revoke-then-enrol).
    /// </summary>
    public static async Task<FilingCredential> EnrolAsync(
        Enrolment enrolment,
        string firmId,
        string attorneyId,
        IFilingCredentialSealer sealer,
        IFilingCredentialStore store,
        IAccessLog accessLog,
        IFilingAuthorizationStore authorizations,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enrolment);
        ArgumentNullException.ThrowIfNull(sealer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(accessLog);
        ArgumentNullException.ThrowIfNull(authorizations);
        var authorization = await authorizations.GetCurrentAsync(firmId, attorneyId, cancellationToken).ConfigureAwait(false);
        if (authorization is null || !FilingAuthorizationRules.IsCurrent(authorization, firmId, attorneyId))
        {
            throw new AuthorizationRequiredException("Sign the filing authorization before storing a court login.");
        }

        var existing = await store.ListForAttorneyAsync(firmId, attorneyId, cancellationToken).ConfigureAwait(false);
        if (existing.Any(credential => PyText.Lower(credential.Login) == PyText.Lower(enrolment.Login)))
        {
            throw new ConflictException("That PACER login is already enrolled — revoke it first to replace it.");
        }

        var credentialId = NewCredentialId();
        var now = Clock.Now(clock);
        var envelope = await sealer.SealAsync(SecretPayload(enrolment.Secret), EncryptionContext(firmId, attorneyId, credentialId), cancellationToken).ConfigureAwait(false);
        var credential = new FilingCredential(credentialId, firmId, attorneyId, enrolment.Login, enrolment.Courts, CredentialStatus.Active, envelope, now, now, authorization.AuthorizationId);
        await store.CreateAsync(credential, cancellationToken).ConfigureAwait(false);
        await accessLog.RecordAsync(AccessEvents.Record(attorneyId, AccessAction.CredentialEnrol, credentialId: credentialId, clock: clock), cancellationToken).ConfigureAwait(false);
        return credential;
    }

    /// <summary>The attorney's own credentials, for the status view — never opens an envelope.</summary>
    public static Task<IReadOnlyList<FilingCredential>> ListAsync(string firmId, string attorneyId, IFilingCredentialStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.ListForAttorneyAsync(firmId, attorneyId, cancellationToken);
    }

    /// <summary>
    /// DESTROY the item. True when there was one. Keyed by the caller's own
    /// partition, so another attorney's id is simply not found. Logged either way.
    /// </summary>
    public static async Task<bool> RevokeAsync(
        string credentialId,
        string firmId,
        string attorneyId,
        IFilingCredentialStore store,
        IAccessLog accessLog,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(accessLog);
        var deleted = await store.DeleteAsync(firmId, attorneyId, credentialId, cancellationToken).ConfigureAwait(false);
        await accessLog.RecordAsync(
            AccessEvents.Record(attorneyId, AccessAction.CredentialRevoke, credentialId: credentialId, outcome: deleted ? AccessOutcome.Allowed : AccessOutcome.Denied, clock: clock),
            cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// THE ONE PREDICATE the open consults before it decrypts: active, enrolled
    /// under a signature, and the attorney's authorization current NOW.
    /// </summary>
    public static bool IsOpenable(FilingCredential credential, FilingAuthorization? authorization)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.Status == CredentialStatus.Active
            && credential.AuthorizationRef is not null
            && FilingAuthorizationRules.IsCurrent(authorization, credential.FirmId, credential.AttorneyId);
    }

    /// <summary>
    /// THE PLAINTEXT, and the one place it is produced — the filing worker's
    /// call. Records a <c>credential.open</c> row FIRST, then refuses an item
    /// that is gone or not openable, then opens under the sealing context.
    /// </summary>
    public static async Task<CredentialSecret> OpenAsync(
        string firmId,
        string attorneyId,
        string credentialId,
        string filingId,
        string purpose,
        IFilingCredentialOpener opener,
        IFilingCredentialStore store,
        IFilingAuthorizationStore authorizations,
        IAccessLog accessLog,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(accessLog);
        await accessLog.RecordAsync(
            AccessEvents.Record(attorneyId, AccessAction.CredentialOpen, credentialId: credentialId, purpose: purpose, filingId: filingId, clock: clock),
            cancellationToken).ConfigureAwait(false);
        var credential = await store.GetAsync(firmId, attorneyId, credentialId, cancellationToken).ConfigureAwait(false);
        if (credential is null
            || !IsOpenable(credential, await authorizations.GetCurrentAsync(firmId, attorneyId, cancellationToken).ConfigureAwait(false)))
        {
            throw new CredentialUnavailableException(credentialId);
        }

        var plaintext = await opener.OpenAsync(
            credential.Envelope,
            EncryptionContext(credential.FirmId, credential.AttorneyId, credential.CredentialId),
            cancellationToken).ConfigureAwait(false);
        var document = JsonNode.Parse(plaintext)!.AsObject();
        return new CredentialSecret(Json.AsString(document["password"])!, Json.AsString(document["totp_seed"])!);
    }

    /// <summary>
    /// WITHDRAW, AND THAT REVOKES. The authorization item goes FIRST — from that
    /// write on nothing this attorney holds is openable — then each credential is
    /// destroyed. Returns how many. 404 when there is nothing to withdraw.
    /// </summary>
    public static async Task<int> WithdrawAuthorizationAsync(
        string firmId,
        string attorneyId,
        IFilingAuthorizationStore authorizations,
        IFilingCredentialStore store,
        IAccessLog accessLog,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(accessLog);
        var authorization = await authorizations.GetCurrentAsync(firmId, attorneyId, cancellationToken).ConfigureAwait(false);
        if (authorization is null
            || !await authorizations.WithdrawAsync(authorization, Clock.Now(clock), cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("no filing authorization to withdraw");
        }

        var destroyed = 0;
        foreach (var credential in await store.ListForAttorneyAsync(firmId, attorneyId, cancellationToken).ConfigureAwait(false))
        {
            if (await RevokeAsync(credential.CredentialId, firmId, attorneyId, store, accessLog, clock, cancellationToken).ConfigureAwait(false))
            {
                destroyed++;
            }
        }

        await accessLog.RecordAsync(
            AccessEvents.Record(attorneyId, AccessAction.AuthorizationWithdraw, authorizationId: authorization.AuthorizationId, clock: clock),
            cancellationToken).ConfigureAwait(false);
        return destroyed;
    }

    /// <summary>The sealed document: canonical JSON, sorted keys, no whitespace.</summary>
    internal static string SecretPayload(CredentialSecret secret) =>
        new JsonObject { ["password"] = secret.Password, ["totp_seed"] = secret.TotpSeed }
            .ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Seeds are shown grouped and sometimes lower-case; canonical is upper-case base32, no spaces.</summary>
    private static string NormaliseSeed(string value) => string.Concat(PyText.SplitWhitespace(value)).ToUpperInvariant();

    /// <summary>
    /// Whether <c>b32decode</c> accepts the seed once re-padded: the regex has
    /// fixed the alphabet, so only the final quantum can be refused — one that
    /// cannot end on a byte (1, 3 or 6 symbols).
    /// </summary>
    private static bool SeedDecodes(string seed) => (seed.TrimEnd('=').Length % 8) is not (1 or 3 or 6);

    [GeneratedRegex(@"^[A-Za-z0-9._@-]{1,64}\z")]
    private static partial Regex Login();

    [GeneratedRegex(@"^[A-Z2-7]{16,128}={0,6}\z")]
    private static partial Regex Seed();
}

/// <summary>A filing credential's state (Python's <c>CREDENTIAL_STATUSES</c>).</summary>
public enum CredentialStatus
{
    /// <summary>Stored and usable while the attorney's authorization is current.</summary>
    [JsonStringEnumMemberName("active")]
    Active,
}
