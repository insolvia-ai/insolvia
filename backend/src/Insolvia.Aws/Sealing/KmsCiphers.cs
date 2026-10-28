using Amazon.KeyManagementService;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;

namespace Insolvia.Aws.Sealing;

/// <summary>
/// <see cref="ITaxIdCipher"/> over the environment's case key (Python's
/// <c>KmsTaxIdCipher</c>). Seal is one <c>GenerateDataKey</c> and the shared
/// AES-GCM seal; open is one <c>Decrypt</c> under the same context and the
/// shared open. KMS refuses the unwrap under any other context — the replay
/// protection — and the IAM grant conditions both verbs on
/// <c>kms:EncryptionContext:purpose = debtor-tax-id</c>.
/// </summary>
/// <param name="kms">The KMS client.</param>
/// <param name="keyId">The case key — <see cref="KeyAliases.CaseKey"/>.</param>
public sealed class KmsTaxIdCipher(IAmazonKeyManagementService kms, string keyId) : ITaxIdCipher
{
    private readonly IAmazonKeyManagementService _kms = kms ?? throw new ArgumentNullException(nameof(kms));

    /// <summary>The key every data key is generated under and unwrapped against.</summary>
    public string KeyId { get; } = string.IsNullOrEmpty(keyId) ? throw new ArgumentException("a key id is required", nameof(keyId)) : keyId;

    /// <inheritdoc />
    public Task<Envelope> SealAsync(string plaintext, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default) =>
        KmsDataKeys.SealAsync(_kms, KeyId, plaintext, context, cancellationToken);

    /// <inheritdoc />
    public Task<string> OpenAsync(Envelope envelope, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default) =>
        KmsDataKeys.OpenAsync(_kms, KeyId, envelope, context, cancellationToken);
}

/// <summary>
/// <see cref="IFilingCredentialSealer"/> — one <c>GenerateDataKey</c> under the
/// vault key and the shared AES-GCM seal (Python's <c>KmsCredentialSealer</c>).
/// The API's whole use of that key, and deliberately a different type from
/// <see cref="KmsFilingCredentialOpener"/> with no open of its own: the API
/// host is composed with this and holds nothing that could open what it seals
/// (ADR 0024).
/// </summary>
/// <param name="kms">The KMS client.</param>
/// <param name="keyId">The vault key — <see cref="KeyAliases.FilingCredentialsKey"/>.</param>
public sealed class KmsFilingCredentialSealer(IAmazonKeyManagementService kms, string keyId) : IFilingCredentialSealer
{
    private readonly IAmazonKeyManagementService _kms = kms ?? throw new ArgumentNullException(nameof(kms));

    /// <summary>The key every data key is generated under.</summary>
    public string KeyId { get; } = string.IsNullOrEmpty(keyId) ? throw new ArgumentException("a key id is required", nameof(keyId)) : keyId;

    /// <inheritdoc />
    public Task<Envelope> SealAsync(string plaintext, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default) =>
        KmsDataKeys.SealAsync(_kms, KeyId, plaintext, context, cancellationToken);
}

/// <summary>
/// <see cref="IFilingCredentialOpener"/> — one <c>Decrypt</c> of the wrapped
/// data key under the sealing context, then the shared AES-GCM open (Python's
/// <c>KmsCredentialOpener</c>). The filing worker's alone: the key policy
/// denies Decrypt to every other principal, so composed anywhere else it gets
/// <c>AccessDeniedException</c> before any key material moves.
/// </summary>
/// <remarks>
/// The <c>credential.open</c> access row is NOT written here: Core's
/// <c>FilingCredentialRules.OpenAsync</c> writes it before it calls this — the
/// one place the plaintext is asked for, holding the filing and the purpose
/// this type never sees — exactly as in Python. A second row here would
/// double every open in the log. The ordering is pinned by a test.
/// </remarks>
/// <param name="kms">The KMS client.</param>
/// <param name="keyId">The vault key — <see cref="KeyAliases.FilingCredentialsKey"/>.</param>
public sealed class KmsFilingCredentialOpener(IAmazonKeyManagementService kms, string keyId) : IFilingCredentialOpener
{
    private readonly IAmazonKeyManagementService _kms = kms ?? throw new ArgumentNullException(nameof(kms));

    /// <summary>The key every wrapped blob must have been wrapped under.</summary>
    public string KeyId { get; } = string.IsNullOrEmpty(keyId) ? throw new ArgumentException("a key id is required", nameof(keyId)) : keyId;

    /// <inheritdoc />
    public Task<string> OpenAsync(Envelope envelope, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default) =>
        KmsDataKeys.OpenAsync(_kms, KeyId, envelope, context, cancellationToken);
}
