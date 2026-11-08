using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime;
using Insolvia.Aws.Sealing;

namespace Insolvia.Aws.Tests.Sealing;

/// <summary>
/// <c>GenerateDataKey</c> / <c>Decrypt</c> as KMS answers them, with no
/// network — the same stub the Python generator of Fixtures/python-sealed.json used.
/// The data key is wrapped under a fixed stub key with the encryption context
/// as associated data, so a Decrypt under any other context is refused here
/// as KMS refuses it. Every request is recorded; any other KMS call fails the
/// test, which is how "the sealer only ever generates a data key" is pinned.
/// </summary>
internal static class FakeKms
{
    // TEST CONSTANTS — they secure nothing and are committed on purpose; the
    // same sentences the Python generator used, so the fixtures' keys recompute.
    public static readonly byte[] TestDataKey = SHA256.HashData(
        Encoding.ASCII.GetBytes("insolvia sealed-envelope fixture data key - TEST CONSTANT, never a real key"));

    public static readonly byte[] StubWrappingKey = SHA256.HashData(
        Encoding.ASCII.GetBytes("insolvia stub KMS wrapping key - TEST CONSTANT, never a real key"));

    public static (IAmazonKeyManagementService Client, FakeKmsState State) Create(
        byte[]? dataKey = null,
        byte[]? wrappingKey = null,
        List<string>? timeline = null)
    {
        var client = DispatchProxy.Create<IAmazonKeyManagementService, FakeKmsProxy>();
        var proxy = (FakeKmsProxy)(object)client;
        proxy.State = new FakeKmsState(dataKey ?? TestDataKey, wrappingKey ?? StubWrappingKey, timeline ?? []);
        return (client, proxy.State);
    }
}

internal sealed class FakeKmsState(byte[] dataKey, byte[] wrappingKey, List<string> timeline)
{
    public List<AmazonWebServiceRequest> Requests { get; } = [];

    public List<string> Timeline { get; } = timeline;

    /// <summary>When set, Decrypt hands back this key instead of unwrapping — "KMS returned the wrong key".</summary>
    public byte[]? DecryptOverride { get; set; }

    public GenerateDataKeyResponse GenerateDataKey(GenerateDataKeyRequest request)
    {
        Requests.Add(request);
        Timeline.Add("kms:GenerateDataKey");
        return new GenerateDataKeyResponse
        {
            KeyId = request.KeyId,
            Plaintext = new MemoryStream([.. dataKey]),
            CiphertextBlob = new MemoryStream(LocalTaxIdCipher.Wrap(wrappingKey, dataKey, request.EncryptionContext)),
        };
    }

    public DecryptResponse Decrypt(DecryptRequest request)
    {
        Requests.Add(request);
        Timeline.Add("kms:Decrypt");
        if (DecryptOverride is not null)
        {
            return new DecryptResponse { KeyId = request.KeyId, Plaintext = new MemoryStream([.. DecryptOverride]) };
        }

        byte[] key;
        try
        {
            key = LocalTaxIdCipher.Unwrap(wrappingKey, request.CiphertextBlob.ToArray(), request.EncryptionContext);
        }
        catch (CryptographicException)
        {
            // What KMS answers for a blob that does not unwrap under this context.
            throw new InvalidCiphertextException("The ciphertext refers to a customer master key that does not exist, does not exist in this region, or you are not allowed to access.");
        }

        return new DecryptResponse { KeyId = request.KeyId, Plaintext = new MemoryStream(key) };
    }
}

/// <summary>The proxy behind the interface. Public with a parameterless constructor because <see cref="DispatchProxy"/> requires it.</summary>
public class FakeKmsProxy : DispatchProxy
{
    internal FakeKmsState State { get; set; } = null!;

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        return targetMethod.Name switch
        {
            nameof(IAmazonKeyManagementService.GenerateDataKeyAsync) => Task.FromResult(State.GenerateDataKey((GenerateDataKeyRequest)args![0]!)),
            nameof(IAmazonKeyManagementService.DecryptAsync) => Task.FromResult(State.Decrypt((DecryptRequest)args![0]!)),
            nameof(IDisposable.Dispose) => null,
            _ => throw new InvalidOperationException($"the fake KMS was asked for {targetMethod.Name}, which no cipher should call"),
        };
    }
}
