using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Insolvia.Filing;

/// <summary>
/// TOTP (RFC 6238, HMAC-SHA1, 30-second steps, six digits; from
/// <c>core/totp.py</c>) — the code PACER's multifactor sign-in asks for,
/// computed from the attorney's sealed seed at the moment the court asks,
/// and never stored, logged, raised or returned anywhere but the court's form.
/// </summary>
public static class Totp
{
    /// <summary>The step.</summary>
    public const int StepSeconds = 30;

    /// <summary>The code's length.</summary>
    public const int Digits = 6;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The code valid at <paramref name="moment"/> for a base32 <paramref name="seed"/> (spaces and case ignored).</summary>
    public static string At(string seed, DateTimeOffset moment) => AtCounter(seed, moment.ToUnixTimeSeconds() / StepSeconds);

    /// <summary>The code for one step counter.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "RFC 6238 TOTP as PACER issues it is HMAC-SHA1; the court picks the algorithm, not us.")]
    public static string AtCounter(string seed, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
        var digest = HMACSHA1.HashData(Key(seed), message);
        var offset = digest[^1] & 0x0F;
        var value = BinaryPrimitives.ReadInt32BigEndian(digest.AsSpan(offset, 4)) & 0x7FFFFFFF;
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>RFC 4648 base32, re-padded as Python's <c>b32decode</c> takes it.</summary>
    internal static byte[] Key(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var normalised = seed.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant().TrimEnd('=');
        var output = new List<byte>(normalised.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var symbol in normalised)
        {
            var index = Alphabet.IndexOf(symbol, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new FormatException("not a base32 seed");
            }

            buffer = (buffer << 5) | index;
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
}
