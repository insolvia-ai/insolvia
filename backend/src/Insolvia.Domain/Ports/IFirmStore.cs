using Insolvia.Core.DocumentRequests;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.LibraryCreditors;
using Insolvia.Core.Questionnaires;

namespace Insolvia.Core.Ports;

/// <summary>
/// Firms, the people in them, and everything else that lives at firm scope:
/// the creditor library, the client directory (with its funnel and merge),
/// the questionnaire config and the document checklist (Python's
/// <c>FirmStore</c>; the DynamoDB firm partition's <c>META</c>, <c>USER#</c>,
/// <c>LIBCREDITOR#</c>, <c>FIRMCLIENT#</c>, <c>QUESTIONNAIRE</c> and
/// <c>DOCUMENT_CHECKLIST</c> items). <see cref="FindUserAsync"/> is the
/// hottest read in the API — it runs on every authenticated request — and
/// tenant isolation is an APPLICATION property (ADR 0001, ADR 0009).
/// </summary>
/// <remarks>
/// Every "only over a row that still exists AND still belongs to the firm"
/// below becomes an <c>UPDATE … WHERE</c> in the Postgres store, and zero
/// rows affected is the same null/false these methods return (ADR 0028).
/// </remarks>
public interface IFirmStore
{
    /// <summary>Store a new firm. MUST refuse to overwrite an existing id.</summary>
    Task CreateFirmAsync(Firm firm, CancellationToken cancellationToken = default);

    /// <summary>One firm by id.</summary>
    Task<Firm?> GetFirmAsync(string firmId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every firm, ordered by name then id — the admin surface's index, and
    /// the ONLY cross-tenant read of firms. The tenant API must never call it.
    /// </summary>
    Task<IReadOnlyList<Firm>> ListFirmsAsync(CancellationToken cancellationToken = default);

    /// <summary>Write <paramref name="firm"/> back, only over a row that still exists; null when it does not.</summary>
    Task<Firm?> UpdateFirmAsync(Firm firm, CancellationToken cancellationToken = default);

    /// <summary>Attach someone to a firm. MUST refuse to overwrite an existing (firm, subject).</summary>
    Task AddUserAsync(FirmUser user, CancellationToken cancellationToken = default);

    /// <summary>One user by (firm, subject) — firm-scoped, so another firm's subject is invisible.</summary>
    Task<FirmUser?> GetUserAsync(string firmId, string subject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which firm this subject belongs to — the ONLY unscoped user read, since
    /// its job is to discover the firm. MUST throw rather than choose when a
    /// subject resolves to more than one firm (one person, one firm is an
    /// application invariant; picking a row would make tenancy depend on order).
    /// </summary>
    Task<FirmUser?> FindUserAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>A firm's whole staff list, ordered by last name, first name, subject. Never truncated.</summary>
    Task<IReadOnlyList<FirmUser>> ListUsersAsync(string firmId, CancellationToken cancellationToken = default);

    /// <summary>Write <paramref name="user"/> back only over a row that still exists AND still belongs to its firm; null otherwise.</summary>
    Task<FirmUser?> UpdateUserAsync(FirmUser user, CancellationToken cancellationToken = default);

    /// <summary>Detach someone (the membership row only). True if this call removed them.</summary>
    Task<bool> RemoveUserAsync(string firmId, string subject, CancellationToken cancellationToken = default);

    /// <summary>Store a new library creditor. MUST refuse to overwrite an existing (firm, id).</summary>
    Task CreateLibraryCreditorAsync(LibraryCreditor creditor, CancellationToken cancellationToken = default);

    /// <summary>One library creditor, firm-scoped by key.</summary>
    Task<LibraryCreditor?> GetLibraryCreditorAsync(string firmId, string creditorId, CancellationToken cancellationToken = default);

    /// <summary>A firm's whole library, ordered by name then id. Never truncated.</summary>
    Task<IReadOnlyList<LibraryCreditor>> ListLibraryCreditorsAsync(string firmId, CancellationToken cancellationToken = default);

    /// <summary>Write back only over a row that still exists AND still belongs to its firm; null otherwise.</summary>
    Task<LibraryCreditor?> UpdateLibraryCreditorAsync(LibraryCreditor creditor, CancellationToken cancellationToken = default);

    /// <summary>Remove a library creditor (case copies survive untouched). True if this call removed it.</summary>
    Task<bool> DeleteLibraryCreditorAsync(string firmId, string creditorId, CancellationToken cancellationToken = default);

    /// <summary>Store a new client. MUST refuse to overwrite an existing (firm, id). There is no delete: archiving is a status write.</summary>
    Task CreateClientAsync(FirmClient client, CancellationToken cancellationToken = default);

    /// <summary>One client, firm-scoped by key.</summary>
    Task<FirmClient?> GetClientAsync(string firmId, string clientId, CancellationToken cancellationToken = default);

    /// <summary>A firm's whole directory, archived included, in <see cref="FirmClientRules.Sorted"/> order.</summary>
    Task<IReadOnlyList<FirmClient>> ListClientsAsync(string firmId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write back only over a row that still exists, still belongs to its
    /// firm, and was never merged away. Writes the record's own fields ONLY —
    /// the merge attributes and the prospect stage stay exactly as stored.
    /// </summary>
    Task<FirmClient?> UpdateClientAsync(FirmClient client, CancellationToken cancellationToken = default);

    /// <summary>
    /// Set (or, with null, remove) a prospect's funnel stage — ONLY IF the row
    /// exists in the firm, was never merged, and is still a prospect. Null
    /// when any condition failed; nothing is written then.
    /// </summary>
    Task<FirmClient?> SetClientProspectStageAsync(string firmId, string clientId, ProspectStage? stage, CancellationToken cancellationToken = default);

    /// <summary>
    /// THE RETAINED TRANSITION: set <c>FirstRetainedAt</c> to
    /// <paramref name="retainedOn"/> unless it already has a value, and
    /// remove the stage — in ONE write, conditional on the row existing in
    /// the firm and never having been merged.
    /// </summary>
    Task<FirmClient?> MarkClientRetainedAsync(string firmId, string clientId, DateOnly retainedOn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claim both rows for a merge — both or neither — only if both are in
    /// the firm, active and unmerged, the merged one is not receiving a merge
    /// and the survivor is not being merged away. A claim for THIS SAME pair
    /// passes (a half-finished merge can be run again). False otherwise.
    /// </summary>
    Task<bool> ClaimClientMergeAsync(string firmId, string mergedId, string survivorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archive the merged client with <c>MergedInto</c> = survivor and release
    /// both halves of the claim, in one write conditional on the claim still
    /// being this pair's. Null when it was not (a concurrent run finished it).
    /// </summary>
    Task<FirmClient?> FinishClientMergeAsync(string firmId, string mergedId, string survivorId, DateTimeOffset mergedAt, CancellationToken cancellationToken = default);

    /// <summary>Drop this pair's claim without archiving anything; another pair's claim is left alone.</summary>
    Task ReleaseClientMergeAsync(string firmId, string mergedId, string survivorId, CancellationToken cancellationToken = default);

    /// <summary>The firm's stored questionnaire config, or null for the defaults. Strongly consistent.</summary>
    Task<QuestionnaireConfig?> GetQuestionnaireAsync(string firmId, CancellationToken cancellationToken = default);

    /// <summary>Write the firm's whole config, replacing any stored one. Last writer wins.</summary>
    Task PutQuestionnaireAsync(QuestionnaireConfig config, CancellationToken cancellationToken = default);

    /// <summary>Reset to the defaults by DELETING the config. True if one was removed.</summary>
    Task<bool> DeleteQuestionnaireAsync(string firmId, CancellationToken cancellationToken = default);

    /// <summary>The firm's stored document checklist, or null for the default. Strongly consistent.</summary>
    Task<DocumentChecklist?> GetDocumentChecklistAsync(string firmId, CancellationToken cancellationToken = default);

    /// <summary>Write the firm's whole checklist, replacing any stored one. Last writer wins.</summary>
    Task PutDocumentChecklistAsync(DocumentChecklist checklist, CancellationToken cancellationToken = default);

    /// <summary>Reset to the default by DELETING the checklist. True if one was removed.</summary>
    Task<bool> DeleteDocumentChecklistAsync(string firmId, CancellationToken cancellationToken = default);
}
