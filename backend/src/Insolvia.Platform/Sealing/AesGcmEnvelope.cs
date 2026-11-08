using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Insolvia.Core.TaxIds;

namespace Insolvia.Aws.Sealing;

/// <summary>
/// The symmetric half of envelope encryption, shared by every cipher in this
/// namespace — a byte-for-byte port of insolvia_core
/// <c>adapters/envelope.py</c> (ADR 0028: "Sealed data keeps its KMS
/// envelopes byte-for-byte").
/// </summary>
/// <remarks>
/// <para><b>The format.</b> AES-256-GCM over the UTF-8 plaintext, a fresh
/// 12-byte nonce, a 16-byte tag APPENDED to the ciphertext (the layout
/// Python's <c>AESGCM.encrypt</c> returns), and the encryption context in
/// canonical JSON as the associated data. Every field of the
/// <see cref="Envelope"/> is standard base64 with padding.</para>
/// <para><b>Why the context is the AAD.</b> It ties the ciphertext to
/// <c>{purpose, firm_id, …}</c> even before the wrapped key reaches KMS: an
/// open under any other context fails the tag, whichever cipher did the
/// unwrap. So the canonical form must never drift — every stored envelope
/// was sealed under it; <see cref="ContextAad"/> owns it.</para>
/// <para>One cipher differs from another only in where the data key is
/// wrapped (KMS, or a fixed local key). Everything after that is this class,
/// so a test against the local cipher proves what the deployed one stores.</para>
/// </remarks>
public static partial class AesGcmEnvelope
{
    /// <summary>AES-256: a 32-byte data key.</summary>
    public const int DataKeyBytes = 32;

    /// <summary>The NIST SP 800-38D 96-bit nonce — the only size .NET's AesGcm takes, and the one stored envelopes carry.</summary>
    public const int NonceBytes = 12;

    /// <summary>The full 128-bit tag, appended to the ciphertext.</summary>
    public const int TagBytes = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The encryption context as GCM associated data — Python's
    /// <c>json.dumps(dict(context), sort_keys=True, separators=(",", ":")).encode()</c>,
    /// reproduced exactly: keys sorted by code point, no whitespace, and
    /// <c>ensure_ascii</c> on (every UTF-16 unit outside printable ASCII as a
    /// lowercase <c>\uXXXX</c>, so the bytes are ASCII).
    /// </summary>
    public static byte[] ContextAad(IReadOnlyDictionary<string, string> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = new StringBuilder("{");
        var first = true;
        foreach (var pair in context.OrderBy(pair => pair.Key, CodePointComparer.Instance))
        {
            if (!first)
            {
                text.Append(',');
            }

            first = false;
            AppendJsonString(text, pair.Key);
            text.Append(':');
            AppendJsonString(text, pair.Value);
        }

        text.Append('}');
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>A fresh data key from the OS CSPRNG (Python's <c>os.urandom(32)</c>).</summary>
    public static byte[] NewDataKey() => RandomNumberGenerator.GetBytes(DataKeyBytes);

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> under <paramref name="dataKey"/>,
    /// binding <paramref name="context"/>, and package it with the caller's
    /// already-wrapped copy of the key.
    /// </summary>
    public static Envelope Seal(ReadOnlySpan<byte> dataKey, string plaintext, IReadOnlyDictionary<string, string> context, ReadOnlySpan<byte> wrappedKey) =>
        Seal(dataKey, plaintext, context, wrappedKey, RandomNumberGenerator.GetBytes(NonceBytes));

    /// <summary>
    /// The seal with a caller-chosen nonce — internal, for the byte-identity
    /// test against the sealed fixtures only. A repeated nonce under one key breaks GCM.
    /// </summary>
    internal static Envelope Seal(ReadOnlySpan<byte> dataKey, string plaintext, IReadOnlyDictionary<string, string> context, ReadOnlySpan<byte> wrappedKey, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var sealedBytes = Encrypt(dataKey, nonce, StrictUtf8.GetBytes(plaintext), ContextAad(context));
        return new Envelope(TaxIdRules.SealedScheme, Convert.ToBase64String(sealedBytes), Convert.ToBase64String(nonce), Convert.ToBase64String(wrappedKey));
    }

    /// <summary>
    /// The inverse. A wrong key or a wrong context fails the tag and throws
    /// <see cref="AuthenticationTagMismatchException"/> (Python's
    /// <c>InvalidTag</c>); an envelope under a scheme this code does not know
    /// is refused before any key is touched.
    /// </summary>
    public static string Open(ReadOnlySpan<byte> dataKey, Envelope envelope, IReadOnlyDictionary<string, string> context)
    {
        RequireKnownScheme(envelope);
        var plaintext = Decrypt(dataKey, Unbase64(envelope.Nonce), Unbase64(envelope.Ciphertext), ContextAad(context));
        return StrictUtf8.GetString(plaintext);
    }

    /// <summary>The wrapped data key's bytes — what KMS <c>Decrypt</c> is handed.</summary>
    public static byte[] WrappedKeyBytes(Envelope envelope)
    {
        RequireKnownScheme(envelope);
        return Unbase64(envelope.WrappedKey);
    }

    /// <summary>
    /// AES-GCM in Python's layout: the tag appended to the ciphertext.
    /// Shared with the local key wrap.
    /// </summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> key, byte[] nonce, byte[] plaintext, byte[] associatedData)
    {
        using var aes = new AesGcm(key, TagBytes);
        var output = new byte[plaintext.Length + TagBytes];
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), associatedData);
        return output;
    }

    /// <summary>The inverse of <see cref="Encrypt"/>. Anything too short to hold a tag fails as a bad tag.</summary>
    internal static byte[] Decrypt(ReadOnlySpan<byte> key, byte[] nonce, byte[] sealedBytes, byte[] associatedData)
    {
        if (sealedBytes.Length < TagBytes)
        {
            throw new AuthenticationTagMismatchException();
        }

        using var aes = new AesGcm(key, TagBytes);
        var length = sealedBytes.Length - TagBytes;
        var plaintext = new byte[length];
        aes.Decrypt(nonce, sealedBytes.AsSpan(0, length), sealedBytes.AsSpan(length), plaintext, associatedData);
        return plaintext;
    }

    /// <summary>
    /// Python's <c>base64.b64decode(text, validate=True)</c>: the standard
    /// alphabet only — no whitespace, no URL-safe characters — which
    /// <see cref="Convert.FromBase64String"/> alone would let through.
    /// </summary>
    internal static byte[] Unbase64(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!StrictBase64().IsMatch(text))
        {
            throw new FormatException("not standard base64");
        }

        return Convert.FromBase64String(text);
    }

    private static void RequireKnownScheme(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Scheme != TaxIdRules.SealedScheme)
        {
            throw new ArgumentException($"unknown sealed scheme '{envelope.Scheme}'", nameof(envelope));
        }
    }

    // json.encoder.py_encode_basestring_ascii: the two-character escapes for
    // \" \\ \b \f \n \r \t, and \uXXXX in lowercase hex for every other unit
    // outside ' '..'~' — which includes DEL (0x7F): Python's pattern is
    // [^\ -~], not "non-ASCII" (the fixture caught that). A character above
    // the BMP is a surrogate pair in both languages, so per-UTF-16-unit
    // escaping yields Python's pair of escapes.
    private static void AppendJsonString(StringBuilder text, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        text.Append('"');
        foreach (var unit in value)
        {
            switch (unit)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case < ' ' or > '~':
                    text.Append("\\u").Append(((int)unit).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default: text.Append(unit); break;
            }
        }

        text.Append('"');
    }

    [GeneratedRegex("^[A-Za-z0-9+/]*={0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex StrictBase64();

    /// <summary>
    /// Python orders <c>str</c> by code point; .NET's ordinal comparison is by
    /// UTF-16 unit, which disagrees once a key mixes astral characters with
    /// U+E000–U+FFFF. Keys are ASCII today; this keeps the AAD right if not.
    /// </summary>
    private sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return string.CompareOrdinal(x, y);
            }

            var (i, j) = (0, 0);
            while (i < x.Length && j < y.Length)
            {
                var (a, aLength) = CodePointAt(x, i);
                var (b, bLength) = CodePointAt(y, j);
                if (a != b)
                {
                    return a.CompareTo(b);
                }

                i += aLength;
                j += bLength;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }

        // A lone surrogate is its own code point, as in a Python str.
        private static (int CodePoint, int Length) CodePointAt(string text, int index) =>
            char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? (char.ConvertToUtf32(text[index], text[index + 1]), 2)
                : (text[index], 1);
    }
}
