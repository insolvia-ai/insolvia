using System.Text.Json.Nodes;
using Insolvia.Core.Access;
using Insolvia.Core.Cases;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;

namespace Insolvia.Core.Tests;

/// <summary>Builders the case-half suites share. Every identifier is obviously fake.</summary>
internal static class CaseFixtures
{
    public const string ClientA = "00000000-0000-4000-8000-0000000c11a0";
    public const string ClientB = "00000000-0000-4000-8000-0000000c11b0";

    /// <summary>A Chapter 7 opening in the Middle District of Florida, Tampa — Python's <c>OPENING</c>.</summary>
    public static JsonObject Opening(params string[] clientIds)
    {
        var ids = new JsonArray();
        foreach (var id in clientIds.Length == 0 ? [ClientA] : clientIds)
        {
            ids.Add(id);
        }

        return new JsonObject { ["chapter"] = 7, ["court"] = "flmb", ["division"] = "tampa", ["client_ids"] = ids };
    }

    /// <summary>A freshly opened case and its creator's assignment.</summary>
    public static (Case Case, CaseAssignment Assignment) NewCase(string firmId = Ids.Firm, string createdBy = Ids.Alice, TimeProvider? clock = null) =>
        CaseRules.Create(CaseRules.ParseCreation(Opening()), firmId, createdBy, clock);

    /// <summary>A firm user's resolved accessor.</summary>
    public static Accessor Accessor(string subject = Ids.Alice, bool isAdmin = false, bool accessAllCases = false, string firmId = Ids.Firm) =>
        new(
            new Firm(firmId, "Example", FirmStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new FirmUser(
                firmId,
                subject,
                "someone@example.test",
                "Some",
                "One",
                FirmRole.Attorney,
                isAdmin,
                accessAllCases,
                ValueCollections.EmptyDictionary<Feature, PermissionLevel>(),
                FirmUserStatus.Active,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch));
}

/// <summary>
/// A stand-in for the KMS envelope (18.12): reversible, and — like the real
/// one — refusing to open under any context but the sealing one. Records
/// every plaintext it sealed so a test can prove no store holds one.
/// </summary>
internal sealed class FakeCipher : ITaxIdCipher, IFilingCredentialSealer, IFilingCredentialOpener
{
    public List<string> Sealed { get; } = [];

    public Task<Envelope> SealAsync(string plaintext, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default)
    {
        Sealed.Add(plaintext);
        var bound = Canonical(context);
        var cipher = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string([.. plaintext.Reverse()])));
        return Task.FromResult(new Envelope(TaxIdRules.SealedScheme, cipher, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(bound)), "wrapped"));
    }

    public Task<string> OpenAsync(Envelope envelope, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default)
    {
        if (System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Nonce)) != Canonical(context))
        {
            throw new System.Security.Cryptography.CryptographicException("the tag does not match");
        }

        var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Ciphertext));
        return Task.FromResult(new string([.. text.Reverse()]));
    }

    private static string Canonical(IReadOnlyDictionary<string, string> context) =>
        string.Join("&", context.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
}
