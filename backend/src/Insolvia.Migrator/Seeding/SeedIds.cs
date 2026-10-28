using System.Security.Cryptography;
using System.Text;

namespace Insolvia.Migrator.Seeding;

/// <summary>
/// A guard or the fixture rejected the run (Python's <c>RefusedError</c>).
/// Whatever was refused, nothing was written for it: every refusal is raised
/// before the write it guards.
/// </summary>
public sealed class SeedRefusedException : Exception
{
    /// <summary>Creates the refusal.</summary>
    public SeedRefusedException()
    {
    }

    /// <summary>Creates the refusal with its reason.</summary>
    public SeedRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the refusal with its reason and cause.</summary>
    public SeedRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// IDS ARE DERIVED, NOT PUBLISHED (seed.py's <c>derived_id</c>): a uuid5 over
/// one namespace and the parts joined by <c>/</c> — byte for byte Python's
/// <c>uuid.uuid5(_SEED_NAMESPACE, "/".join(parts))</c>, pinned by vectors the
/// Python computed. So a fixture row's id is a pure function of (target, firm,
/// version, handle): two environments seed different ids from one fixture,
/// and a second run finds every row it wrote instead of writing it twice.
/// </summary>
public static class SeedIds
{
    /// <summary>The namespace every derived id hangs off (seed.py's <c>_SEED_NAMESPACE</c>).</summary>
    public static readonly Guid Namespace = new("6f1c2b8e-3d4a-4f5b-9c6d-7e8f9a0b1c2d");

    /// <summary>The id <paramref name="parts"/> derive, as a lower-case hyphenated uuid.</summary>
    /// <param name="parts">The parts, joined with <c>/</c>.</param>
    /// <returns>The uuid5.</returns>
    public static string Derive(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return Uuid5(Namespace, string.Join('/', parts));
    }

    /// <summary>RFC 4122 §4.3 with SHA-1: the namespace's bytes in network order, then the name's UTF-8.</summary>
    /// <param name="space">The namespace.</param>
    /// <param name="name">The name.</param>
    /// <returns>The uuid5.</returns>
    internal static string Uuid5(Guid space, string name)
    {
        var bytes = space.ToByteArray(bigEndian: true).Concat(Encoding.UTF8.GetBytes(name)).ToArray();
#pragma warning disable CA5350 // uuid5 is DEFINED as SHA-1; this is an identifier, not a security boundary.
        var hash = SHA1.HashData(bytes);
#pragma warning restore CA5350
        var id = hash[..16];
        id[6] = (byte)((id[6] & 0x0F) | 0x50);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id, bigEndian: true).ToString();
    }
}

/// <summary>
/// Where a load lands, and the fence around it. Python fenced on the target
/// TABLE's name (<c>insolvia-dev-&lt;machine&gt;-*</c> or <c>insolvia-staging-*</c>);
/// with one database per environment the fence is the environment itself:
/// <c>local</c> (a developer's compose database) and <c>staging</c> load, and
/// <c>production</c> — or anything else — is refused outright. Provisioning a
/// real firm wants an audit trail and a review, which is the admin service's
/// HTTP surface, never a CLI.
/// </summary>
/// <remarks>
/// The first part of every derived id was the DynamoDB table's name. It still
/// is: <see cref="FirmTarget"/> and <see cref="CaseTarget"/> are the names the
/// environment's tables had (<c>insolvia-staging-cases</c>), so the same inputs
/// derive the same ids in both backends — a staging case id is the one the
/// Python loader would have given it for the same firm.
/// </remarks>
public sealed record SeedTarget
{
    private SeedTarget(string environment) => Environment = environment;

    /// <summary>The environment loaded into.</summary>
    public string Environment { get; }

    /// <summary>The firm side's derivation root (the firm table's old name).</summary>
    public string FirmTarget => $"insolvia-{Environment}-firms";

    /// <summary>The case side's derivation root (the case table's old name).</summary>
    public string CaseTarget => $"insolvia-{Environment}-cases";

    /// <summary>The target for <paramref name="environment"/> (<c>INSOLVIA_ENV</c>), or a refusal.</summary>
    /// <param name="environment">The environment's name; null or empty means <c>local</c>, as everywhere.</param>
    /// <returns>The target.</returns>
    public static SeedTarget For(string? environment)
    {
        var name = string.IsNullOrEmpty(environment) ? "local" : environment;
        return name switch
        {
            "local" or "staging" => new SeedTarget(name),
            "production" => throw new SeedRefusedException(
                "refusing to seed production: a real firm is provisioned through the admin service, with its audit trail, never from a fixture"),
            _ => throw new SeedRefusedException($"'{name}' is not a seedable environment (expected local or staging)"),
        };
    }
}
