using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.Cases;
using Insolvia.Core.Debtors;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Core.Provenance;
using Insolvia.Data.Cases;
using Insolvia.Data.Firms;
using Insolvia.Data.Pagination;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// Every case-side port, over ONE state: what a request scope composes. For
/// the memory twins, the same instances wired as the API wires them (the
/// debtor store reads its client conditions from the firm store, the case
/// store writes debtors into it, the filing store files into the case
/// store); for Postgres, stores over one fresh context per handle.
/// </summary>
internal sealed record CaseStores(
    IFirmStore Firms,
    ICaseStore Cases,
    IDebtorStore Debtors,
    ICaseEntityStore Entities,
    IDocumentStore Documents,
    ICandidateStore Candidates,
    ICaseTaskStore Tasks,
    IDocumentRequestStore Requests,
    IFilingStore Filings,
    ITaxIdStore TaxIds,
    IJobStore Jobs,
    IPacketStore Packets)
{
    /// <summary>The filing approvals (#530), over the same state.</summary>
    public required IFilingApprovalStore Approvals { get; init; }
}

/// <summary>The memory twins — the oracle.</summary>
internal sealed class MemoryCaseWorld
{
    private readonly CaseStores _stores;

    public MemoryCaseWorld()
    {
        var firms = new MemoryFirmStore();
        var debtors = new MemoryDebtorStore(firms);
        var cases = new MemoryCaseStore(debtors);
        _stores = new CaseStores(
            firms,
            cases,
            debtors,
            new MemoryCaseEntityStore(),
            new MemoryDocumentStore(),
            new MemoryCandidateStore(),
            new MemoryCaseTaskStore(),
            new MemoryDocumentRequestStore(),
            new MemoryFilingStore(cases),
            new MemoryTaxIdStore(),
            new MemoryJobStore(),
            new MemoryPacketStore(cases))
        {
            Approvals = new MemoryFilingApprovalStore(),
        };
    }

    public CaseStores Connect() => _stores;
}

/// <summary>The Postgres stores: each handle is another connection to the same migrated database, so two handles race the way two requests do.</summary>
internal sealed class PostgresCaseWorld(PostgresFixture postgres) : IAsyncDisposable
{
    private static readonly CursorCodec Cursors = CursorCodec.FromEnvironment(_ => null);
    private readonly PostgresHarness _harness = new(postgres);

    public PostgresHarness Harness => _harness;

    public Task InitializeAsync() => _harness.InitializeAsync();

    public CaseStores Connect() => Over(_harness.NewContext());

    /// <summary>The stores over one context whose operations pass through <paramref name="interceptors"/>.</summary>
    public CaseStores Connect(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) => Over(_harness.NewContext(interceptors));

    private static CaseStores Over(InsolviaDbContext db)
    {
        return new CaseStores(
            new PostgresFirmStore(db),
            new PostgresCaseStore(db, Cursors),
            new PostgresDebtorStore(db),
            new PostgresCaseEntityStore(db),
            new PostgresDocumentStore(db),
            new PostgresCandidateStore(db),
            new PostgresCaseTaskStore(db),
            new PostgresDocumentRequestStore(db),
            new PostgresFilingStore(db),
            new PostgresTaxIdStore(db),
            new PostgresJobStore(db),
            new PostgresPacketStore(db))
        {
            Approvals = new PostgresFilingApprovalStore(db),
        };
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

/// <summary>
/// The base of every case-side conformance suite: the world's two firms, and
/// the builders the suites share. A suite states behaviour through the PORTS
/// only — never a backdoor — so the same test is meaningful for both stores.
/// </summary>
public abstract class CaseConformance : IAsyncLifetime
{
    private protected static readonly DateTimeOffset Opened = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync()
    {
        var firms = Connect().Firms;
        await firms.CreateFirmAsync(Make.Firm(Ids.Firm), Ct);
        await firms.CreateFirmAsync(Make.Firm(Ids.OtherFirm, "Other Firm LLP"), Ct);
    }

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Another handle on the SAME stores.</summary>
    private protected abstract CaseStores Connect();

    private protected static string NewId() => Guid.NewGuid().ToString();

    /// <summary>A firm member: an admin sees every case of the firm; anyone else, only the ones they are linked to.</summary>
    private protected static Accessor Staff(string subject = Ids.Alice, string firmId = Ids.Firm, bool admin = false) =>
        new(Make.Firm(firmId), Make.User(subject, firmId) with { IsAdmin = admin });

    private protected static Accessor Admin(string firmId = Ids.Firm) => Staff(Ids.Bob, firmId, admin: true);

    /// <summary>A case created through the port, its creator (Alice) linked.</summary>
    private protected async Task<Case> CreateCaseAsync(
        string firmId = Ids.Firm,
        DateTimeOffset? createdAt = null,
        int chapter = 7,
        IReadOnlyList<Debtor>? debtors = null,
        string? caseId = null)
    {
        var created = Make.Case(caseId ?? NewId(), firmId, createdAt ?? Opened, chapter);
        await Connect().Cases.CreateAsync(created, Make.Assignment(created), debtors, Ct);
        return created;
    }

    /// <summary>A client in <paramref name="firmId"/>'s directory.</summary>
    private protected async Task<FirmClient> ClientAsync(string firmId = Ids.Firm, string surname = "Example")
    {
        var client = Make.Client(firmId, surname);
        await Connect().Firms.CreateClientAsync(client, Ct);
        return client;
    }

    /// <summary>The client archived — no longer linkable to a new case.</summary>
    private protected async Task ArchiveAsync(FirmClient client) =>
        await Connect().Firms.UpdateClientAsync(FirmClientRules.SetStatus(client, FirmClientStatus.Archived), Ct);

    /// <summary>A debtor with something filled in and its provenance, linked to <paramref name="clientId"/> when given.</summary>
    private protected static Debtor MakeDebtor(Case owner, FilingRole role = FilingRole.Debtor1, string? clientId = null, string email = "pat@example.test") =>
        new(NewId(), owner.Id, role, Opened, Opened, DebtorBody.Empty with { Email = email, Phone = "555-0100" })
        {
            Provenance = new Dictionary<string, ProvenanceEntry>
            {
                ["email"] = new(ProvenanceSource.StaffTyped),
                ["phone"] = new(ProvenanceSource.StaffTyped),
            }.ToValueDictionary(),
            ClientId = clientId,
            CaseCreatedAt = clientId is null ? null : owner.CreatedAt,
        };

    /// <summary>Runs <paramref name="attempts"/> together and counts which succeeded, a refusal being <typeparamref name="TRefusal"/>.</summary>
    private protected static async Task<bool[]> RaceAsync<TRefusal>(params Func<Task>[] attempts)
        where TRefusal : Exception
    {
        return await Task.WhenAll(attempts.Select(async attempt =>
        {
            try
            {
                await Task.Yield();
                await attempt();
                return true;
            }
            catch (TRefusal)
            {
                return false;
            }
        }));
    }
}
