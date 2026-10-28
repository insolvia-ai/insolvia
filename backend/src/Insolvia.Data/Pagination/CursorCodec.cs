using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Insolvia.Data.Pagination;

/// <summary>
/// Mints and opens pagination cursors: opaque to the client, tamper-evident,
/// and bound to the query that minted them.
/// </summary>
/// <remarks>
/// <para>
/// A cursor is <c>base64url(json(key)) "." base64url(mac)</c>, where the MAC
/// is HMAC-SHA256 over the query's name and the payload. A cursor from
/// listing A presented to listing B fails the MAC — the Python service's
/// index tag (<c>cases.py</c> <c>decode_cursor</c>) made mechanical — and so
/// does any edit to the payload. Every failure is the same
/// <see cref="InvalidCursorException"/>: "that cursor is for the other
/// listing" would tell a caller something about how their permissions are
/// evaluated, and their action is the same either way.
/// </para>
/// <para>
/// The cursor is still not an authorization token and must never become one:
/// the query it resumes is already pinned to the caller's firm or subject.
/// The MAC exists so a store never has to reason about a key the client
/// shaped. With no <c>INSOLVIA_CURSOR_KEY</c> configured the key is a
/// constant written in this public repository, which keeps the binding and
/// the edit detection but not unforgeability; configuring a secret key
/// upgrades that without a code change (and invalidates outstanding cursors,
/// which a client handles by starting the listing again).
/// </para>
/// </remarks>
public sealed class CursorCodec
{
    /// <summary>The environment variable holding a base64 key of at least 32 bytes.</summary>
    public const string KeyVariable = "INSOLVIA_CURSOR_KEY";

    private const int MacLength = 16;

    private static readonly byte[] PublicKey = SHA256.HashData("insolvia-cursor-v1"u8);

    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private readonly byte[] _key;

    /// <summary>A codec signing with <paramref name="key"/> (at least 32 bytes).</summary>
    public CursorCodec(ReadOnlySpan<byte> key)
    {
        if (key.Length < 32)
        {
            throw new ArgumentException("A cursor key is at least 32 bytes.", nameof(key));
        }

        _key = key.ToArray();
    }

    /// <summary>The codec configured by <see cref="KeyVariable"/>, or the public constant key.</summary>
    public static CursorCodec FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var configured = getVariable(KeyVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? new CursorCodec(PublicKey)
            : new CursorCodec(Convert.FromBase64String(configured));
    }

    /// <summary>A cursor resuming <paramref name="query"/> after <paramref name="key"/>.</summary>
    /// <param name="query">The listing's name, including anything that changes
    /// which rows it reads (index, view, scope) — e.g. <c>cases.by-firm:archived</c>.</param>
    /// <param name="key">The last row's keyset values.</param>
    public string Encode<TKey>(string query, TKey key)
        where TKey : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        var payload = JsonSerializer.SerializeToUtf8Bytes(key, Json);
        return $"{Base64Url(payload)}.{Base64Url(Mac(query, payload))}";
    }

    /// <summary>The keyset values in <paramref name="cursor"/>, if <paramref name="query"/> minted it.</summary>
    /// <exception cref="InvalidCursorException">The cursor is malformed, edited, or from another query.</exception>
    public TKey Decode<TKey>(string query, string cursor)
        where TKey : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        if (string.IsNullOrEmpty(cursor))
        {
            throw new InvalidCursorException();
        }

        var dot = cursor.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0
            || !TryFromBase64Url(cursor[..dot], out var payload)
            || !TryFromBase64Url(cursor[(dot + 1)..], out var mac)
            || !CryptographicOperations.FixedTimeEquals(mac, Mac(query, payload)))
        {
            throw new InvalidCursorException();
        }

        try
        {
            return JsonSerializer.Deserialize<TKey>(payload, Json) ?? throw new InvalidCursorException();
        }
        catch (JsonException error)
        {
            throw new InvalidCursorException(error);
        }
    }

    private byte[] Mac(string query, byte[] payload)
    {
        var queryBytes = Encoding.UTF8.GetBytes(query);
        var input = new byte[4 + queryBytes.Length + payload.Length];
        // Length-prefixed, so (query "a", payload "bc") and ("ab", "c") differ.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(input, queryBytes.Length);
        queryBytes.CopyTo(input, 4);
        payload.CopyTo(input, 4 + queryBytes.Length);
        return HMACSHA256.HashData(_key, input)[..MacLength];
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string text, out byte[] bytes)
    {
        var standard = text.Replace('-', '+').Replace('_', '/');
        standard = (standard.Length % 4) switch
        {
            2 => standard + "==",
            3 => standard + "=",
            _ => standard,
        };
        var buffer = new byte[(standard.Length * 3 / 4) + 3];
        if (Convert.TryFromBase64String(standard, buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = [];
        return false;
    }
}
