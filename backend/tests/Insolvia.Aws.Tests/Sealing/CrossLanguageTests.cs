using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.KeyManagementService.Model;
using Insolvia.Aws.Sealing;

namespace Insolvia.Aws.Tests.Sealing;

/// <summary>
/// THE CROSS-LANGUAGE PROOF (plan 18.12: "an envelope sealed by Python opens
/// in .NET and vice versa"). Fixtures/python-sealed.json is sealed by the real
/// insolvia_core code (Fixtures/sealed_envelopes.py generate); each case is
/// opened here, re-sealed here, and the .NET seals are exported to
/// dotnet-sealed.json, which <c>sealed_envelopes.py check</c> opens in Python.
/// </summary>
public sealed class CrossLanguageTests
{
    /// <summary>Set to a path to (re)write the .NET-sealed fixture the Python check opens.</summary>
    private const string ExportVariable = "INSOLVIA_EXPORT_DOTNET_SEALED";

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
            // What Python's adapter sends on open: KeyId, the wrapped blob, the same context.
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

    /// <summary>
    /// Seals every Python case with the .NET code. Writes dotnet-sealed.json
    /// only when <c>INSOLVIA_EXPORT_DOTNET_SEALED</c> names a path:
    /// <c>INSOLVIA_EXPORT_DOTNET_SEALED=$PWD/tests/Insolvia.Aws.Tests/Fixtures/dotnet-sealed.json dotnet test tests/Insolvia.Aws.Tests</c>.
    /// </summary>
    [Fact]
    public async Task Dotnet_seals_every_case_for_the_python_check()
    {
        var cases = new JsonArray();
        foreach (var sealedCase in SealedFixtures.Python)
        {
            cases.Add(SealedFixtures.ToJson(await SealedFixtures.SealAsync(sealedCase)));
        }

        Assert.Equal(SealedFixtures.Python.Count, cases.Count);
        var target = Environment.GetEnvironmentVariable(ExportVariable);
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        var document = new JsonObject
        {
            ["_comment"] = "Sealed by Insolvia.Aws (.NET) with TEST CONSTANT keys - synthetic data only. Opened by: python sealed_envelopes.py check",
            ["test_data_key"] = Convert.ToBase64String(FakeKms.TestDataKey),
            ["stub_kms_wrapping_key"] = Convert.ToBase64String(FakeKms.StubWrappingKey),
            ["cases"] = cases,
        };
        await File.WriteAllTextAsync(target, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", TestContext.Current.CancellationToken);
    }
}
