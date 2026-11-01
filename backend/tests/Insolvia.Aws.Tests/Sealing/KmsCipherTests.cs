using System.Text.Json.Nodes;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Insolvia.Aws.Sealing;
using Insolvia.Core.AccessLog;
using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;

namespace Insolvia.Aws.Tests.Sealing;

/// <summary>
/// The KMS ciphers against a recording fake KMS — translated from the KMS
/// cases of test_tax_ids.py and test_filing_credentials.py: the requests are
/// asserted exactly as the Python adapters sent them.
/// </summary>
public sealed class KmsCipherTests
{
    private const string CaseKey = "alias/insolvia-staging-cases";
    private const string VaultKey = "alias/insolvia-staging-filing-credentials";
    private const string Firm = "00000000-0000-4000-8000-0000000f1a01";
    private const string OtherFirm = "00000000-0000-4000-8000-0000000f1a02";
    private const string Attorney = "00000000-0000-4000-8000-00000000a771";
    private const string OtherAttorney = "00000000-0000-4000-8000-00000000b771";
    private const string Ssn = "987654320";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_tax_id_cipher_seals_and_opens_with_exactly_pythons_requests()
    {
        var (kms, state) = FakeKms.Create();
        var cipher = new KmsTaxIdCipher(kms, CaseKey);
        var context = TaxIdRules.EncryptionContext(Firm, "r1");
        var envelope = await cipher.SealAsync(Ssn, context, Ct);
        Assert.Equal(Ssn, await cipher.OpenAsync(envelope, context, Ct));

        Assert.Collection(
            state.Requests,
            request =>
            {
                var generate = Assert.IsType<GenerateDataKeyRequest>(request);
                Assert.Equal(CaseKey, generate.KeyId);
                Assert.Equal(DataKeySpec.AES_256, generate.KeySpec);
                Assert.Null(generate.NumberOfBytes);
                Assert.Equal(new Dictionary<string, string> { ["purpose"] = "debtor-tax-id", ["firm_id"] = Firm, ["tax_id_ref"] = "r1" }, generate.EncryptionContext);
            },
            request =>
            {
                var decrypt = Assert.IsType<DecryptRequest>(request);
                Assert.Equal(CaseKey, decrypt.KeyId);
                Assert.Equal(Convert.FromBase64String(envelope.WrappedKey), decrypt.CiphertextBlob.ToArray());
                Assert.Equal(new Dictionary<string, string> { ["purpose"] = "debtor-tax-id", ["firm_id"] = Firm, ["tax_id_ref"] = "r1" }, decrypt.EncryptionContext);
            });
        Assert.Equal(TaxIdRules.SealedScheme, envelope.Scheme);
        Assert.DoesNotContain(Ssn, envelope.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tax_id_cipher_refuses_another_firm_at_the_unwrap()
    {
        var (kms, _) = FakeKms.Create(dataKey: AesGcmEnvelope.NewDataKey());
        var cipher = new KmsTaxIdCipher(kms, CaseKey);
        var envelope = await cipher.SealAsync(Ssn, TaxIdRules.EncryptionContext(Firm, "r1"), Ct);
        await Assert.ThrowsAsync<InvalidCiphertextException>(() => cipher.OpenAsync(envelope, TaxIdRules.EncryptionContext(OtherFirm, "r1"), Ct));
        await Assert.ThrowsAsync<InvalidCiphertextException>(() => cipher.OpenAsync(envelope, TaxIdRules.EncryptionContext(Firm, "r2"), Ct));
    }

    [Fact]
    public async Task The_sealer_only_ever_generates_a_data_key()
    {
        var (kms, state) = FakeKms.Create();
        var context = FilingCredentialRules.EncryptionContext(Firm, Attorney, "c1");
        await new KmsFilingCredentialSealer(kms, VaultKey).SealAsync("plaintext", context, Ct);
        var generate = Assert.IsType<GenerateDataKeyRequest>(Assert.Single(state.Requests));
        Assert.Equal(VaultKey, generate.KeyId);
        Assert.Equal(DataKeySpec.AES_256, generate.KeySpec);
        Assert.Equal(
            new Dictionary<string, string> { ["purpose"] = "filing-credential", ["firm_id"] = Firm, ["attorney_id"] = Attorney, ["credential_id"] = "c1" },
            generate.EncryptionContext);
    }

    [Theory]
    [InlineData(typeof(KmsFilingCredentialSealer))]
    [InlineData(typeof(LocalFilingCredentialSealer))]
    public void The_sealer_has_no_way_to_open(Type sealer)
    {
        // ADR 0024: the API is composed with a sealer and nothing that opens.
        Assert.False(typeof(IFilingCredentialOpener).IsAssignableFrom(sealer));
        Assert.False(typeof(ITaxIdCipher).IsAssignableFrom(sealer));
        Assert.DoesNotContain(sealer.GetMethods(), method => method.Name.StartsWith("Open", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(KmsFilingCredentialOpener))]
    [InlineData(typeof(LocalFilingCredentialOpener))]
    public void The_opener_cannot_seal(Type opener) =>
        Assert.False(typeof(IFilingCredentialSealer).IsAssignableFrom(opener));

    [Fact]
    public async Task The_kms_opener_opens_under_the_sealing_context_only()
    {
        var (kms, state) = FakeKms.Create(dataKey: AesGcmEnvelope.NewDataKey());
        var context = FilingCredentialRules.EncryptionContext(Firm, Attorney, "c1");
        var envelope = await new KmsFilingCredentialSealer(kms, VaultKey).SealAsync("plaintext", context, Ct);
        var opener = new KmsFilingCredentialOpener(kms, VaultKey);
        Assert.Equal("plaintext", await opener.OpenAsync(envelope, context, Ct));
        Assert.Equal(VaultKey, Assert.IsType<DecryptRequest>(state.Requests[^1]).KeyId);
        await Assert.ThrowsAsync<InvalidCiphertextException>(
            () => opener.OpenAsync(envelope, FilingCredentialRules.EncryptionContext(Firm, OtherAttorney, "c1"), Ct));
    }

    [Fact]
    public async Task Every_open_is_logged_before_kms_is_asked_to_decrypt()
    {
        // The access row belongs to Core's OpenAsync (it holds the filing and
        // the purpose); the adapter must not be reached before it is written.
        var timeline = new List<string>();
        var (kms, _) = FakeKms.Create(dataKey: AesGcmEnvelope.NewDataKey(), timeline: timeline);
        var log = new TimelineLog(timeline);
        var store = new MemoryFilingCredentialStore();
        var authorizations = new MemoryFilingAuthorizationStore();
        await FilingAuthorizationRules.SignAsync(
            new JsonObject
            {
                ["text_version"] = FilingAuthorizationRules.TextVersion,
                ["text_digest"] = FilingAuthorizationRules.TextDigest(FilingAuthorizationRules.AuthorizationText()),
            },
            Firm,
            Attorney,
            4_102_444_800,
            DateTimeOffset.FromUnixTimeSeconds(4_102_444_830),
            authorizations,
            log,
            cancellationToken: Ct);
        var enrolment = FilingCredentialRules.ParseEnrolment(new JsonObject { ["login"] = "FAKE-ECF-USER", ["password"] = "FAKE-ECF-PASSWORD-not-a-real-one", ["totp_seed"] = "JBSWY3DPEHPK3PXP" });
        var credential = await FilingCredentialRules.EnrolAsync(enrolment, Firm, Attorney, new KmsFilingCredentialSealer(kms, VaultKey), store, log, authorizations, cancellationToken: Ct);
        timeline.Clear();

        var secret = await FilingCredentialRules.OpenAsync(Firm, Attorney, credential.CredentialId, "filing-0001", "sign_in", new KmsFilingCredentialOpener(kms, VaultKey), store, authorizations, log, cancellationToken: Ct);
        Assert.Equal(enrolment.Secret, secret);
        Assert.Equal(["log:CredentialOpen", "kms:Decrypt"], timeline);

        // Revoked: still logged, and KMS is never asked.
        await FilingCredentialRules.RevokeAsync(credential.CredentialId, Firm, Attorney, store, log, cancellationToken: Ct);
        timeline.Clear();
        await Assert.ThrowsAsync<CredentialUnavailableException>(
            () => FilingCredentialRules.OpenAsync(Firm, Attorney, credential.CredentialId, "filing-0001", "sign_in", new KmsFilingCredentialOpener(kms, VaultKey), store, authorizations, log, cancellationToken: Ct));
        Assert.Equal(["log:CredentialOpen"], timeline);
    }

    [Fact]
    public async Task A_tax_id_stored_through_core_opens_through_the_kms_cipher()
    {
        var (kms, _) = FakeKms.Create(dataKey: AesGcmEnvelope.NewDataKey());
        var cipher = new KmsTaxIdCipher(kms, CaseKey);
        var context = TaxIdRules.EncryptionContext(Firm, TaxIdRules.NewRef());
        var envelope = await cipher.SealAsync(Ssn, context, Ct);
        Assert.Equal(Ssn, await new KmsTaxIdCipher(kms, CaseKey).OpenAsync(envelope, context, Ct));
    }

    [Fact]
    public void A_cipher_needs_a_key_and_a_client()
    {
        var (kms, _) = FakeKms.Create();
        Assert.Throws<ArgumentException>(() => new KmsTaxIdCipher(kms, ""));
        Assert.Throws<ArgumentNullException>(() => new KmsFilingCredentialSealer(null!, VaultKey));
        Assert.Throws<ArgumentException>(() => new KmsFilingCredentialOpener(kms, ""));
    }

    // ── The local twins ─────────────────────────────────────────────

    [Fact]
    public void The_local_master_key_is_pythons()
    {
        // hashlib.sha256(b"insolvia local tax-id master key - tests and the bare dev server only").hexdigest()
        Assert.Equal("ddc04f245aa0dacdc126fb089082d2724f8953f8b5d9c4b1de106f21e79729a6", Convert.ToHexStringLower(LocalTaxIdCipher.LocalMasterKey));
    }

    [Fact]
    public async Task The_local_cipher_refuses_another_context()
    {
        var cipher = new LocalTaxIdCipher();
        var envelope = await cipher.SealAsync(Ssn, TaxIdRules.EncryptionContext(Firm, "r1"), Ct);
        Assert.Equal(Ssn, await cipher.OpenAsync(envelope, TaxIdRules.EncryptionContext(Firm, "r1"), Ct));
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => cipher.OpenAsync(envelope, TaxIdRules.EncryptionContext(OtherFirm, "r1"), Ct));
    }

    private sealed class TimelineLog(List<string> timeline) : IAccessLog
    {
        public Task RecordAsync(AccessEvent accessEvent, CancellationToken cancellationToken = default)
        {
            timeline.Add($"log:{accessEvent.Action}");
            return Task.CompletedTask;
        }
    }
}
