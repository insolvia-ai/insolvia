using Insolvia.Core;
using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;
using Insolvia.Data.Concurrency;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Vault;

/// <summary>
/// The Postgres <see cref="IFilingCredentialStore"/> (Python's
/// <c>DynamoDbFilingCredentialStore</c>). Every statement names the attorney's
/// partition — firm, attorney, credential — so ownership and lookup are one
/// key, as they were.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which statements need which columns</b> is the point of this class: the
/// API role holds <c>INSERT</c>, <c>DELETE</c> and a column-level
/// <c>SELECT</c> on <see cref="FilingCredentialColumns.Metadata"/> only, and
/// the filing worker <c>SELECT</c> on everything (the migrator's
/// <c>GrantTable</c>). So <see cref="CreateAsync"/> (a plain <c>INSERT</c>, no
/// <c>RETURNING</c>), <see cref="ListForAttorneyAsync"/> and
/// <see cref="DeleteAsync"/> touch metadata columns alone and work as either
/// role; <see cref="GetAsync"/> reads the envelope and works as the filing
/// worker alone — the API never calls it, and as the API it is refused by the
/// database.
/// </para>
/// <para>
/// Hence <see cref="ListForAttorneyAsync"/> returns each credential with its
/// envelope WITHHELD (<see cref="Withheld"/>: the scheme, empty ciphertext,
/// nonce and wrapped key). Every caller of the list builds a status view or
/// picks a credential id; the one place a secret is produced
/// (<c>FilingCredentialRules.OpenAsync</c>) reads through
/// <see cref="GetAsync"/>.
/// </para>
/// </remarks>
public sealed class PostgresFilingCredentialStore(InsolviaDbContext db) : IFilingCredentialStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    private IQueryable<FilingCredentialRow> Credentials => _db.Set<FilingCredentialRow>().AsNoTracking();

    /// <summary>The envelope a listed credential carries: its scheme, and no sealed bytes.</summary>
    public static Envelope Withheld(string scheme) => new(scheme, "", "", "");

    /// <inheritdoc />
    /// <remarks>Python: <c>PutItem … attribute_not_exists(SK)</c> → the primary key; a taken id throws, never replaces.</remarks>
    public Task CreateAsync(FilingCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return _db.InsertOnceAsync(
            ToRow(credential),
            FilingCredentialRow.PrimaryKey,
            () => new InvalidOperationException("credential id already exists"),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<FilingCredential?> GetAsync(string firmId, string attorneyId, string credentialId, CancellationToken cancellationToken = default)
    {
        var row = await Credentials
            .SingleOrDefaultAsync(found => found.FirmId == firmId && found.AttorneyId == attorneyId && found.CredentialId == credentialId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    /// <remarks>Metadata columns only — see the class remarks: the envelope is <see cref="Withheld"/>.</remarks>
    public async Task<IReadOnlyList<FilingCredential>> ListForAttorneyAsync(string firmId, string attorneyId, CancellationToken cancellationToken = default)
    {
        var rows = await Credentials
            .Where(found => found.FirmId == firmId && found.AttorneyId == attorneyId)
            .Select(found => new
            {
                found.FirmId,
                found.AttorneyId,
                found.CredentialId,
                found.Login,
                found.Courts,
                found.Status,
                found.Scheme,
                found.CreatedAt,
                found.UpdatedAt,
                found.AuthorizationRef,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows
            .Select(row => new FilingCredential(
                row.CredentialId,
                row.FirmId,
                row.AttorneyId,
                row.Login,
                row.Courts.ToValueCollection(),
                row.Status,
                Withheld(row.Scheme),
                row.CreatedAt,
                row.UpdatedAt,
                row.AuthorizationRef))
            .OrderBy(credential => credential.CreatedAt)
            .ThenBy(credential => credential.CredentialId, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    /// <remarks>Python: <c>DeleteItem … ReturnValues=ALL_OLD</c> → one <c>DELETE</c>; a row deleted is "there was one".</remarks>
    public Task<bool> DeleteAsync(string firmId, string attorneyId, string credentialId, CancellationToken cancellationToken = default) =>
        _db.DeleteOnceAsync(_db.Set<FilingCredentialRow>().Where(found => found.FirmId == firmId && found.AttorneyId == attorneyId && found.CredentialId == credentialId), cancellationToken);

    internal static FilingCredentialRow ToRow(FilingCredential credential) => new()
    {
        FirmId = credential.FirmId,
        AttorneyId = credential.AttorneyId,
        CredentialId = credential.CredentialId,
        Login = credential.Login,
        Courts = [.. credential.Courts],
        Status = credential.Status,
        Scheme = credential.Envelope.Scheme,
        Ciphertext = SealedBytes.Decode(credential.Envelope.Ciphertext, "ciphertext"),
        Nonce = SealedBytes.Decode(credential.Envelope.Nonce, "nonce"),
        WrappedKey = SealedBytes.Decode(credential.Envelope.WrappedKey, "wrapped_key"),
        CreatedAt = credential.CreatedAt,
        UpdatedAt = credential.UpdatedAt,
        AuthorizationRef = credential.AuthorizationRef,
    };

    internal static FilingCredential ToDomain(FilingCredentialRow row) => new(
        row.CredentialId,
        row.FirmId,
        row.AttorneyId,
        row.Login,
        row.Courts.ToValueCollection(),
        row.Status,
        new Envelope(row.Scheme, Convert.ToBase64String(row.Ciphertext), Convert.ToBase64String(row.Nonce), Convert.ToBase64String(row.WrappedKey)),
        row.CreatedAt,
        row.UpdatedAt,
        row.AuthorizationRef);
}

/// <summary>An envelope's base64 fields as the bytes stored, decoded strictly.</summary>
internal static class SealedBytes
{
    /// <summary>
    /// Standard, padded base64 that re-encodes to exactly itself — so what
    /// is read back is byte-for-byte what was sealed. Anything else (white
    /// space, URL-safe alphabet, non-canonical padding bits) is refused
    /// rather than silently normalised.
    /// </summary>
    public static byte[] Decode(string value, string field)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new byte[(value.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64String(value, buffer, out var written))
        {
            throw new ArgumentException($"the envelope's {field} is not base64", nameof(value));
        }

        var bytes = buffer[..written];
        return Convert.ToBase64String(bytes) == value
            ? bytes
            : throw new ArgumentException($"the envelope's {field} is not canonical base64", nameof(value));
    }
}
