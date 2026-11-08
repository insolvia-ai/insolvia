using Insolvia.Core.Clients;

namespace Insolvia.Core.Ports;

/// <summary>
/// Which case a client-portal subject may reach (ADR 0023). Three kinds of
/// record — the authoritative binding (firm-scoped, found by subject), its
/// case-side mirror, and the per-role claims that make "at most one live
/// binding per role per case" structural — and every write that touches more
/// than one of them is ONE transaction. Nothing here is accessor-scoped: the
/// invitation routes resolve the case under the caller's accessor FIRST.
/// </summary>
public interface IClientBindingStore
{
    /// <summary>
    /// Write a new (or re-bound) binding, its mirror and its role claims, and
    /// rewrite each <paramref name="narrowed"/> binding with its smaller role
    /// set — in one transaction. MUST refuse with <see cref="ConflictException"/>,
    /// writing nothing, when the subject already holds a LIVE binding in the
    /// firm, or any role is held by a subject other than the new one and the
    /// narrowed ones (the refusal that holds under a race).
    /// </summary>
    Task BindAsync(ClientBinding binding, IReadOnlyList<ClientBinding> narrowed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrite an existing binding and its mirror — a status change. A
    /// revocation also releases the role claims it names, in the same
    /// transaction — and MUST throw <see cref="ConflictException"/>
    /// (<see cref="Clients.ClientBindings.AccessChanged"/>), writing nothing,
    /// when one of them is now held by another subject (a revoke from a stale
    /// read). Null when the binding does not exist.
    /// </summary>
    Task<ClientBinding?> UpdateAsync(ClientBinding binding, CancellationToken cancellationToken = default);

    /// <summary>The authoritative binding by key, STRONGLY consistent — a revocation takes effect on the next request.</summary>
    Task<ClientBinding?> GetAsync(string firmId, string subject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which binding a portal token's subject holds — the one unscoped read.
    /// MUST throw rather than choose when the subject resolves in more than one firm.
    /// </summary>
    Task<ClientBinding?> FindAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>This firm's binding for <paramref name="email"/>, if it ever had one (a refiled case re-binds through it).</summary>
    Task<ClientBinding?> FindByEmailAsync(string firmId, string email, CancellationToken cancellationToken = default);

    /// <summary>Every binding on a case, revoked included, oldest first.</summary>
    Task<IReadOnlyList<ClientBinding>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default);
}
