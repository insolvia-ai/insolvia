using System.Security.Cryptography;
using Insolvia.Core.Ports;

namespace Insolvia.Migrator.Seeding;

/// <summary>
/// The pool half of seeding: make sure a person can sign in, and say who they
/// are (seed.py's <c>Accounts</c>).
/// </summary>
/// <remarks>
/// NOT <see cref="IUserDirectory"/>. That port is AdminCreateUser and nothing
/// else — no password, no lookup — because it is the API's grant, and a
/// service that could set a password would be an impersonation primitive. The
/// seeder is a different principal with different needs; widening that port
/// for it would quietly hand the API a capability it was designed not to have.
/// <see cref="SeedRefusedException"/> is part of the contract, so a fake that
/// throws anything else is a broken fake rather than a passing test.
/// </remarks>
public interface ISeedAccounts
{
    /// <summary>The pool subject for <paramref name="email"/>; <see cref="SeedRefusedException"/> when there is no such account.</summary>
    Task<string> SubjectOfAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create the account if absent, set its password (every run, so a rotated
    /// secret converges and FORCE_CHANGE_PASSWORD never strands a browser
    /// test), and return its subject.
    /// </summary>
    Task<string> EnsureAsync(string email, string password, CancellationToken cancellationToken = default);
}

/// <summary>
/// The bytes half of a fixture (seed.py's <c>FixtureObjects</c>): the shared
/// fixture bucket's objects, copied into a target's own case-documents store
/// without passing through the loader.
/// </summary>
public interface IFixtureObjects
{
    /// <summary>
    /// Copy <paramref name="sourceKey"/> (<c>&lt;version&gt;/&lt;key&gt;</c> in the
    /// fixture bucket) to <paramref name="targetKey"/> in the target's
    /// document store, carrying <paramref name="contentType"/> as its own.
    /// </summary>
    Task CopyAsync(string sourceKey, string targetKey, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Write one object INTO the fixture bucket (publish).</summary>
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>The sha256 (lower-case hex) of an object in the fixture bucket, or null when absent.</summary>
    Task<string?> DigestAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Everything a load writes through: the stores the API composes, plus the fixture's bytes.</summary>
/// <param name="Firms">Firms, their people and their client directories.</param>
/// <param name="Cases">Cases (with their assignments and opening debtors).</param>
/// <param name="Debtors">Debtors, keyed by filing role.</param>
/// <param name="Entities">Collection items.</param>
/// <param name="Documents">Document rows.</param>
/// <param name="Blobs">The target's document bytes (stat after copy, as the complete route does).</param>
/// <param name="Objects">The fixture bucket.</param>
/// <param name="TaxIds">Sealed tax ids.</param>
/// <param name="TaxIdCipher">The case key (KMS deployed, the local key on a laptop).</param>
/// <param name="Bindings">Portal client bindings (ADR 0023).</param>
public sealed record SeedStores(
    IFirmStore Firms,
    ICaseStore Cases,
    IDebtorStore Debtors,
    ICaseEntityStore Entities,
    IDocumentStore Documents,
    IDocumentBlobStore Blobs,
    IFixtureObjects Objects,
    ITaxIdStore TaxIds,
    ITaxIdCipher TaxIdCipher,
    IClientBindingStore Bindings);

/// <summary>
/// <see cref="ISeedAccounts"/> with no identity provider at all — a developer's
/// compose database with no pool named (<c>AUTH_USER_POOL_ID</c> unset).
/// Every address resolves to a subject DERIVED from it, so a second load finds
/// the same people; nobody can sign in as one, which is the truth about a
/// database no pool knows. Point the seeder at the shared dev pool to seed
/// people who can.
/// </summary>
public sealed class LocalSeedAccounts : ISeedAccounts
{
    /// <summary>The subject an address resolves to.</summary>
    public static string SubjectFor(string email) => SeedIds.Derive("local-account", email);

    /// <inheritdoc />
    public Task<string> SubjectOfAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(SubjectFor(email));

    /// <inheritdoc />
    public Task<string> EnsureAsync(string email, string password, CancellationToken cancellationToken = default) => Task.FromResult(SubjectFor(email));
}

/// <summary>
/// The fixture bucket and the target's document store as two local
/// directories: the committed <c>seeds/fixtures</c> tree is the bucket (what
/// <c>publish</c> would upload is exactly what is there), and the target is a
/// directory a laptop keeps across runs. A copy reads the committed bytes and
/// writes them into the target through <see cref="IDocumentBlobStore.PutBytesAsync"/>.
/// </summary>
/// <param name="fixturesRoot">The <c>seeds/fixtures</c> directory.</param>
/// <param name="target">Where the bytes land.</param>
public sealed class DirectoryFixtureObjects(string fixturesRoot, IDocumentBlobStore target) : IFixtureObjects
{
    /// <inheritdoc />
    public async Task CopyAsync(string sourceKey, string targetKey, string contentType, CancellationToken cancellationToken = default)
    {
        var source = Resolve(sourceKey);
        var content = File.Exists(source)
            ? await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false)
            : throw new SeedRefusedException($"no fixture object {sourceKey} under {fixturesRoot}");
        await target.PutBytesAsync(targetKey, content, contentType, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default) =>
        throw new SeedRefusedException("the local fixture tree is the committed one; publish to the shared fixture bucket instead");

    /// <inheritdoc />
    public async Task<string?> DigestAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        return File.Exists(path)
            ? Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)))
            : null;
    }

    private string Resolve(string key)
    {
        var root = Path.GetFullPath(fixturesRoot);
        var path = Path.GetFullPath(Path.Combine(root, key));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new SeedRefusedException($"fixture object {key} is outside {fixturesRoot}");
    }
}
