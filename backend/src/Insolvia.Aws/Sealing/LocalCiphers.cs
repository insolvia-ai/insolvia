using System.Security.Cryptography;
using System.Text;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;

namespace Insolvia.Aws.Sealing;

/// <summary>
/// <see cref="ITaxIdCipher"/> with the KMS call replaced by a local key wrap —
/// Python's <c>LocalTaxIdCipher</c> (adapters/memory/tax_id_cipher.py), for
/// tests and an in-memory host that has no KMS key to protect.
/// </summary>
/// <remarks>
/// <para><b>Why here and not in Core's Memory namespace.</b> A twin built on a
/// second copy of the AES-GCM code would prove nothing about what the
/// deployed cipher stores; this one runs <see cref="AesGcmEnvelope"/>, the
/// same code as the KMS ciphers. Core may not reference this project and a
/// host may, so the twin lives beside the code it must share.</para>
/// <para>THE REAL CODE PATH MINUS THE NETWORK: seal mints a data key as KMS
/// would and wraps it as <c>nonce ‖ AES-GCM(master, data key, AAD=context)</c>
/// — the binding KMS enforces on its side — so an envelope sealed for one firm
/// and ref cannot be opened for another. Byte-compatible with Python's: an
/// envelope either one seals, the other opens.</para>
/// </remarks>
/// <param name="masterKey">The wrapping key; <see cref="LocalMasterKey"/> by default.</param>
public sealed class LocalTaxIdCipher(byte[]? masterKey = null) : ITaxIdCipher
{
    private static readonly byte[] DefaultMasterKey = SHA256.HashData(
        Encoding.ASCII.GetBytes("insolvia local tax-id master key - tests and the bare dev server only"));

    private readonly byte[] _masterKey = masterKey ?? DefaultMasterKey;

    /// <summary>
    /// DETERMINISTIC, AND NEVER DEPLOYED: <c>sha256</c> of a fixed string —
    /// Python's <c>LOCAL_MASTER_KEY</c>, byte for byte — so a fixture sealed in
    /// one process opens in another. It secures nothing and is not meant to.
    /// A copy, so no caller can change the shared one.
    /// </summary>
    public static byte[] LocalMasterKey => [.. DefaultMasterKey];

    /// <inheritdoc />
    public Task<Envelope> SealAsync(string plaintext, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default)
    {
        var dataKey = AesGcmEnvelope.NewDataKey();
        try
        {
            var wrapped = Wrap(_masterKey, dataKey, context);
            return Task.FromResult(AesGcmEnvelope.Seal(dataKey, plaintext, context, wrapped));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <inheritdoc />
    public Task<string> OpenAsync(Envelope envelope, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default)
    {
        var dataKey = Unwrap(_masterKey, AesGcmEnvelope.WrappedKeyBytes(envelope), context);
        try
        {
            return Task.FromResult(AesGcmEnvelope.Open(dataKey, envelope, context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary><c>nonce ‖ AES-GCM(masterKey, dataKey, AAD=context)</c> — 12 + 32 + 16 bytes.</summary>
    internal static byte[] Wrap(byte[] masterKey, byte[] dataKey, IReadOnlyDictionary<string, string> context)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcmEnvelope.NonceBytes);
        return [.. nonce, .. AesGcmEnvelope.Encrypt(masterKey, nonce, dataKey, AesGcmEnvelope.ContextAad(context))];
    }

    /// <summary>The inverse of <see cref="Wrap"/>; throws under any other context.</summary>
    internal static byte[] Unwrap(byte[] masterKey, byte[] wrapped, IReadOnlyDictionary<string, string> context)
    {
        if (wrapped.Length < AesGcmEnvelope.NonceBytes)
        {
            throw new AuthenticationTagMismatchException();
        }

        return AesGcmEnvelope.Decrypt(masterKey, wrapped[..AesGcmEnvelope.NonceBytes], wrapped[AesGcmEnvelope.NonceBytes..], AesGcmEnvelope.ContextAad(context));
    }
}

/// <summary>
/// <see cref="IFilingCredentialSealer"/> over the local wrap (Python's
/// <c>LocalCredentialSealer</c>). Has no open: an API composed with it
/// cannot read what it stores, exactly as the deployed one cannot.
/// </summary>
public sealed class LocalFilingCredentialSealer : IFilingCredentialSealer
{
    private readonly LocalTaxIdCipher _cipher = new();

    /// <inheritdoc />
    public Task<Envelope> SealAsync(string plaintext, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default) =>
        _cipher.SealAsync(plaintext, context, cancellationToken);
}

/// <summary>
/// <see cref="IFilingCredentialOpener"/>, the local wrap's inverse (Python's
/// <c>LocalCredentialOpener</c>). Refuses any context but the sealing one.
/// </summary>
public sealed class LocalFilingCredentialOpener : IFilingCredentialOpener
{
    private readonly LocalTaxIdCipher _cipher = new();

    /// <inheritdoc />
    public Task<string> OpenAsync(Envelope envelope, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default) =>
        _cipher.OpenAsync(envelope, context, cancellationToken);
}
