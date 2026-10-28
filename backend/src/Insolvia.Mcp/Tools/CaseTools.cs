using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Candidates;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using Insolvia.Mcp.Views;

namespace Insolvia.Mcp.Tools;

/// <summary>
/// The eight tools' MEANING (services/mcp <c>core/tools.py</c>): which store
/// answers each tool, which permission gates it, and what every refusal is.
/// Everything protocol-shaped lives in <see cref="InsolviaTool"/>; this class
/// raises Core's <see cref="ApiException"/> family exactly as the API does,
/// so each error class's documented reasoning governs both surfaces.
/// </summary>
/// <remarks>
/// <para>Three inherited rules shape every method (mcp-surface.md):</para>
/// <list type="bullet">
/// <item>The firm is never an argument: every method takes a resolved
/// <see cref="Accessor"/>, resolved per call above this class, never cached.</item>
/// <item>Permission checks run HERE, below auth, failing closed. A tool the
/// caller may not use is listed but refuses (<c>permission_denied</c>);
/// another firm's case answers <c>not_found</c>, indistinguishable from none.</item>
/// <item>There is NO method that writes a case record. The write half of the
/// surface is candidates, so confirm-before-entry holds structurally: the
/// code path does not exist. This class holds no store that could write one
/// other than through <see cref="ICandidateStore"/>.</item>
/// </list>
/// </remarks>
public sealed class CaseTools(
    ICaseStore cases,
    ICaseEntityStore entities,
    IDebtorStore debtors,
    IDocumentStore documents,
    ICandidateStore candidates,
    IAccessLog accessLog,
    IFirmStore firms)
{
    /// <summary>The listing ceiling (mcp-surface.md § Pagination) — the API's number.</summary>
    public const int MaxLimit = 100;

    /// <summary>The surface's default page: an agent's context window is the budget.</summary>
    public const int DefaultLimit = 25;

    /// <summary>The debtor collection — keyed by filing role, not a generic collection.</summary>
    public const string DebtorsType = "debtors";

    /// <summary>Document metadata — the documents feature, not intake.</summary>
    public const string DocumentsType = "documents";

    /// <summary>
    /// A case's clients (ADR 0022): the FIRM clients its debtors are linked to,
    /// read from the firm table, reached only through a case, read-only.
    /// </summary>
    public const string ClientsType = "clients";

    /// <summary>
    /// The entity types the generic record tools serve: every registered
    /// collection, then the non-generic case children, then clients. DERIVED
    /// from the registry, never written out — a collection registered in Core
    /// is a new value here with no change to this file.
    /// </summary>
    public static readonly IReadOnlyList<string> EntityTypes =
        [.. CaseCollections.Kinds.Select(kind => kind.Collection), DebtorsType, DocumentsType, ClientsType];

    /// <summary>
    /// The per-entity gate map (mcp-surface.md § Permission gates), mirroring
    /// the API's routes: documents is the documents feature, clients the
    /// clients feature (the first type not under intake), the rest intake.
    /// </summary>
    /// <param name="entityType">A validated entity type.</param>
    /// <returns>The feature that gates it.</returns>
    public static Feature FeatureFor(string entityType) => entityType switch
    {
        DocumentsType => Feature.Documents,
        ClientsType => Feature.Clients,
        _ => Feature.Intake,
    };

    /// <summary>
    /// <c>whoami</c> — the <c>/v1/me</c> of this surface, and the ONE tool
    /// that answers without a firm: it reports the firm or its absence, so an
    /// agent whose user was never provisioned gets a fact, not a refusal.
    /// </summary>
    /// <param name="accessor">The caller's accessor, or null.</param>
    /// <returns>The <c>WhoamiResult</c>.</returns>
    public static JsonObject Whoami(Accessor? accessor)
    {
        if (accessor is null)
        {
            return new JsonObject
            {
                ["firm"] = null,
                ["displayName"] = string.Empty,
                ["isAdmin"] = false,
                ["accessAllCases"] = false,
                ["permissions"] = new JsonObject(),
            };
        }

        var permissions = new JsonObject();
        foreach (var feature in Wire.Members<Feature>())
        {
            permissions[feature.ToWire()] = Permissions.PermissionFor(accessor.User, feature).ToWire();
        }

        return new JsonObject
        {
            ["firm"] = new JsonObject { ["id"] = accessor.Firm.Id, ["name"] = accessor.Firm.Name },
            ["displayName"] = accessor.User.FullName,
            ["isAdmin"] = accessor.User.IsAdmin,
            ["accessAllCases"] = accessor.User.AccessAllCases,
            ["permissions"] = permissions,
        };
    }

    /// <summary><c>tools.py</c>'s <c>parse_limit</c>: absent is the default; 1–100 otherwise.</summary>
    /// <param name="value">The argument, already coerced to an integer or null.</param>
    /// <returns>The limit.</returns>
    public static int ParseLimit(long? value)
    {
        if (value is null)
        {
            return DefaultLimit;
        }

        if (value is < 1 or > MaxLimit)
        {
            throw new ValidationException($"limit must be between 1 and {MaxLimit}");
        }

        return (int)value.Value;
    }

    /// <summary><c>list_cases</c>: the caller's working list, newest first, filtered within the page.</summary>
    /// <param name="accessor">The caller.</param>
    /// <param name="status">An optional case status.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cursor">The previous page's <c>nextCursor</c>.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The <c>ListCasesResult</c>.</returns>
    public async Task<JsonObject> ListCasesAsync(Accessor accessor, string? status = null, long? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        Require(accessor, Feature.Cases, PermissionLevel.ViewOnly);
        CaseStatus? wanted = null;
        if (status is not null)
        {
            if (!Wire.TryParse<CaseStatus>(status, out var parsed))
            {
                throw new ValidationException("status must be one of: " + string.Join(", ", Wire.Names<CaseStatus>()));
            }

            wanted = parsed;
        }

        var page = await cases.ListForAccessorAsync(accessor, ParseLimit(limit), cursor, cancellationToken: cancellationToken).ConfigureAwait(false);

        // The status filter is applied WITHIN the page rather than pushed into
        // the store: the listing is ordered by creation, not status, and a
        // filtered page that silently skipped ahead would need a third index.
        // A short page with a nextCursor is the honest answer.
        var result = new JsonObject
        {
            ["cases"] = new JsonArray([.. page.Cases.Where(found => wanted is null || found.Status == wanted).Select(found => (JsonNode)WireViews.Case(found))]),
        };
        if (page.NextCursor is not null)
        {
            result["nextCursor"] = page.NextCursor;
        }

        return result;
    }

    /// <summary><c>get_case</c>: the case root plus a count per entity type.</summary>
    /// <param name="accessor">The caller.</param>
    /// <param name="caseId">The case.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The <c>GetCaseResult</c>.</returns>
    public async Task<JsonObject> GetCaseAsync(Accessor accessor, string caseId, CancellationToken cancellationToken = default)
    {
        Require(accessor, Feature.Cases, PermissionLevel.ViewOnly);
        var found = await ReachableCaseAsync(accessor, caseId, AccessAction.CaseRead, cancellationToken).ConfigureAwait(false);

        // One listing per entity type — a couple of dozen keyed reads against
        // one case. Honest cost, stated: the ports expose per-type listings
        // because that is the dominant access pattern.
        var counts = new JsonObject();
        foreach (var entityType in EntityTypes)
        {
            counts[entityType] = (await RecordsAsync(found, entityType, cancellationToken).ConfigureAwait(false)).Count;
        }

        return new JsonObject { ["case"] = WireViews.Case(found), ["recordCounts"] = counts };
    }

    /// <summary><c>list_case_records</c>: one entity type's records, paginated by bound offset cursors.</summary>
    /// <param name="accessor">The caller.</param>
    /// <param name="caseId">The case.</param>
    /// <param name="entityType">The entity type.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cursor">The previous page's <c>nextCursor</c>.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The <c>ListCaseRecordsResult</c>.</returns>
    public async Task<JsonObject> ListCaseRecordsAsync(Accessor accessor, string caseId, string entityType, long? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var validated = ValidatedEntityType(entityType);
        Require(accessor, FeatureFor(validated), PermissionLevel.ViewOnly);
        var pageLimit = ParseLimit(limit);
        var action = validated == DocumentsType ? AccessAction.DocumentRead : AccessAction.CaseRead;
        var found = await ReachableCaseAsync(accessor, caseId, action, cancellationToken).ConfigureAwait(false);

        // Not logged per client: a listing is not, matching the API's
        // `GET /v1/firm/clients` — the case.read row above records it.
        var records = await RecordsAsync(found, validated, cancellationToken).ConfigureAwait(false);
        var (page, next) = Paginate(records, pageLimit, cursor, OffsetBinding(caseId, $"records:{validated}"));
        var result = new JsonObject { ["records"] = new JsonArray([.. page]) };
        if (next is not null)
        {
            result["nextCursor"] = next;
        }

        return result;
    }

    /// <summary><c>get_case_record</c>: one record by id.</summary>
    /// <param name="accessor">The caller.</param>
    /// <param name="caseId">The case.</param>
    /// <param name="entityType">The entity type.</param>
    /// <param name="recordId">The record.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The <c>GetCaseRecordResult</c>.</returns>
    public async Task<JsonObject> GetCaseRecordAsync(Accessor accessor, string caseId, string entityType, string recordId, CancellationToken cancellationToken = default)
    {
        var validated = ValidatedEntityType(entityType);
        Require(accessor, FeatureFor(validated), PermissionLevel.ViewOnly);
        if (string.IsNullOrEmpty(recordId))
        {
            throw new ValidationException("recordId is required");
        }

        var action = validated == DocumentsType ? AccessAction.DocumentRead : AccessAction.CaseRead;
        var found = await ReachableCaseAsync(accessor, caseId, action, cancellationToken).ConfigureAwait(false);

        // A record id from another case does not resolve — the case is half
        // every key — so this is the same not_found a foreign id gets.
        var record = await FindRecordAsync(found, validated, recordId, accessor, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("record not found");
        return new JsonObject { ["record"] = record };
    }

    /// <summary>
    /// <c>propose_case_records</c>: validate the WHOLE batch, then write one
    /// pending CANDIDATE per proposal — never a case record.
    /// </summary>
    /// <param name="accessor">The caller.</param>
    /// <param name="caseId">The case.</param>
    /// <param name="proposals">The raw batch.</param>
    /// <param name="clientId">The verified token's app client — the origin, never an argument.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The <c>ProposeCaseRecordsResult</c>.</returns>
    public async Task<JsonObject> ProposeCaseRecordsAsync(Accessor accessor, string caseId, JsonNode? proposals, string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        Require(accessor, Feature.Intake, PermissionLevel.AddEdit);

        // Validated BEFORE the case is resolved, so a malformed request
        // answers validation_failed without an access-log row saying somebody
        // aimed machinery at the case.
        var drafts = CandidateRules.ParseProposals(proposals);
        await ReachableCaseAsync(accessor, caseId, AccessAction.CandidatePropose, cancellationToken).ConfigureAwait(false);

        // Both halves of the origin from the VERIFIED TOKEN.
        var origin = new CandidateOrigin(OriginChannel.Mcp, clientId, accessor.Subject);
        var stored = new JsonArray();
        foreach (var draft in drafts)
        {
            var candidate = CandidateRules.Create(draft, caseId, origin);
            await candidates.CreateAsync(candidate, cancellationToken).ConfigureAwait(false);
            stored.Add(new JsonObject
            {
                ["candidateId"] = candidate.Id,
                ["entityType"] = candidate.EntityType,
                ["status"] = candidate.Status.ToWire(),
            });
        }

        return new JsonObject { ["candidates"] = stored };
    }

    /// <summary><c>check_proposals</c>: the review status of a case's candidates, filtered and paginated.</summary>
    /// <param name="accessor">The caller.</param>
    /// <param name="caseId">The case.</param>
    /// <param name="candidateIds">Only these candidates, when given.</param>
    /// <param name="status">Only this status, when given.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cursor">The previous page's <c>nextCursor</c>.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The <c>CheckProposalsResult</c>.</returns>
    public async Task<JsonObject> CheckProposalsAsync(Accessor accessor, string caseId, IReadOnlyList<string>? candidateIds = null, string? status = null, long? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        Require(accessor, Feature.Intake, PermissionLevel.ViewOnly);
        CandidateStatus? wanted = null;
        if (status is not null)
        {
            if (!Wire.TryParse<CandidateStatus>(status, out var parsed))
            {
                throw new ValidationException("status must be one of: " + string.Join(", ", Wire.Names<CandidateStatus>()));
            }

            wanted = parsed;
        }

        var pageLimit = ParseLimit(limit);
        await ReachableCaseAsync(accessor, caseId, AccessAction.CaseRead, cancellationToken).ConfigureAwait(false);
        var listed = await candidates.ListForCaseAsync(caseId, cancellationToken).ConfigureAwait(false);
        var rows = listed
            .Where(candidate => (candidateIds is null || candidateIds.Contains(candidate.Id, StringComparer.Ordinal))
                && (wanted is null || candidate.Status == wanted))
            .Select(candidate => (JsonNode)WireViews.CandidateReview(candidate))
            .ToList();
        var (page, next) = Paginate(rows, pageLimit, cursor, OffsetBinding(caseId, "candidates"));
        var result = new JsonObject { ["candidates"] = new JsonArray([.. page]) };
        if (next is not null)
        {
            result["nextCursor"] = next;
        }

        return result;
    }

    /// <summary><c>withdraw_proposal</c>: the proposer's own, only while pending.</summary>
    /// <param name="accessor">The caller.</param>
    /// <param name="caseId">The case.</param>
    /// <param name="candidateId">The candidate.</param>
    /// <param name="cancellationToken">Cancels the reads and the write.</param>
    /// <returns>The <c>WithdrawProposalResult</c>.</returns>
    public async Task<JsonObject> WithdrawProposalAsync(Accessor accessor, string caseId, string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        Require(accessor, Feature.Intake, PermissionLevel.AddEdit);
        if (string.IsNullOrEmpty(candidateId))
        {
            throw new ValidationException("candidateId is required");
        }

        await ReachableCaseAsync(accessor, caseId, AccessAction.CandidateWithdraw, cancellationToken).ConfigureAwait(false);
        var candidate = await candidates.GetAsync(caseId, candidateId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("candidate not found");
        var withdrawn = CandidateRules.Withdraw(candidate, accessor.Subject);

        // Conditional on the stored status still being pending: a withdrawal
        // racing the reviewer's acceptance loses, and hears the same conflict
        // a sequential attempt would.
        var stored = await candidates.UpdateAsync(withdrawn, CandidateStatus.Pending, cancellationToken).ConfigureAwait(false)
            ?? throw new ConflictException("candidate has already been reviewed");
        return new JsonObject { ["candidateId"] = stored.Id, ["status"] = stored.Status.ToWire() };
    }

    // The tool-layer `@requires`: the caller's-own-account refusal, never a 404.
    private static void Require(Accessor accessor, Feature feature, PermissionLevel level)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        if (!accessor.May(feature, level))
        {
            throw new ForbiddenException("your firm has not granted you access to this");
        }
    }

    private static string ValidatedEntityType(string? value) =>
        value is not null && EntityTypes.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new ValidationException("entityType must be one of: " + string.Join(", ", EntityTypes));

    // What an offset cursor is bound to: replayed against another case or
    // another entity type it answers validation_failed instead of paging the
    // wrong list. The case cursor codec, so it is opaque and validated.
    private static string OffsetBinding(string caseId, string listing) => $"mcp-offset:{caseId}:{listing}";

    // Offset pagination over a whole listing: the ports return a case's WHOLE
    // collection (their documented contract), so this is a presentation of
    // that answer, not a second query.
    private static (IReadOnlyList<JsonNode> Page, string? Next) Paginate(IReadOnlyList<JsonNode> records, int limit, string? cursor, string binding)
    {
        var offset = 0;
        if (cursor is not null)
        {
            var raw = CaseRules.DecodeCursor(cursor, binding).GetValueOrDefault("offset", string.Empty);
            if (raw.Length == 0 || !raw.All(char.IsAsciiDigit) || !int.TryParse(raw, out offset))
            {
                throw new ValidationException("cursor is not valid");
            }
        }

        var page = records.Skip(offset).Take(limit).Select(node => node.DeepClone()).ToList();
        string? next = null;
        if ((long)offset + limit < records.Count)
        {
            next = CaseRules.EncodeCursor(new Dictionary<string, string> { ["offset"] = (offset + limit).ToString(System.Globalization.CultureInfo.InvariantCulture) }, binding);
        }

        return (page, next);
    }

    // Resolve the case first, and record the attempt either way — the same
    // seam as the API's reachable-case check, and the only authorization
    // check there is: the child stores take no accessor and enforce nothing.
    private async Task<Case> ReachableCaseAsync(Accessor accessor, string caseId, AccessAction action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(caseId))
        {
            throw new ValidationException("caseId is required");
        }

        var found = await cases.GetAsync(caseId, accessor, cancellationToken).ConfigureAwait(false);
        await accessLog.RecordAsync(
            AccessEvents.Record(accessor.Subject, action, caseId: caseId, outcome: found is not null ? AccessOutcome.Allowed : AccessOutcome.Denied),
            cancellationToken).ConfigureAwait(false);

        // Identical to a case that does not exist — the anti-oracle 404.
        return found ?? throw new NotFoundException("case not found");
    }

    // The firm clients a case's debtors are linked to, in filing-role order,
    // each once. Resolved in the CASE's firm (which is the caller's). A link
    // that no longer resolves is skipped; archived clients are included.
    private async Task<List<FirmClient>> CaseClientsAsync(Case found, CancellationToken cancellationToken)
    {
        var linked = new List<FirmClient>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var debtor in await debtors.ListForCaseAsync(found.Id, cancellationToken).ConfigureAwait(false))
        {
            if (debtor.ClientId is null || !seen.Add(debtor.ClientId))
            {
                continue;
            }

            if (await firms.GetClientAsync(found.FirmId, debtor.ClientId, cancellationToken).ConfigureAwait(false) is { } client)
            {
                linked.Add(client);
            }
        }

        return linked;
    }

    // One entity type's records in the API's wire shape, so the surfaces
    // cannot drift. A client is the last-four view only.
    private async Task<IReadOnlyList<JsonNode>> RecordsAsync(Case found, string entityType, CancellationToken cancellationToken)
    {
        switch (entityType)
        {
            case ClientsType:
                return [.. (await CaseClientsAsync(found, cancellationToken).ConfigureAwait(false)).Select(client => (JsonNode)WireViews.FirmClient(client))];
            case DebtorsType:
                return [.. (await debtors.ListForCaseAsync(found.Id, cancellationToken).ConfigureAwait(false)).Select(debtor => (JsonNode)WireViews.Debtor(debtor))];
            case DocumentsType:
                return [.. (await documents.ListForCaseAsync(found.Id, cancellationToken).ConfigureAwait(false)).Select(document => (JsonNode)WireViews.Document(document))];
            default:
                var kind = CaseCollections.ByCollection[entityType];
                return [.. (await entities.ListForCaseAsync(found.Id, kind, cancellationToken).ConfigureAwait(false)).Select(entity => (JsonNode)WireViews.Entity(entity))];
        }
    }

    private async Task<JsonObject?> FindRecordAsync(Case found, string entityType, string recordId, Accessor accessor, CancellationToken cancellationToken)
    {
        switch (entityType)
        {
            case ClientsType:
                // Only a client this case's debtors name resolves; one this case
                // does not link answers the same not_found as another firm's. A
                // single-record read is PII with its own subject, so it is
                // logged under CLIENT#<id> as the API's is; refused ones denied.
                var client = (await CaseClientsAsync(found, cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(linked => linked.Id == recordId);
                await accessLog.RecordAsync(
                    AccessEvents.Record(accessor.Subject, AccessAction.ClientRead, clientId: recordId, outcome: client is not null ? AccessOutcome.Allowed : AccessOutcome.Denied),
                    cancellationToken).ConfigureAwait(false);
                return client is null ? null : WireViews.FirmClient(client);
            case DebtorsType:
                // Keyed by filing role, not id: a by-id read scans a ≤3-item listing.
                var debtor = (await debtors.ListForCaseAsync(found.Id, cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(candidate => candidate.Id == recordId);
                return debtor is null ? null : WireViews.Debtor(debtor);
            case DocumentsType:
                var document = await documents.GetAsync(found.Id, recordId, cancellationToken).ConfigureAwait(false);
                return document is null ? null : WireViews.Document(document);
            default:
                var entity = await entities.GetAsync(found.Id, CaseCollections.ByCollection[entityType], recordId, cancellationToken).ConfigureAwait(false);
                return entity is null ? null : WireViews.Entity(entity);
        }
    }
}
