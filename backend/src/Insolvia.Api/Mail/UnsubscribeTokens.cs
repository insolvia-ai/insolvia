using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Insolvia.Core;

namespace Insolvia.Api.Mail;

/// <summary>
/// No unsubscribe signing key is configured (Python's
/// <c>UnsubscribeSecretMissingError</c>). Deliberately NOT an
/// <see cref="ApiException"/>: every one of those is the caller's fault, and
/// this is the deployment's — it reaches the catch-all, which logs it and
/// answers the 500 that names nothing.
/// </summary>
public sealed class UnsubscribeSecretMissingException : Exception
{
    /// <summary>Creates the exception with its one message.</summary>
    public UnsubscribeSecretMissingException()
        : base("unsubscribe secret is not configured")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public UnsubscribeSecretMissingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public UnsubscribeSecretMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Unsubscribe tokens (issue #80; services/api core/unsubscribe.py, byte for
/// byte, so a link minted by either backend verifies on the other):
/// <c>v1.&lt;b64url(payload)&gt;.&lt;b64url(hmac_sha256(secret, payload))&gt;</c>
/// with <c>payload = "1:&lt;issued_at&gt;:&lt;lower-cased address&gt;"</c>.
/// </summary>
/// <remarks>
/// <para>The HMAC is the whole point: the mailer suppresses whatever address
/// a registered caller names, so without it the public unsubscribe route
/// would stop mail to anyone. The address is readable (base64, not a
/// cipher) — only its owner receives the link.</para>
/// <para>No expiry is enforced (an old link must still unsubscribe);
/// verification is constant-time; and every failure is the SAME
/// <see cref="ValidationException"/>, so a caller learns "invalid", never
/// which part.</para>
/// </remarks>
public static class UnsubscribeTokens
{
    /// <summary>The token format's version prefix.</summary>
    public const string TokenVersion = "v1";

    /// <summary>The mailer's cap on <c>to_address</c>; an address longer cannot be a subject.</summary>
    public const int MaxAddressChars = 320;

    /// <summary>The longest token worth spending an HMAC on.</summary>
    public const int MaxTokenChars = 1024;

    private const string PayloadVersion = "1";
    private const string Invalid = "unsubscribe token is invalid";
    private const string NotASubject = "address is not a valid unsubscribe subject";

    /// <summary>
    /// <c>address.strip().lower()</c> — the mailer's <c>recipient_hash</c>
    /// normalisation, so mixed-case and lower-case tokens suppress one entry.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>The normalised address.</returns>
    public static string NormalizeAddress(string address) => PyText.Lower(PyText.Strip(address));

    /// <summary>A token proving the holder was sent mail at <paramref name="address"/>.</summary>
    /// <param name="address">The address.</param>
    /// <param name="secret">The signing key (<c>UNSUBSCRIBE_SECRET</c>).</param>
    /// <param name="issuedAt">Epoch seconds; now when null.</param>
    /// <returns>The token.</returns>
    public static string Mint(string address, string? secret, long? issuedAt = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        var key = RequireSecret(secret);
        var normalized = NormalizeAddress(address);
        if (normalized.Length == 0 || PyText.Length(normalized) > MaxAddressChars)
        {
            throw new ValidationException(NotASubject);
        }

        // The payload is colon-delimited; an address carrying one could
        // shift the field boundaries and claim a different address.
        if (normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new ValidationException(NotASubject);
        }

        var stamp = issuedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = Encoding.UTF8.GetBytes($"{PayloadVersion}:{stamp.ToString(CultureInfo.InvariantCulture)}:{normalized}");
        return string.Join(".", TokenVersion, Encode(payload), Encode(Sign(payload, key)));
    }

    /// <summary>The address a valid token names, or a <see cref="ValidationException"/>.</summary>
    /// <param name="token">The token from the link.</param>
    /// <param name="secret">The signing key.</param>
    /// <returns>The address.</returns>
    public static string Verify(string? token, string? secret)
    {
        var key = RequireSecret(secret);
        if (token is null || token.Length == 0 || token.Length > MaxTokenChars)
        {
            throw new ValidationException(Invalid);
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != TokenVersion)
        {
            throw new ValidationException(Invalid);
        }

        var payload = Decode(parts[1]);
        if (!CryptographicOperations.FixedTimeEquals(Sign(payload, key), Decode(parts[2])))
        {
            throw new ValidationException(Invalid);
        }

        // Only now is the payload trusted enough to parse.
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(payload);
        }
        catch (DecoderFallbackException error)
        {
            throw new ValidationException(Invalid, error);
        }

        var fields = text.Split(':', 3);
        if (fields.Length != 3 || fields[0] != PayloadVersion || fields[2].Length == 0)
        {
            throw new ValidationException(Invalid);
        }

        return fields[2];
    }

    /// <summary>How long ago a token was minted (verified first). Diagnostics only — nothing rejects on it.</summary>
    /// <param name="token">The token.</param>
    /// <param name="secret">The signing key.</param>
    /// <param name="now">Epoch seconds; now when null.</param>
    /// <returns>The age in seconds.</returns>
    public static long AgeSeconds(string token, string? secret, long? now = null)
    {
        Verify(token, secret);
        var payload = Encoding.UTF8.GetString(Decode(token.Split('.')[1]));
        var issuedAt = long.Parse(payload.Split(':', 3)[1], CultureInfo.InvariantCulture);
        return (now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - issuedAt;
    }

    /// <summary>
    /// The link for an email footer and its List-Unsubscribe header — on the
    /// MARKETING origin, which forwards to <c>POST /v1/unsubscribe</c>.
    /// </summary>
    /// <param name="address">The recipient.</param>
    /// <param name="secret">The signing key.</param>
    /// <param name="marketingOrigin">This environment's marketing origin.</param>
    /// <returns>The URL.</returns>
    public static string Url(string address, string? secret, string marketingOrigin)
    {
        ArgumentNullException.ThrowIfNull(marketingOrigin);
        return $"{marketingOrigin.TrimEnd('/')}/unsubscribe?token={Mint(address, secret)}";
    }

    private static string RequireSecret(string? secret) =>
        string.IsNullOrEmpty(secret) ? throw new UnsubscribeSecretMissingException() : secret;

    private static byte[] Sign(byte[] payload, string secret) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);

    private static string Encode(byte[] raw) => Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - (value.Length % 4)) % 4);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException error)
        {
            throw new ValidationException(Invalid, error);
        }
    }
}
