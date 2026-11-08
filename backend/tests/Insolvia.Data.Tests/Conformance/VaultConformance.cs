using Insolvia.Core;
using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;
using Insolvia.Data.Vault;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>The vault's two ports over one state, as a request composes them.</summary>
internal sealed record VaultStores(IFilingCredentialStore Credentials, IFilingAuthorizationStore Authorizations);

/// <summary>
/// The vault's shared harness: what both suites need — a signature to enrol
/// under (the Postgres credential cites it by foreign key), and a credential
/// with a synthetic envelope. Nothing here is a real secret.
/// </summary>
public abstract class VaultConformance : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>A handle on the stores; for Postgres, another connection each call (so two handles race).</summary>
    private protected abstract VaultStores Connect();

    /// <summary>The history row for <paramref name="authorizationId"/>, or null — a probe; the port has no history read.</summary>
    private protected abstract Task<FilingAuthorization?> HistoryAsync(string authorizationId);

    private protected static string NewId() => Guid.NewGuid().ToString();

    private protected static FilingAuthorization Signature(string attorneyId = Ids.Alice, string firmId = Ids.Firm, int minutes = 0) =>
        new(NewId(), firmId, attorneyId, FilingAuthorizationRules.TextVersion, new string('a', 64), At.AddMinutes(minutes), 1_793_000_000 + minutes);

    /// <summary>A current signature for the attorney, made through the port.</summary>
    private protected async Task<FilingAuthorization> SignAsync(string attorneyId = Ids.Alice, string firmId = Ids.Firm)
    {
        var signature = Signature(attorneyId, firmId);
        await Connect().Authorizations.PutCurrentAsync(signature, replacing: null, Ct);
        return signature;
    }

    private protected static FilingCredential Credential(FilingAuthorization under, string? credentialId = null, string login = "example-login", int minutes = 0, string[]? courts = null) =>
        new(
            credentialId ?? NewId(),
            under.FirmId,
            under.AttorneyId,
            login,
            (courts ?? ["flmb", "flsb"]).ToValueCollection(),
            CredentialStatus.Active,
            new Envelope(TaxIdRules.SealedScheme, Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8, 9]), Convert.ToBase64String(new byte[12]), Convert.ToBase64String([0xfe, 0xed, 0xfa, 0xce])),
            At.AddMinutes(minutes),
            At.AddMinutes(minutes),
            under.AuthorizationId);

    /// <summary>A credential as the list returns it on every store: the metadata, the envelope withheld.</summary>
    private protected static FilingCredential Listed(FilingCredential credential) =>
        credential with { Envelope = PostgresFilingCredentialStore.Withheld(credential.Envelope.Scheme) };
}

/// <summary>
/// What every <see cref="IFilingCredentialStore"/> must do (Python's
/// <c>FilingCredentialStore</c>): create once (<c>attribute_not_exists(SK)</c>),
/// read and delete within the attorney's partition only, list the attorney's
/// own oldest first. Of two creates of one id, exactly one lands.
/// </summary>
public abstract class FilingCredentialStoreConformance : VaultConformance
{
    [Fact]
    public async Task A_credential_reads_back_whole_from_its_attorneys_partition()
    {
        var credential = Credential(await SignAsync());
        await Connect().Credentials.CreateAsync(credential, Ct);

        Assert.Equal(credential, await Connect().Credentials.GetAsync(Ids.Firm, Ids.Alice, credential.CredentialId, Ct));
        Assert.Null(await Connect().Credentials.GetAsync(Ids.Firm, Ids.Bob, credential.CredentialId, Ct));
        Assert.Null(await Connect().Credentials.GetAsync(Ids.OtherFirm, Ids.Alice, credential.CredentialId, Ct));
        Assert.Null(await Connect().Credentials.GetAsync(Ids.Firm, Ids.Alice, NewId(), Ct));
    }

    [Fact]
    public async Task A_taken_id_is_refused_and_never_replaces_the_sealed_secret()
    {
        var credential = Credential(await SignAsync());
        await Connect().Credentials.CreateAsync(credential, Ct);

        var replacement = credential with { Login = "someone-else", Envelope = credential.Envelope with { Ciphertext = Convert.ToBase64String([9, 9, 9]) } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Credentials.CreateAsync(replacement, Ct));
        Assert.Equal(credential, await Connect().Credentials.GetAsync(Ids.Firm, Ids.Alice, credential.CredentialId, Ct));
    }

    [Fact]
    public async Task Of_two_creates_of_one_id_exactly_one_lands()
    {
        var signature = await SignAsync();
        var credentialId = NewId();
        var first = Credential(signature, credentialId, login: "first");
        var second = Credential(signature, credentialId, login: "second");

        var outcomes = await Task.WhenAll(
            Attempt(() => Connect().Credentials.CreateAsync(first, Ct)),
            Attempt(() => Connect().Credentials.CreateAsync(second, Ct)));

        Assert.Single(outcomes, landed => landed);
        var stored = await Connect().Credentials.GetAsync(Ids.Firm, Ids.Alice, credentialId, Ct);
        Assert.Contains(stored, new[] { first, second });
    }

    [Fact]
    public async Task The_same_id_in_another_attorneys_partition_is_another_credential()
    {
        var credentialId = NewId();
        var alices = Credential(await SignAsync(Ids.Alice), credentialId, login: "alice-login");
        var bobs = Credential(await SignAsync(Ids.Bob), credentialId, login: "bob-login");
        await Connect().Credentials.CreateAsync(alices, Ct);
        await Connect().Credentials.CreateAsync(bobs, Ct);

        Assert.Equal(alices, await Connect().Credentials.GetAsync(Ids.Firm, Ids.Alice, credentialId, Ct));
        Assert.Equal(bobs, await Connect().Credentials.GetAsync(Ids.Firm, Ids.Bob, credentialId, Ct));
    }

    [Fact]
    public async Task The_list_is_the_attorneys_own_oldest_first_then_by_id()
    {
        var alice = await SignAsync(Ids.Alice);
        var later = Credential(alice, login: "later", minutes: 5);
        var tieA = Credential(alice, "00000000-0000-4000-8000-0000000000c1", "tie-a", minutes: 1, courts: []);
        var tieB = Credential(alice, "00000000-0000-4000-8000-0000000000c2", "tie-b", minutes: 1);
        var bobs = Credential(await SignAsync(Ids.Bob), login: "bob-login");
        var otherFirm = Credential(await SignAsync(Ids.Alice, Ids.OtherFirm), login: "other-firm");
        foreach (var credential in new[] { later, tieB, bobs, tieA, otherFirm })
        {
            await Connect().Credentials.CreateAsync(credential, Ct);
        }

        var listed = await Connect().Credentials.ListForAttorneyAsync(Ids.Firm, Ids.Alice, Ct);

        Assert.Equal(new[] { tieA, tieB, later }.Select(Listed), listed.Select(Listed));
        Assert.Empty(await Connect().Credentials.ListForAttorneyAsync(Ids.Firm, "nobody", Ct));
    }

    [Fact]
    public async Task A_delete_destroys_the_item_once_and_only_in_its_own_partition()
    {
        var credential = Credential(await SignAsync());
        await Connect().Credentials.CreateAsync(credential, Ct);

        Assert.False(await Connect().Credentials.DeleteAsync(Ids.Firm, Ids.Bob, credential.CredentialId, Ct));
        Assert.False(await Connect().Credentials.DeleteAsync(Ids.OtherFirm, Ids.Alice, credential.CredentialId, Ct));
        Assert.NotNull(await Connect().Credentials.GetAsync(Ids.Firm, Ids.Alice, credential.CredentialId, Ct));

        Assert.True(await Connect().Credentials.DeleteAsync(Ids.Firm, Ids.Alice, credential.CredentialId, Ct));
        Assert.False(await Connect().Credentials.DeleteAsync(Ids.Firm, Ids.Alice, credential.CredentialId, Ct));
        Assert.Null(await Connect().Credentials.GetAsync(Ids.Firm, Ids.Alice, credential.CredentialId, Ct));
        Assert.Empty(await Connect().Credentials.ListForAttorneyAsync(Ids.Firm, Ids.Alice, Ct));
    }

    private static async Task<bool> Attempt(Func<Task> create)
    {
        try
        {
            await create();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// What every <see cref="IFilingAuthorizationStore"/> must do (Python's
/// <c>FilingAuthorizationStore</c>): the pointer moves only from the value the
/// caller read (<c>attribute_not_exists(SK)</c> / <c>authorizationId = :held</c>),
/// a lost race leaves no history behind, a replaced signature is superseded,
/// and a withdrawal removes the pointer only if it is still the one named.
/// </summary>
public abstract class FilingAuthorizationStoreConformance : VaultConformance
{
    [Fact]
    public async Task With_no_signature_there_is_no_current_authorization()
    {
        Assert.Null(await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
    }

    [Fact]
    public async Task The_first_signature_becomes_current_and_reads_back_whole()
    {
        var signature = await SignAsync();

        Assert.Equal(signature, await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Equal(signature, await HistoryAsync(signature.AuthorizationId));
        Assert.Null(await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Bob, Ct));
        Assert.Null(await Connect().Authorizations.GetCurrentAsync(Ids.OtherFirm, Ids.Alice, Ct));
    }

    [Fact]
    public async Task A_first_signature_while_one_is_current_is_a_conflict_and_leaves_no_trace()
    {
        var held = await SignAsync();
        var late = Signature(minutes: 1);

        await Assert.ThrowsAsync<ConflictException>(() => Connect().Authorizations.PutCurrentAsync(late, replacing: null, Ct));

        Assert.Equal(held, await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Null(await HistoryAsync(late.AuthorizationId));
    }

    [Fact]
    public async Task Replacing_the_held_signature_supersedes_it()
    {
        var held = await SignAsync();
        var next = Signature(minutes: 1);

        await Connect().Authorizations.PutCurrentAsync(next, held, Ct);

        Assert.Equal(next, await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Equal(next, await HistoryAsync(next.AuthorizationId));
        Assert.Equal(held with { Status = AuthorizationStatus.Superseded }, await HistoryAsync(held.AuthorizationId));
    }

    [Fact]
    public async Task Replacing_a_signature_that_is_no_longer_current_is_a_conflict()
    {
        var first = await SignAsync();
        var second = Signature(minutes: 1);
        await Connect().Authorizations.PutCurrentAsync(second, first, Ct);
        var stale = Signature(minutes: 2);

        await Assert.ThrowsAsync<ConflictException>(() => Connect().Authorizations.PutCurrentAsync(stale, first, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => Connect().Authorizations.PutCurrentAsync(Signature(Ids.Bob), first with { AttorneyId = Ids.Bob }, Ct));

        Assert.Equal(second, await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Null(await HistoryAsync(stale.AuthorizationId));
    }

    [Fact]
    public async Task Of_two_first_signatures_exactly_one_becomes_current()
    {
        var one = Signature();
        var two = Signature(minutes: 1);

        var outcomes = await Task.WhenAll(
            Attempt(() => Connect().Authorizations.PutCurrentAsync(one, replacing: null, Ct)),
            Attempt(() => Connect().Authorizations.PutCurrentAsync(two, replacing: null, Ct)));

        Assert.Single(outcomes, landed => landed);
        var (winner, loser) = outcomes[0] ? (one, two) : (two, one);
        Assert.Equal(winner, await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Null(await HistoryAsync(loser.AuthorizationId));
    }

    [Fact]
    public async Task Of_two_replacements_from_one_read_exactly_one_lands_and_the_loser_leaves_no_history()
    {
        var held = await SignAsync();
        var one = Signature(minutes: 1);
        var two = Signature(minutes: 2);

        var outcomes = await Task.WhenAll(
            Attempt(() => Connect().Authorizations.PutCurrentAsync(one, held, Ct)),
            Attempt(() => Connect().Authorizations.PutCurrentAsync(two, held, Ct)));

        Assert.Single(outcomes, landed => landed);
        var (winner, loser) = outcomes[0] ? (one, two) : (two, one);
        Assert.Equal(winner, await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Null(await HistoryAsync(loser.AuthorizationId));
        Assert.Equal(AuthorizationStatus.Superseded, (await HistoryAsync(held.AuthorizationId))!.Status);
    }

    [Fact]
    public async Task A_withdrawal_removes_the_current_signature_once_and_records_when()
    {
        var held = await SignAsync();
        var at = new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

        Assert.True(await Connect().Authorizations.WithdrawAsync(held, at, Ct));
        Assert.False(await Connect().Authorizations.WithdrawAsync(held, at.AddMinutes(1), Ct));

        Assert.Null(await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Equal(held with { Status = AuthorizationStatus.Withdrawn, WithdrawnAt = at }, await HistoryAsync(held.AuthorizationId));

        // After a withdrawal the attorney signs afresh, replacing nothing.
        var again = Signature(minutes: 3);
        await Connect().Authorizations.PutCurrentAsync(again, replacing: null, Ct);
        Assert.Equal(again, await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
    }

    [Fact]
    public async Task Withdrawing_a_signature_that_is_not_the_current_one_changes_nothing()
    {
        var first = await SignAsync();
        var second = Signature(minutes: 1);
        await Connect().Authorizations.PutCurrentAsync(second, first, Ct);
        var bobs = await SignAsync(Ids.Bob);

        Assert.False(await Connect().Authorizations.WithdrawAsync(first, DateTimeOffset.UnixEpoch, Ct));
        Assert.False(await Connect().Authorizations.WithdrawAsync(bobs with { AttorneyId = Ids.Alice }, DateTimeOffset.UnixEpoch, Ct));

        Assert.Equal(second, await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
        Assert.Equal(bobs, await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Bob, Ct));
        Assert.Equal(AuthorizationStatus.Superseded, (await HistoryAsync(first.AuthorizationId))!.Status);
    }

    [Fact]
    public async Task Of_a_withdrawal_and_a_replacement_from_one_read_exactly_one_lands()
    {
        var held = await SignAsync();
        var next = Signature(minutes: 1);

        var outcomes = await Task.WhenAll(
            Connect().Authorizations.WithdrawAsync(held, DateTimeOffset.UnixEpoch, Ct),
            Attempt(() => Connect().Authorizations.PutCurrentAsync(next, held, Ct)));

        Assert.Single(outcomes, landed => landed);
        var current = await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct);
        Assert.Equal(outcomes[0] ? null : next, current);
    }

    private static async Task<bool> Attempt(Func<Task> put)
    {
        try
        {
            await put();
            return true;
        }
        catch (ConflictException)
        {
            return false;
        }
    }
}

/// <summary>The oracle for both suites: the memory twins.</summary>
internal sealed class MemoryVault
{
    public MemoryFilingCredentialStore Credentials { get; } = new();

    public MemoryFilingAuthorizationStore Authorizations { get; } = new();

    public VaultStores Connect() => new(Credentials, Authorizations);

    public Task<FilingAuthorization?> HistoryAsync(string authorizationId) =>
        Task.FromResult(Authorizations.History.GetValueOrDefault(authorizationId));
}

/// <summary>The Postgres stores, one fresh context per handle, over a database with the suites' two firms.</summary>
internal sealed class PostgresVault(PostgresFixture postgres) : IAsyncDisposable
{
    public PostgresHarness Harness { get; } = new(postgres);

    public async Task InitializeAsync()
    {
        await Harness.InitializeAsync();
        await Harness.SeedFirmsAsync();
    }

    public VaultStores Connect()
    {
        var db = Harness.NewContext();
        return new(new PostgresFilingCredentialStore(db), new PostgresFilingAuthorizationStore(db));
    }

    public async Task<FilingAuthorization?> HistoryAsync(string authorizationId)
    {
        var row = await Harness.NewContext().Set<FilingAuthorizationRow>().AsNoTracking()
            .SingleOrDefaultAsync(found => found.AuthorizationId == authorizationId, TestContext.Current.CancellationToken);
        return row is null ? null : PostgresFilingAuthorizationStore.ToDomain(row);
    }

    public ValueTask DisposeAsync() => Harness.DisposeAsync();
}

/// <summary><see cref="MemoryFilingCredentialStore"/>, the oracle.</summary>
public sealed class MemoryFilingCredentialStoreConformance : FilingCredentialStoreConformance
{
    private readonly MemoryVault _vault = new();

    private protected override VaultStores Connect() => _vault.Connect();

    private protected override Task<FilingAuthorization?> HistoryAsync(string authorizationId) => _vault.HistoryAsync(authorizationId);
}

/// <summary><see cref="PostgresFilingCredentialStore"/> on a real Postgres 17 — plus what only the table enforces.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresFilingCredentialStoreConformance(PostgresFixture postgres) : FilingCredentialStoreConformance
{
    private readonly PostgresVault _vault = new(postgres);

    public override async ValueTask InitializeAsync()
    {
        await _vault.InitializeAsync();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _vault.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task The_list_reads_no_envelope_column_and_says_so()
    {
        // The API role's column grant names no envelope column (GrantTable);
        // the list is the API's read, so it must not need one.
        var credential = Credential(await SignAsync());
        await Connect().Credentials.CreateAsync(credential, Ct);

        var listed = Assert.Single(await Connect().Credentials.ListForAttorneyAsync(Ids.Firm, Ids.Alice, Ct));

        Assert.Equal(Listed(credential), listed);
        Assert.Equal(new Envelope(TaxIdRules.SealedScheme, "", "", ""), listed.Envelope);
    }

    [Fact]
    public async Task Every_column_is_either_metadata_or_envelope()
    {
        // The API's column grant is built from FilingCredentialColumns: a new
        // column nobody classified would be invisible to the API, or — worse —
        // classified by accident.
        var columns = await _vault.Harness.NewContext().Database
            .SqlQuery<string>($"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'insolvia' AND table_name = 'filing_credentials'")
            .ToListAsync(Ct);

        Assert.Equal(
            FilingCredentialColumns.Metadata.Concat(FilingCredentialColumns.Envelope).Order(StringComparer.Ordinal),
            columns.Order(StringComparer.Ordinal));
        Assert.Empty(FilingCredentialColumns.Metadata.Intersect(FilingCredentialColumns.Envelope));
    }

    [Fact]
    public async Task The_envelope_is_stored_as_bytes_beside_the_context_it_was_sealed_under()
    {
        var credential = Credential(await SignAsync());
        await Connect().Credentials.CreateAsync(credential, Ct);

        var row = await _vault.Harness.NewContext().Set<FilingCredentialRow>().AsNoTracking().SingleAsync(Ct);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, row.Ciphertext);
        Assert.Equal(new byte[12], row.Nonce);
        Assert.Equal(new byte[] { 0xfe, 0xed, 0xfa, 0xce }, row.WrappedKey);
        Assert.Equal(
            FilingCredentialRules.EncryptionContext(row.FirmId, row.AttorneyId, row.CredentialId),
            FilingCredentialRules.EncryptionContext(credential.FirmId, credential.AttorneyId, credential.CredentialId));
    }

    [Theory]
    [InlineData("AQID BA==")]
    [InlineData("AQIDBA")]
    [InlineData("AQIDBB==")]
    [InlineData("-_-_")]
    [InlineData("not base64!")]
    public async Task An_envelope_field_that_is_not_canonical_base64_is_refused(string ciphertext)
    {
        var credential = Credential(await SignAsync());
        var bent = credential with { Envelope = credential.Envelope with { Ciphertext = ciphertext } };

        await Assert.ThrowsAsync<ArgumentException>(() => Connect().Credentials.CreateAsync(bent, Ct));
        Assert.Null(await Connect().Credentials.GetAsync(Ids.Firm, Ids.Alice, credential.CredentialId, Ct));
    }

    [Fact]
    public async Task A_credential_can_only_cite_a_signature_its_own_attorney_made()
    {
        var bobs = await SignAsync(Ids.Bob);
        var alices = Credential(bobs) with { AttorneyId = Ids.Alice };

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Connect().Credentials.CreateAsync(alices, Ct));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);

        var unknownFirm = Credential(await SignAsync()) with { FirmId = "00000000-0000-4000-8000-00000000f18f", AuthorizationRef = null };
        await Assert.ThrowsAsync<DbUpdateException>(() => Connect().Credentials.CreateAsync(unknownFirm, Ct));
    }

    private protected override VaultStores Connect() => _vault.Connect();

    private protected override Task<FilingAuthorization?> HistoryAsync(string authorizationId) => _vault.HistoryAsync(authorizationId);
}

/// <summary><see cref="MemoryFilingAuthorizationStore"/>, the oracle.</summary>
public sealed class MemoryFilingAuthorizationStoreConformance : FilingAuthorizationStoreConformance
{
    private readonly MemoryVault _vault = new();

    private protected override VaultStores Connect() => _vault.Connect();

    private protected override Task<FilingAuthorization?> HistoryAsync(string authorizationId) => _vault.HistoryAsync(authorizationId);
}

/// <summary><see cref="PostgresFilingAuthorizationStore"/> on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresFilingAuthorizationStoreConformance(PostgresFixture postgres) : FilingAuthorizationStoreConformance
{
    private readonly PostgresVault _vault = new(postgres);

    public override async ValueTask InitializeAsync()
    {
        await _vault.InitializeAsync();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _vault.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_withdrawn_status_and_its_time_go_together()
    {
        var signature = Signature() with { Status = AuthorizationStatus.Withdrawn };

        await Assert.ThrowsAsync<DbUpdateException>(() => Connect().Authorizations.PutCurrentAsync(signature, replacing: null, Ct));
        Assert.Null(await Connect().Authorizations.GetCurrentAsync(Ids.Firm, Ids.Alice, Ct));
    }

    private protected override VaultStores Connect() => _vault.Connect();

    private protected override Task<FilingAuthorization?> HistoryAsync(string authorizationId) => _vault.HistoryAsync(authorizationId);
}
