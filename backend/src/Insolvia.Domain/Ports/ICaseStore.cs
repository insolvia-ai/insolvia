using Insolvia.Core.Access;
using Insolvia.Core.Cases;
using Insolvia.Core.Clients;
using Insolvia.Core.Debtors;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists case records (issue 8.3; Python's <c>CaseStore</c>). Every read
/// takes an <see cref="Accessor"/> and every implementation MUST apply
/// <see cref="CaseAccess.MaySeeCase(Accessor, Case, bool)"/> itself — the only
/// thing between one firm's cases and another's. <see cref="GetAsync"/> answers
/// null for "no such case", "another firm's case" AND "your firm's case you
/// are not linked to": the three must be indistinguishable from outside.
/// </summary>
public interface ICaseStore
{
    /// <summary>
    /// Store a new case, link its creator, and write the debtors copied from
    /// its clients — ALL OR NONE, one transaction. Each debtor's client is a
    /// CONDITION of the same transaction: MUST throw
    /// <see cref="ClientUnavailableException"/>, naming the client, when its
    /// row in the case's firm is no longer linkable — nothing written.
    /// </summary>
    Task CreateAsync(Case created, CaseAssignment assignment, IReadOnlyList<Debtor>? debtors = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The cases naming <paramref name="clientId"/> on a debtor that this
    /// accessor may see, newest first — each filtered exactly as
    /// <see cref="GetAsync"/> filters one. The filtered-out leave NO trace.
    /// Unpaginated on purpose.
    /// </summary>
    Task<IReadOnlyList<ClientCase>> ListForClientAsync(string clientId, Accessor accessor, CancellationToken cancellationToken = default);

    /// <summary>One case, if this accessor may see it — the case and the caller's assignment read together.</summary>
    Task<Case?> GetAsync(string caseId, Accessor accessor, CancellationToken cancellationToken = default);

    /// <summary>
    /// The cases this accessor may see, newest first: the by-firm index when
    /// <see cref="Accessor.SeesEveryCase"/>, by-assignee otherwise, and the
    /// working list or the archive (<paramref name="archived"/>). A deleted case
    /// is in neither. The cursor carries its index and view; a cursor from
    /// another is refused (<see cref="ValidationException"/>). A page is never
    /// short merely because the rows it skipped were archived.
    /// </summary>
    Task<CasePage> ListForAccessorAsync(Accessor accessor, int limit, string? cursor, bool archived = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write <paramref name="updated"/> back — an edit, not an event — only if
    /// it still belongs to its firm (null otherwise). <paramref name="expectedStatus"/>
    /// is the status the caller READ: when the stored one differs, nothing is
    /// written and this throws <see cref="ConflictException"/>
    /// (<see cref="CaseRules.StatusMoved"/>). A plain update NEVER moves the
    /// status: one whose status differs from the stored one is refused the
    /// same way (a status move is <see cref="RecordAsync"/>, with its event).
    /// No assignment check: the route resolved the case through <see cref="GetAsync"/>.
    /// </summary>
    Task<Case?> UpdateAsync(Case updated, CaseStatus? expectedStatus = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write a lifecycle event (ADR 0027) and the case it moved, IN ONE
    /// TRANSACTION — the only way an event reaches the history. Conditioned on
    /// the version the writer checked (<see cref="LifecycleWrite.ReadAt"/>):
    /// null when the case is gone or in another firm; <see cref="ConflictException"/>
    /// with <see cref="CaseRules.StatusMoved"/> when its status or chapter
    /// moved, or <see cref="CaseLifecycle.HistoryMoved"/> when another event
    /// landed since (or one shares this event's instant). Nothing is written then.
    /// </summary>
    Task<Case?> RecordAsync(LifecycleWrite write, CancellationToken cancellationToken = default);

    /// <summary>Every lifecycle event of one case, oldest first. Case-scoped: the caller resolved the case first.</summary>
    Task<IReadOnlyList<LifecycleEvent>> LifecycleAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>Link someone to a case. Idempotent — the admin UI cannot tell whether its first request landed.</summary>
    Task AssignAsync(CaseAssignment assignment, CancellationToken cancellationToken = default);

    /// <summary>Unlink someone. True if this call removed the link.</summary>
    Task<bool> UnassignAsync(string caseId, string subject, CancellationToken cancellationToken = default);

    /// <summary>Everyone linked to this case, oldest link first. Case-scoped.</summary>
    Task<IReadOnlyList<CaseAssignment>> AssigneesAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The case with NO accessor and NO access rule — the pipeline worker's
    /// read (ADR 0018), and ONLY the worker's. MUST NOT be called from a route.
    /// </summary>
    Task<Case?> ReadForWorkerAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The bound case's chapter and stage — the CLIENT's only read of a case.
    /// Takes the <see cref="ClientAccessor"/>, never a case id; null unless the
    /// row belongs to the binding's firm (<see cref="CasePublicStatuses.From"/>).
    /// </summary>
    Task<CasePublicStatus?> PublicStatusAsync(ClientAccessor client, CancellationToken cancellationToken = default);
}
