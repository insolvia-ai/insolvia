using System.Text.Json.Nodes;
using Amazon.KeyManagementService;
using Insolvia.Aws.Sealing;
using Insolvia.Core.TaxIds;

namespace Insolvia.Aws.Tests.Sealing;

/// <summary>One case of Fixtures/python-sealed.json (or dotnet-sealed.json) — the schema sealed_envelopes.py writes.</summary>
internal sealed record SealedCase(
    string Name,
    string Kind,
    string? KeyId,
    Dictionary<string, string> Context,
    string Plaintext,
    string Aad,
    Envelope Envelope,
    byte[]? FixedNonce,
    JsonObject? GenerateDataKeyRequest);

/// <summary>Reads the committed fixtures and opens or seals a case the way its kind says.</summary>
internal static class SealedFixtures
{
    public const string WrappedKeyStub = "stub-wrapped-key";

    private static readonly Lazy<IReadOnlyList<SealedCase>> PythonCases = new(() => Load("python-sealed.json"));

    public static IReadOnlyList<SealedCase> Python => PythonCases.Value;

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static IReadOnlyList<SealedCase> Load(string name)
    {
        var document = JsonNode.Parse(File.ReadAllText(FixturePath(name)))!.AsObject();
        Assert.Equal(Convert.ToBase64String(FakeKms.TestDataKey), document["test_data_key"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(FakeKms.StubWrappingKey), document["stub_kms_wrapping_key"]!.GetValue<string>());
        return [.. document["cases"]!.AsArray().Select(node => Parse(node!.AsObject()))];
    }

    public static SealedCase Case(string name) => Python.Single(c => c.Name == name);

    public static Dictionary<string, string> Context(JsonObject node)
    {
        // Insertion order is kept, so the canonical form's sort is exercised.
        var context = new Dictionary<string, string>();
        foreach (var (key, value) in node)
        {
            context[key] = value!.GetValue<string>();
        }

        return context;
    }

    public static JsonObject ToJson(SealedCase sealedCase)
    {
        var context = new JsonObject();
        foreach (var (key, value) in sealedCase.Context)
        {
            context[key] = value;
        }

        var node = new JsonObject
        {
            ["name"] = sealedCase.Name,
            ["kind"] = sealedCase.Kind,
        };
        if (sealedCase.KeyId is not null)
        {
            node["key_id"] = sealedCase.KeyId;
        }

        node["context"] = context;
        node["plaintext"] = sealedCase.Plaintext;
        node["aad"] = sealedCase.Aad;
        if (sealedCase.FixedNonce is not null)
        {
            node["fixed_nonce"] = Convert.ToBase64String(sealedCase.FixedNonce);
        }

        node["envelope"] = new JsonObject
        {
            ["scheme"] = sealedCase.Envelope.Scheme,
            ["ciphertext"] = sealedCase.Envelope.Ciphertext,
            ["nonce"] = sealedCase.Envelope.Nonce,
            ["wrapped_key"] = sealedCase.Envelope.WrappedKey,
        };
        if (sealedCase.GenerateDataKeyRequest is not null)
        {
            node["generate_data_key_request"] = sealedCase.GenerateDataKeyRequest.DeepClone();
        }

        return node;
    }

    /// <summary>Open <paramref name="envelope"/> as this case's kind would, under <paramref name="context"/>.</summary>
    public static Task<string> OpenAsync(SealedCase sealedCase, Envelope envelope, IReadOnlyDictionary<string, string> context, IAmazonKeyManagementService? kms = null, byte[]? key = null)
    {
        kms ??= FakeKms.Create().Client;
        return sealedCase.Kind switch
        {
            "kms_tax_id" => new KmsTaxIdCipher(kms, sealedCase.KeyId!).OpenAsync(envelope, context),
            "kms_credential" => new KmsFilingCredentialOpener(kms, sealedCase.KeyId!).OpenAsync(envelope, context),
            "local_tax_id" => new LocalTaxIdCipher(key).OpenAsync(envelope, context),
            "local_credential" when key is null => new LocalFilingCredentialOpener().OpenAsync(envelope, context),
            "local_credential" => new LocalTaxIdCipher(key).OpenAsync(envelope, context),
            "envelope" => Task.FromResult(AesGcmEnvelope.Open(key ?? FakeKms.TestDataKey, envelope, context)),
            _ => throw new InvalidOperationException($"unknown kind {sealedCase.Kind}"),
        };
    }

    /// <summary>Seal this case's plaintext under its context with the .NET code, recording what KMS was asked.</summary>
    public static async Task<SealedCase> SealAsync(SealedCase sealedCase)
    {
        var (kms, state) = FakeKms.Create();
        var envelope = sealedCase.Kind switch
        {
            "kms_tax_id" => await new KmsTaxIdCipher(kms, sealedCase.KeyId!).SealAsync(sealedCase.Plaintext, sealedCase.Context),
            "kms_credential" => await new KmsFilingCredentialSealer(kms, sealedCase.KeyId!).SealAsync(sealedCase.Plaintext, sealedCase.Context),
            "local_tax_id" => await new LocalTaxIdCipher().SealAsync(sealedCase.Plaintext, sealedCase.Context),
            "local_credential" => await new LocalFilingCredentialSealer().SealAsync(sealedCase.Plaintext, sealedCase.Context),
            "envelope" => AesGcmEnvelope.Seal(FakeKms.TestDataKey, sealedCase.Plaintext, sealedCase.Context, System.Text.Encoding.ASCII.GetBytes(WrappedKeyStub), sealedCase.FixedNonce!),
            _ => throw new InvalidOperationException($"unknown kind {sealedCase.Kind}"),
        };
        JsonObject? request = null;
        if (sealedCase.Kind.StartsWith("kms_", StringComparison.Ordinal))
        {
            var generate = Assert.IsType<Amazon.KeyManagementService.Model.GenerateDataKeyRequest>(Assert.Single(state.Requests));
            var context = new JsonObject();
            foreach (var (key, value) in generate.EncryptionContext)
            {
                context[key] = value;
            }

            request = new JsonObject { ["KeyId"] = generate.KeyId, ["KeySpec"] = generate.KeySpec.Value, ["EncryptionContext"] = context };
        }

        return sealedCase with
        {
            Aad = System.Text.Encoding.ASCII.GetString(AesGcmEnvelope.ContextAad(sealedCase.Context)),
            Envelope = envelope,
            GenerateDataKeyRequest = request,
        };
    }

    private static SealedCase Parse(JsonObject node)
    {
        var envelope = node["envelope"]!.AsObject();
        return new SealedCase(
            node["name"]!.GetValue<string>(),
            node["kind"]!.GetValue<string>(),
            node["key_id"]?.GetValue<string>(),
            Context(node["context"]!.AsObject()),
            node["plaintext"]!.GetValue<string>(),
            node["aad"]!.GetValue<string>(),
            new Envelope(
                envelope["scheme"]!.GetValue<string>(),
                envelope["ciphertext"]!.GetValue<string>(),
                envelope["nonce"]!.GetValue<string>(),
                envelope["wrapped_key"]!.GetValue<string>()),
            node["fixed_nonce"] is { } nonce ? Convert.FromBase64String(nonce.GetValue<string>()) : null,
            node["generate_data_key_request"]?.AsObject());
    }
}
