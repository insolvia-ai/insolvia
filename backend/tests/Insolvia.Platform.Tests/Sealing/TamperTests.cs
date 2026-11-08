using System.Security.Cryptography;
using Amazon.KeyManagementService.Model;
using Insolvia.Core.TaxIds;

namespace Insolvia.Aws.Tests.Sealing;

/// <summary>
/// Every Python-sealed fixture, tampered with every way that matters, is
/// REFUSED — never opened to garbage (the ports' contract: "MUST throw").
/// The refusal is a cryptographic one (the GCM tag, or KMS's unwrap), an
/// unknown scheme, or malformed base64; anything else would be a bug.
/// </summary>
public sealed class TamperTests
{
    private static readonly byte[] OtherKey = SHA256.HashData("a different TEST key"u8);

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var sealedCase in SealedFixtures.Python)
        {
            foreach (var label in Tampers(sealedCase).Keys)
            {
                data.Add(sealedCase.Name, label);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_tampered_envelope_is_refused(string name, string tamper)
    {
        var sealedCase = SealedFixtures.Case(name);
        var error = await Assert.ThrowsAnyAsync<Exception>(Tampers(sealedCase)[tamper]);
        Assert.True(
            error is CryptographicException or InvalidCiphertextException or ArgumentException or FormatException,
            $"refused for the wrong reason: {error.GetType().Name}: {error.Message}");
    }

    [Fact]
    public async Task The_gcm_tag_refuses_another_context_even_when_kms_hands_back_the_key()
    {
        // Two layers: KMS binds the context to the wrapped key, AND the context
        // is the AAD. Simulate a KMS that unwrapped anyway — the tag still says no.
        var sealedCase = SealedFixtures.Case("kms-tax-id-ssa-block");
        var (kms, state) = FakeKms.Create();
        state.DecryptOverride = FakeKms.TestDataKey;
        var swapped = new Dictionary<string, string>(sealedCase.Context) { ["firm_id"] = "00000000-0000-4000-8000-0000000f1a02" };
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => SealedFixtures.OpenAsync(sealedCase, sealedCase.Envelope, swapped, kms));
    }

    private static string Flip(string base64, int index)
    {
        var raw = Convert.FromBase64String(base64);
        raw[index < 0 ? raw.Length + index : index] ^= 0x01;
        return Convert.ToBase64String(raw);
    }

    private static string Truncate(string base64, int drop)
    {
        var raw = Convert.FromBase64String(base64);
        return Convert.ToBase64String(raw[..Math.Max(0, raw.Length - drop)]);
    }

    private static Dictionary<string, Func<Task>> Tampers(SealedCase c)
    {
        var e = c.Envelope;
        var context = c.Context;
        Func<Task> Open(Envelope envelope, IReadOnlyDictionary<string, string>? under = null) =>
            () => SealedFixtures.OpenAsync(c, envelope, under ?? context);
        Dictionary<string, string> With(string key, string value) => new(context) { [key] = value };

        var tampers = new Dictionary<string, Func<Task>>
        {
            ["extra context field"] = Open(e, With("case_id", "x")),
            ["ciphertext bit flipped"] = Open(e with { Ciphertext = Flip(e.Ciphertext, 0) }),
            ["tag bit flipped"] = Open(e with { Ciphertext = Flip(e.Ciphertext, -1) }),
            ["ciphertext truncated by one byte"] = Open(e with { Ciphertext = Truncate(e.Ciphertext, 1) }),
            ["ciphertext emptied"] = Open(e with { Ciphertext = string.Empty }),
            ["nonce bit flipped"] = Open(e with { Nonce = Flip(e.Nonce, 0) }),
            ["nonce truncated"] = Open(e with { Nonce = Truncate(e.Nonce, 1) }),
            ["unknown scheme"] = Open(e with { Scheme = "kms-aes256-gcm/2" }),
            ["whitespace in the base64"] = Open(e with { Ciphertext = e.Ciphertext.Insert(4, "\n") }),
            ["a url-safe character in the base64"] = Open(e with { Nonce = "-" + e.Nonce[1..] }),
        };
        if (context.ContainsKey("firm_id"))
        {
            tampers["firm_id swapped"] = Open(e, With("firm_id", "00000000-0000-4000-8000-0000000f1a02"));
        }

        if (context.ContainsKey("purpose"))
        {
            tampers["purpose changed"] = Open(e, With("purpose", c.Context["purpose"] == TaxIdRules.Purpose ? "filing-credential" : TaxIdRules.Purpose));
        }

        if (context.ContainsKey("attorney_id"))
        {
            tampers["attorney_id swapped"] = Open(e, With("attorney_id", "00000000-0000-4000-8000-00000000b771"));
        }

        if (context.ContainsKey("tax_id_ref"))
        {
            tampers["tax_id_ref replayed under another ref"] = Open(e, With("tax_id_ref", "00000000-0000-4000-8000-0000000000ff"));
        }

        if (context.Count > 0)
        {
            tampers["a context field dropped"] = Open(e, context.Skip(1).ToDictionary());
        }

        switch (c.Kind)
        {
            case "envelope":
                tampers["wrong data key"] = () => SealedFixtures.OpenAsync(c, e, context, key: OtherKey);
                break;
            case "local_tax_id" or "local_credential":
                tampers["wrong master key"] = () => SealedFixtures.OpenAsync(c, e, context, key: OtherKey);
                tampers["wrapped key bit flipped"] = Open(e with { WrappedKey = Flip(e.WrappedKey, -1) });
                tampers["wrapped key from another envelope"] = Open(e with { WrappedKey = Other(c).Envelope.WrappedKey });
                break;
            default:
                tampers["wrong KMS key"] = () => SealedFixtures.OpenAsync(c, e, context, FakeKms.Create(wrappingKey: OtherKey).Client);
                tampers["KMS returns a different data key"] = () =>
                {
                    var (kms, state) = FakeKms.Create();
                    state.DecryptOverride = OtherKey;
                    return SealedFixtures.OpenAsync(c, e, context, kms);
                };
                tampers["wrapped key bit flipped"] = Open(e with { WrappedKey = Flip(e.WrappedKey, -1) });
                tampers["wrapped key from another envelope"] = Open(e with { WrappedKey = Other(c).Envelope.WrappedKey });
                break;
        }

        return tampers;
    }

    // Another case's envelope of a kind that shares this one's wrapping key.
    private static SealedCase Other(SealedCase c) =>
        SealedFixtures.Python.First(o => o.Name != c.Name && (o.Kind.StartsWith("kms_", StringComparison.Ordinal) == c.Kind.StartsWith("kms_", StringComparison.Ordinal)) && o.Kind != "envelope");
}
