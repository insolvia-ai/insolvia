using System.Security.Cryptography;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Insolvia.Core.TaxIds;

namespace Insolvia.Aws.Sealing;

/// <summary>
/// The two KMS verbs every sealed value goes through, as the Python adapters
/// called them (adapters/aws/tax_id_cipher.py, filing_credentials.py):
/// <c>GenerateDataKey(KeyId, KeySpec=AES_256, EncryptionContext)</c> to seal,
/// <c>Decrypt(KeyId, CiphertextBlob, EncryptionContext)</c> to open.
/// </summary>
/// <remarks>
/// <c>KeyId</c> is passed on Decrypt too. KMS does not need it (the wrapped
/// blob names its key), but passing it makes "this was wrapped under THIS key
/// and no other" a check KMS performs rather than one this code assumes. The
/// plaintext data key is zeroed as soon as the AES-GCM step is done with it.
/// </remarks>
internal static class KmsDataKeys
{
    public static async Task<Envelope> SealAsync(
        IAmazonKeyManagementService kms,
        string keyId,
        string plaintext,
        IReadOnlyDictionary<string, string> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var response = await kms.GenerateDataKeyAsync(
            new GenerateDataKeyRequest
            {
                KeyId = keyId,
                KeySpec = DataKeySpec.AES_256,
                EncryptionContext = new Dictionary<string, string>(context),
            },
            cancellationToken).ConfigureAwait(false);
        var dataKey = response.Plaintext.ToArray();
        try
        {
            return AesGcmEnvelope.Seal(dataKey, plaintext, context, response.CiphertextBlob.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            Scrub(response.Plaintext);
        }
    }

    public static async Task<string> OpenAsync(
        IAmazonKeyManagementService kms,
        string keyId,
        Envelope envelope,
        IReadOnlyDictionary<string, string> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var blob = new MemoryStream(AesGcmEnvelope.WrappedKeyBytes(envelope));
        var response = await kms.DecryptAsync(
            new DecryptRequest
            {
                KeyId = keyId,
                CiphertextBlob = blob,
                EncryptionContext = new Dictionary<string, string>(context),
            },
            cancellationToken).ConfigureAwait(false);
        var dataKey = response.Plaintext.ToArray();
        try
        {
            return AesGcmEnvelope.Open(dataKey, envelope, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            Scrub(response.Plaintext);
        }
    }

    // ToArray() copies: zeroing the copy leaves the plaintext key in the
    // response's own buffer. Zero that too where the stream exposes it, and
    // dispose it either way.
    private static void Scrub(MemoryStream? plaintext)
    {
        if (plaintext is null)
        {
            return;
        }

        if (plaintext.TryGetBuffer(out var buffer))
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
        }

        plaintext.Dispose();
    }
}
