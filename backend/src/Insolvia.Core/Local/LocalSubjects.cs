using System.Security.Cryptography;
using System.Text;

namespace Insolvia.Core.Local;

/// <summary>
/// The subject an address resolves to on a developer's machine, where
/// <c>backend/local-auth</c> stands in for the pool (ADR 0030). A pure function
/// of the address, so the seeder, the user directory and the sign-in server
/// agree with no shared state. <c>backend/local-auth/server.ts</c> computes the
/// same value, and both test suites pin the same vector.
/// </summary>
public static class LocalSubjects
{
    // SeedIds.Namespace in Insolvia.Migrator, which Core cannot reference.
    private static readonly Guid Namespace = new("6f1c2b8e-3d4a-4f5b-9c6d-7e8f9a0b1c2d");

    /// <summary>The subject for <paramref name="email"/>, case-insensitively, as the pool treats addresses.</summary>
    /// <param name="email">The address.</param>
    /// <returns>A lower-case hyphenated uuid5.</returns>
    public static string For(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var name = Encoding.UTF8.GetBytes("local-account/" + email.Trim().ToLowerInvariant());
        var bytes = Namespace.ToByteArray(bigEndian: true).Concat(name).ToArray();
#pragma warning disable CA5350 // uuid5 is DEFINED as SHA-1; this is an identifier, not a security boundary.
        var id = SHA1.HashData(bytes)[..16];
#pragma warning restore CA5350
        id[6] = (byte)((id[6] & 0x0F) | 0x50);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id, bigEndian: true).ToString();
    }
}
