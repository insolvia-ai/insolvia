using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Debtors;

namespace Insolvia.Core.Candidates;

// A candidate: a proposed case record awaiting a person's review
// (mcp-surface.md; case-data-model.md's extraction_candidate; from
// candidates.py). Not case data — accepting one is what writes a real record
// and mints its provenance (confirm-before-entry).

/// <summary>Who proposed it, through what — from the VERIFIED TOKEN, never an argument.</summary>
public sealed record CandidateOrigin(OriginChannel Channel, string ClientId, string Subject);

/// <summary>Where a harness sourced the record — carried onto the case record at acceptance.</summary>
public sealed record ExternalRef(string System, string ExternalId, string? ExternalUrl = null);

/// <summary>One candidate. The review members are set only once there is a review.</summary>
/// <param name="Id">The server-minted uuid.</param>
/// <param name="CaseId">The case.</param>
/// <param name="EntityType">The collection (or <c>debtors</c>) it targets.</param>
/// <param name="Payload">The proposed record, shape-validated by the target's own parser.</param>
/// <param name="Status">Its review state.</param>
/// <param name="Origin">Who proposed it.</param>
/// <param name="CreatedAt">When it was proposed — the list order.</param>
/// <param name="UpdatedAt">When it was last written.</param>
public sealed record Candidate(
    string Id,
    string CaseId,
    string EntityType,
    JsonObject Payload,
    CandidateStatus Status,
    CandidateOrigin Origin,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The source document (extraction only).</summary>
    public string? DocumentId { get; init; }

    /// <summary>Where a harness sourced it.</summary>
    public ExternalRef? ExternalRef { get; init; }

    /// <summary>The proposer's note.</summary>
    public string? Note { get; init; }

    /// <summary>The model's confidence (extraction only).</summary>
    public double? Confidence { get; init; }

    /// <summary>The page or question anchor.</summary>
    public JsonObject? Locator { get; init; }

    /// <summary>Who reviewed it.</summary>
    public string? ConfirmedBy { get; init; }

    /// <summary>When it was reviewed.</summary>
    public string? ConfirmedAt { get; init; }

    /// <summary>The reviewer's corrections.</summary>
    public JsonObject? CorrectedPayload { get; init; }

    /// <summary>The case record accepting it wrote.</summary>
    public string? ResultingRecordId { get; init; }
}

/// <summary>One validated proposal, before identity.</summary>
public sealed record ProposalDraft(string EntityType, JsonObject Payload, ExternalRef? ExternalRef, string? Note);

/// <summary>The candidate rules.</summary>
public static class CandidateRules
{
    /// <summary>1-25 proposals per call.</summary>
    public const int MaxProposalsPerCall = 25;

    /// <summary>The cap on a proposal's note.</summary>
    public const int MaxNote = 2000;

    /// <summary>The cap on each external-ref member.</summary>
    public const int MaxExternalRefField = 500;

    /// <summary>The debtor's entity type — the one proposable target outside the collections.</summary>
    public const string Debtors = "debtors";

    /// <summary>
    /// The entity types a proposal may target: every collection plus
    /// <c>debtors</c>. Not documents (bytes stay in the presigned flow), not the
    /// case root (an agent never initiates a matter).
    /// </summary>
    public static readonly IReadOnlyList<string> ProposableEntityTypes = [.. CaseCollections.Kinds.Select(kind => kind.Collection), Debtors];

    /// <summary>
    /// Validate a <c>propose_case_records</c> batch WHOLE before anything is
    /// stored — "we kept the first two" is a partial write the caller cannot
    /// discover. Payloads run through the target's OWN parser, provenance not
    /// enforced (a candidate is not case data yet).
    /// </summary>
    public static ValueCollection<ProposalDraft> ParseProposals(JsonNode? value)
    {
        if (value is not JsonArray list)
        {
            throw new ValidationException("proposals must be an array");
        }

        if (list.Count is < 1 or > MaxProposalsPerCall)
        {
            throw new ValidationException($"proposals must contain between 1 and {MaxProposalsPerCall} items");
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var drafts = new List<ProposalDraft>();
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index] is not JsonObject raw)
            {
                errors[$"proposals[{index}]"] = "Each proposal must be an object.";
                continue;
            }

            var entityType = Json.AsString(raw["entityType"]);
            if (entityType is null || !ProposableEntityTypes.Contains(entityType, StringComparer.Ordinal))
            {
                errors[$"proposals[{index}].entityType"] = "entityType must be one of: " + string.Join(", ", ProposableEntityTypes) + ".";
                continue;
            }

            if (raw["payload"] is not JsonObject payload)
            {
                errors[$"proposals[{index}].payload"] = "payload must be an object.";
                continue;
            }

            try
            {
                ValidatePayloadShape(entityType, payload);
            }
            catch (FieldValidationException error)
            {
                foreach (var (path, message) in error.Fields)
                {
                    errors[$"proposals[{index}].payload.{path}"] = message;
                }

                continue;
            }

            var noteNode = raw["note"];
            var note = Json.AsString(noteNode);
            if (noteNode is not null && note is null)
            {
                errors[$"proposals[{index}].note"] = "note must be a string.";
                continue;
            }

            if (note is not null && PyText.Length(note) > MaxNote)
            {
                errors[$"proposals[{index}].note"] = $"Must be at most {MaxNote} characters.";
                continue;
            }

            var externalRef = ParseExternalRef(raw["externalRef"], index, errors);
            drafts.Add(new ProposalDraft(entityType, payload, externalRef, note is not null && PyText.Strip(note).Length > 0 ? PyText.Strip(note) : null));
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return drafts.ToValueCollection();
    }

    /// <summary>
    /// A fresh pending candidate. The extras are the extraction stream's (the
    /// source document, the model's confidence, the page anchor); an MCP
    /// proposal passes none.
    /// </summary>
    public static Candidate Create(
        ProposalDraft draft,
        string caseId,
        CandidateOrigin origin,
        string? documentId = null,
        double? confidence = null,
        JsonObject? locator = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.Now(clock);
        return new Candidate(Guid.NewGuid().ToString(), caseId, draft.EntityType, draft.Payload, CandidateStatus.Pending, origin, now, now)
        {
            DocumentId = documentId,
            ExternalRef = draft.ExternalRef,
            Note = draft.Note,
            Confidence = confidence,
            Locator = locator,
        };
    }

    /// <summary>
    /// The withdrawn copy, or a refusal: 403 for somebody else's candidate (a
    /// colleague can already see it — only whose retraction it is is at stake),
    /// 409 once reviewed.
    /// </summary>
    public static Candidate Withdraw(Candidate candidate, string subject, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Origin.Subject != subject)
        {
            throw new ForbiddenException("only the proposer may withdraw a candidate");
        }

        if (candidate.Status != CandidateStatus.Pending)
        {
            throw new ConflictException($"candidate has already been reviewed (status: {candidate.Status.ToWire()})");
        }

        return candidate with { Status = CandidateStatus.Withdrawn, UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>Creation order, id as the tiebreak.</summary>
    public static IComparer<Candidate> ListOrder { get; } = Comparer<Candidate>.Create((left, right) =>
    {
        var byTime = left.CreatedAt.CompareTo(right.CreatedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(left.Id, right.Id);
    });

    /// <summary>The payload through the target's own parser, provenance not enforced.</summary>
    internal static void ValidatePayloadShape(string entityType, JsonObject payload)
    {
        if (entityType == Debtors)
        {
            _ = DebtorRules.Parse(payload, enforceProvenance: false);
            return;
        }

        _ = CaseCollections.ByCollection[entityType].ParseDraft(payload, enforceProvenance: false);
    }

    private static ExternalRef? ParseExternalRef(JsonNode? value, int index, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return null;
        }

        var prefix = $"proposals[{index}].externalRef";
        if (value is not JsonObject raw)
        {
            errors[prefix] = "externalRef must be an object.";
            return null;
        }

        var system = Json.AsString(raw["system"]);
        var externalId = Json.AsString(raw["externalId"]);
        var urlNode = raw["externalUrl"];
        var externalUrl = Json.AsString(urlNode);
        var refErrors = false;
        if (system is null || PyText.Strip(system).Length == 0)
        {
            errors[$"{prefix}.system"] = "A source system name is required.";
            refErrors = true;
        }

        if (externalId is null || PyText.Strip(externalId).Length == 0)
        {
            errors[$"{prefix}.externalId"] = "A source record id is required.";
            refErrors = true;
        }

        if (urlNode is not null && (externalUrl is null || PyText.Strip(externalUrl).Length == 0))
        {
            errors[$"{prefix}.externalUrl"] = "externalUrl must be a string.";
            refErrors = true;
        }

        foreach (var (name, text) in new[] { ("system", system), ("externalId", externalId), ("externalUrl", externalUrl) })
        {
            if (text is not null && PyText.Length(text) > MaxExternalRefField)
            {
                errors[$"{prefix}.{name}"] = $"Must be at most {MaxExternalRefField} characters.";
                refErrors = true;
            }
        }

        if (refErrors || system is null || externalId is null)
        {
            return null;
        }

        return new ExternalRef(
            PyText.Strip(system),
            PyText.Strip(externalId),
            externalUrl is not null && PyText.Strip(externalUrl).Length > 0 ? PyText.Strip(externalUrl) : null);
    }
}

/// <summary>A candidate's review state (Python's <c>STATUSES</c>).</summary>
public enum CandidateStatus
{
    /// <summary>Awaiting review.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>Accepted as proposed.</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    /// <summary>Accepted with a reviewer's corrections.</summary>
    [JsonStringEnumMemberName("corrected")]
    Corrected,

    /// <summary>Refused by the reviewer.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,

    /// <summary>Retracted by its proposer before review.</summary>
    [JsonStringEnumMemberName("withdrawn")]
    Withdrawn,
}

/// <summary>Where a candidate came from (Python's <c>ORIGIN_CHANNELS</c>).</summary>
public enum OriginChannel
{
    /// <summary>An MCP harness.</summary>
    [JsonStringEnumMemberName("mcp")]
    Mcp,

    /// <summary>Document extraction.</summary>
    [JsonStringEnumMemberName("extraction")]
    Extraction,

    /// <summary>The client, through the portal (ADR 0023).</summary>
    [JsonStringEnumMemberName("client")]
    Client,
}
