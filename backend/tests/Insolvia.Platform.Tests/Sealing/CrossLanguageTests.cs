using System.Text;
using Amazon.KeyManagementService.Model;
using Insolvia.Aws.Sealing;

namespace Insolvia.Aws.Tests.Sealing;

/// <summary>
/// THE CROSS-LANGUAGE PROOF (plan 18.12: "an envelope sealed by Python opens
/// in .NET and vice versa"). Fixtures/python-sealed.json was sealed by the
/// Python backend's own envelope code before 18.23 deleted it; each case is
/// opened here and re-sealed here. dotnet-sealed.json is the .NET half, which
/// the Python check opened before the cut-over. Both are now frozen evidence:
/// an envelope sealed before the switch still opens, byte for byte.
/// </summary>
public sealed class CrossLanguageTests
{
    public static TheoryData<string> AllCases() => [.. SealedFixtures.Python.Select(c => c.Name)];

    public static TheoryData<string> KmsCases() => [.. SealedFixtures.Python.Where(c => c.Kind.StartsWith("kms_", StringComparison.Ordinal)).Select(c => c.Name)];

    public static TheoryData<string> FixedNonceCases() => [.. SealedFixtures.Python.Where(c => c.FixedNonce is not null).Select(c => c.Name)];

    [Fact]
    public void The_fixture_covers_every_kind()
    {
        Assert.Equal(
            ["envelope", "kms_credential", "kms_tax_id", "local_credential", "local_tax_id"],
            SealedFixtures.Python.Select(c => c.Kind).Distinct().Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AllCases))]
    public void The_canonical_context_is_byte_for_byte_pythons(string name)
    {
        var sealedCase = SealedFixtures.Case(name);
        Assert.Equal(sealedCase.Aad, Encoding.ASCII.GetString(AesGcmEnvelope.ContextAad(sealedCase.Context)));
    }

    [Theory]
    [MemberData(nameof(AllCases))]
    public async Task An_envelope_sealed_by_python_opens_in_dotnet(string name)
    {
        var sealedCase = SealedFixtures.Case(name);
        var (kms, state) = FakeKms.Create();
        Assert.Equal(sealedCase.Plaintext, await SealedFixtures.OpenAsync(sealedCase, sealedCase.Envelope, sealedCase.Context, kms));
        if (sealedCase.KeyId is not null)
        {
            // What the adapter sends on open: KeyId, the wrapped blob, the same context.
            var decrypt = Assert.IsType<DecryptRequest>(Assert.Single(state.Requests));
            Assert.Equal(sealedCase.KeyId, decrypt.KeyId);
            Assert.Equal(sealedCase.Context, decrypt.EncryptionContext);
            Assert.Equal(Convert.FromBase64String(sealedCase.Envelope.WrappedKey), decrypt.CiphertextBlob.ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(FixedNonceCases))]
    public void Under_the_same_key_and_nonce_dotnet_seals_the_same_bytes_as_python(string name)
    {
        var sealedCase = SealedFixtures.Case(name);
        var mine = AesGcmEnvelope.Seal(FakeKms.TestDataKey, sealedCase.Plaintext, sealedCase.Context, Encoding.ASCII.GetBytes(SealedFixtures.WrappedKeyStub), sealedCase.FixedNonce!);
        Assert.Equal(sealedCase.Envelope, mine);
    }

    [Theory]
    [MemberData(nameof(KmsCases))]
    public async Task Dotnet_asks_kms_for_a_data_key_exactly_as_python_did(string name)
    {
        var sealedCase = SealedFixtures.Case(name);
        var python = sealedCase.GenerateDataKeyRequest!;
        var dotnet = (await SealedFixtures.SealAsync(sealedCase)).GenerateDataKeyRequest!;
        Assert.Equal(python["KeyId"]!.GetValue<string>(), dotnet["KeyId"]!.GetValue<string>());
        Assert.Equal("AES_256", python["KeySpec"]!.GetValue<string>());
        Assert.Equal("AES_256", dotnet["KeySpec"]!.GetValue<string>());
        Assert.Equal(
            SealedFixtures.Context(python["EncryptionContext"]!.AsObject()),
            SealedFixtures.Context(dotnet["EncryptionContext"]!.AsObject()));
        Assert.Equal(sealedCase.Context, SealedFixtures.Context(dotnet["EncryptionContext"]!.AsObject()));
    }

    [Theory]
    [MemberData(nameof(AllCases))]
    public async Task What_dotnet_seals_dotnet_opens(string name)
    {
        var resealed = await SealedFixtures.SealAsync(SealedFixtures.Case(name));
        Assert.Equal(resealed.Plaintext, await SealedFixtures.OpenAsync(resealed, resealed.Envelope, resealed.Context));
    }

    [Fact]
    public async Task The_committed_dotnet_fixture_covers_the_same_cases_and_opens_here()
    {
        var dotnet = SealedFixtures.Load("dotnet-sealed.json");
        Assert.Equal(SealedFixtures.Python.Select(c => c.Name), dotnet.Select(c => c.Name));
        foreach (var sealedCase in dotnet)
        {
            Assert.Equal(sealedCase.Plaintext, await SealedFixtures.OpenAsync(sealedCase, sealedCase.Envelope, sealedCase.Context));
        }
    }
}
