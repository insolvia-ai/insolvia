using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Clients;
using Insolvia.Core.Debtors;
using Insolvia.Core.Provenance;
using Insolvia.Core.Questionnaires;
using Insolvia.Core.Questions;

namespace Insolvia.Core.Candidates;

/// <summary>One validated review request.</summary>
/// <param name="Accept">Accept (true) or reject.</param>
/// <param name="CorrectedPayload">The human's corrected record — an accept only.</param>
public sealed record ReviewDecision(bool Accept, JsonObject? CorrectedPayload = null);

/// <summary>
/// Extraction review — the human confirmation that turns candidates into case
/// data (from services/api core/extraction_review.py; issue #89 / 8.9). NOTHING
/// a machine (or a client) proposed enters the case until a person accepts it,
/// and this is the one place that acceptance is built: the reviewed payload
/// goes through the SAME parse every staff-typed write uses, carrying
/// provenance minted here — the channel's machine source on an unchanged
/// field, <c>staff_typed</c> on a field the human corrected, the confirmation
/// pair and the candidate id on every one.
/// </summary>
/// <remarks>
/// The candidate is compare-and-swapped BEFORE the record is written (the
/// route's order): of two racing reviewers only the winner reaches the write.
/// There is deliberately no bulk accept.
/// </remarks>
public static partial class ExtractionReview
{
    /// <summary>The link fields a CLIENT answer cannot fill and acceptance fills from the case.</summary>
    public static readonly IReadOnlyDictionary<string, string> ClientLinks = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["employments"] = "debtor_id",
        ["expenses"] = "household_id",
    };

    /// <summary>Which reference field may carry candidate-id indirection, per entity type.</summary>
    public static readonly IReadOnlyDictionary<string, string> LinkFields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["claims"] = "creditor_id",
        ["pay_period_records"] = "employment_id",
    };

    private static readonly string[] ReviewActions = ["accept", "reject"];

    /// <summary>The provenance source each origin channel confirms into.</summary>
    public static string SourceFor(OriginChannel channel) => channel switch
    {
        OriginChannel.Extraction => ProvenanceSource.AiExtracted.ToWire(),
        OriginChannel.Mcp => ProvenanceSource.Imported.ToWire(),
        OriginChannel.Client => ProvenanceSource.ClientAnswered.ToWire(),
        _ => throw new ValidationException($"candidates from channel '{channel.ToWire()}' are not reviewable"),
    };

    /// <summary>Validate POST …/review; unknown keys are ignored. Corrections ride on an accept only.</summary>
    public static ReviewDecision ParseReview(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var action = Json.AsString(payload["action"]);
        if (action is null || !ReviewActions.Contains(action, StringComparer.Ordinal))
        {
            throw Field("action", "Action must be one of " + string.Join(", ", ReviewActions) + ".");
        }

        JsonObject? corrected = null;
        if (payload.TryGetPropertyValue("correctedPayload", out var raw) && raw is not null)
        {
            if (action != "accept")
            {
                throw Field("correctedPayload", "Corrections ride on an accept.");
            }

            corrected = raw as JsonObject ?? throw Field("correctedPayload", "correctedPayload must be an object.");
            corrected = (JsonObject)corrected.DeepClone();
        }

        return new ReviewDecision(action == "accept", corrected);
    }

    /// <summary>The <c>?status=</c> filter, or a 400.</summary>
    public static CandidateStatus? ParseStatusFilter(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return Wire.TryParse<CandidateStatus>(value, out var status)
            ? status
            : throw new ValidationException("status must be one of " + string.Join(", ", Wire.Names<CandidateStatus>()));
    }

    /// <summary>The kind an accept would write, or a refusal (a <c>debtors</c> proposal is not acceptable in-app yet).</summary>
    public static IEntityKind ReviewableKind(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.EntityType == CandidateRules.Debtors)
        {
            throw new ValidationException("debtor proposals cannot be accepted in-app yet — enter the debtor through intake and reject this candidate");
        }

        return CaseCollections.Find(candidate.EntityType)
            ?? throw new ValidationException($"candidates of type '{candidate.EntityType}' are not reviewable");
    }

    /// <summary>
    /// Rewrite candidate-id indirection to the accepted record's id. A value
    /// naming no sibling passes through; one naming a sibling still awaiting
    /// review refuses with the instruction that makes the queue self-explaining.
    /// </summary>
    public static JsonObject ResolveCandidateReferences(string entityType, JsonObject payload, IReadOnlyDictionary<string, Candidate> siblings)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(siblings);
        if (!LinkFields.TryGetValue(entityType, out var field) || Json.AsString(payload[field]) is not { } value || !siblings.TryGetValue(value, out var referenced))
        {
            return payload;
        }

        if (referenced.ResultingRecordId is null)
        {
            throw Field(field, $"This record references another extracted candidate ({referenced.EntityType}) that has not been accepted yet — review that one first.");
        }

        var resolved = (JsonObject)payload.DeepClone();
        resolved[field] = referenced.ResultingRecordId;
        return resolved;
    }

    /// <summary>
    /// The record acceptance writes: <paramref name="payload"/> (final,
    /// reference-resolved) plus minted provenance, through the same parse every
    /// write path uses. Which source a field gets is decided against the
    /// CANDIDATE'S original payload, both canonicalised by the same parser.
    /// </summary>
    public static IEntityDraft BuildAcceptedDraft(Candidate candidate, JsonObject payload, string confirmedBy, string confirmedAt)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(payload);
        var kind = ReviewableKind(candidate);
        var machineSource = SourceFor(candidate.Origin.Channel);
        var body = kind.ParseDraft(payload, enforceProvenance: false).BodyNode();
        JsonObject original;
        try
        {
            original = kind.ParseDraft(candidate.Payload, enforceProvenance: false).BodyNode();
        }
        catch (FieldValidationException)
        {
            original = (JsonObject)candidate.Payload.DeepClone();
        }

        LinkFields.TryGetValue(candidate.EntityType, out var linkField);
        var provenance = new JsonObject();
        foreach (var path in ProvenanceRules.PopulatedPaths(body))
        {
            // The link rewrite is mechanical, not authorship.
            var unchanged = path == linkField || JsonNode.DeepEquals(ProvenanceRules.ValueAt(body, path), ProvenanceRules.ValueAt(original, path));
            var entry = new JsonObject
            {
                ["source"] = unchanged ? machineSource : ProvenanceSource.StaffTyped.ToWire(),
                ["confirmed_by"] = confirmedBy,
                ["confirmed_at"] = confirmedAt,
                ["extraction_id"] = candidate.Id,
            };
            if (unchanged)
            {
                foreach (var (key, value) in SourcePointers(candidate))
                {
                    entry[key] = value;
                }
            }

            provenance[path] = entry;
        }

        var withProvenance = (JsonObject)payload.DeepClone();
        withProvenance["provenance"] = provenance;
        return kind.ParseDraft(withProvenance);
    }

    /// <summary>A client's answer about a debtor's own fields — accepted by MERGING into that debtor.</summary>
    public static bool IsDebtorAnswer(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Origin.Channel == OriginChannel.Client && candidate.EntityType == CandidateRules.Debtors;
    }

    /// <summary>The filing role a client answer was given for — its question locator's.</summary>
    public static FilingRole AnswerFilingRole(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var role = Json.AsString(candidate.Locator?["filing_role"]);
        return role is not null && Wire.TryParse<ClientRole>(role, out _) && Wire.TryParse<FilingRole>(role, out var filingRole)
            ? filingRole
            : throw new ValidationException("this answer does not name which debtor it is for");
    }

    /// <summary>
    /// The debtor body with an answer applied: each top-level member REPLACES
    /// the stored one, except a list, whose elements are added — or replace the
    /// element with their id.
    /// </summary>
    public static JsonObject MergeAnswer(JsonObject stored, JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(payload);
        var merged = (JsonObject)stored.DeepClone();
        foreach (var (key, value) in payload)
        {
            if (value is JsonArray incoming && merged[key] is JsonArray current)
            {
                var ids = incoming.OfType<JsonObject>().Select(element => element["id"]?.ToJsonString()).ToHashSet(StringComparer.Ordinal);
                var kept = current.Where(element => !(element is JsonObject map && ids.Contains(map["id"]?.ToJsonString())));
                merged[key] = new JsonArray([.. kept.Select(element => element?.DeepClone()), .. incoming.Select(element => element?.DeepClone())]);
            }
            else
            {
                merged[key] = value?.DeepClone();
            }
        }

        return merged;
    }

    /// <summary>
    /// The debtor acceptance writes: the stored record with the reviewed answer
    /// merged in, validated with provenance ENFORCED. Every path the answer
    /// populates is minted fresh; every other field keeps its stored entry —
    /// except one under a replaced member whose value is gone with it.
    /// </summary>
    public static DebtorDraft BuildAcceptedDebtor(Candidate candidate, JsonObject payload, Debtor stored, string confirmedBy, string confirmedAt)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(stored);
        var source = SourceFor(OriginChannel.Client);
        var storedBody = DebtorRules.BodyNode(stored.Body);
        var merged = MergeAnswer(storedBody, payload);
        var answeredBody = DebtorRules.BodyNode(DebtorRules.Parse(payload, enforceProvenance: false).Body);
        JsonObject original;
        try
        {
            original = DebtorRules.BodyNode(DebtorRules.Parse(candidate.Payload, enforceProvenance: false).Body);
        }
        catch (FieldValidationException)
        {
            original = (JsonObject)candidate.Payload.DeepClone();
        }

        var answeredPaths = ProvenanceRules.PopulatedPaths(BodyJson.Prune(answeredBody));
        var replacedRoots = payload.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var provenance = new JsonObject();
        foreach (var (path, entry) in ProvenanceRules.ToJson(stored.Provenance))
        {
            var root = SegmentRoot().Match(path);
            var rootName = root.Success ? root.Groups[1].Value : path;
            if (replacedRoots.Contains(rootName) && !Survives(path, storedBody, merged))
            {
                continue;
            }

            provenance[path] = entry?.DeepClone();
        }

        foreach (var path in answeredPaths)
        {
            var unchanged = JsonNode.DeepEquals(ProvenanceRules.ValueAt(answeredBody, path), ProvenanceRules.ValueAt(original, path));
            provenance[path] = new JsonObject
            {
                ["source"] = unchanged ? source : ProvenanceSource.StaffTyped.ToWire(),
                ["confirmed_by"] = confirmedBy,
                ["confirmed_at"] = confirmedAt,
                ["extraction_id"] = candidate.Id,
            };
        }

        merged["provenance"] = provenance;
        return DebtorRules.Parse(merged);
    }

    /// <summary>A client record answer's link field, filled from the case — only for the client channel and only when empty.</summary>
    public static JsonObject FillClientLinks(Candidate candidate, JsonObject payload, string? debtorId, string? householdId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(payload);
        if (candidate.Origin.Channel != OriginChannel.Client || !ClientLinks.TryGetValue(candidate.EntityType, out var field) || Truthy(payload[field]))
        {
            return payload;
        }

        var value = field == "debtor_id" ? debtorId : householdId;
        if (value is null)
        {
            return payload;
        }

        var filled = (JsonObject)payload.DeepClone();
        filled[field] = value;
        return filled;
    }

    /// <summary>The reviewed row: <c>corrected</c> when the human changed it, <c>accepted</c> otherwise — retained either way.</summary>
    public static Candidate Accept(Candidate candidate, JsonObject? correctedPayload, string resultingRecordId, string confirmedBy, string confirmedAt, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate with
        {
            Status = correctedPayload is not null ? CandidateStatus.Corrected : CandidateStatus.Accepted,
            CorrectedPayload = correctedPayload,
            ResultingRecordId = resultingRecordId,
            ConfirmedBy = confirmedBy,
            ConfirmedAt = confirmedAt,
            UpdatedAt = Clock.Now(clock),
        };
    }

    /// <summary>The rejected row — retained, same reason corrections are.</summary>
    public static Candidate Reject(Candidate candidate, string confirmedBy, string confirmedAt, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate with { Status = CandidateStatus.Rejected, ConfirmedBy = confirmedBy, ConfirmedAt = confirmedAt, UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>One confirmation instant, minted once per review, so the row and every entry carry the same value.</summary>
    public static string ReviewMoment(TimeProvider? clock = null) => CaseRules.Iso(Clock.Now(clock));

    /// <summary>
    /// The review queue's wire shape (<c>candidate_json</c>): everything a
    /// reviewer needs to verify a record; absent values omitted.
    /// </summary>
    public static JsonObject CandidateJson(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var body = new JsonObject
        {
            ["id"] = candidate.Id,
            ["entityType"] = candidate.EntityType,
            ["status"] = candidate.Status.ToWire(),
            ["payload"] = BodyJson.Prune((JsonObject)candidate.Payload.DeepClone()),
            ["origin"] = new JsonObject
            {
                ["channel"] = candidate.Origin.Channel.ToWire(),
                ["clientId"] = candidate.Origin.ClientId,
                ["subject"] = candidate.Origin.Subject,
            },
            ["createdAt"] = CaseRules.Iso(candidate.CreatedAt),
            ["updatedAt"] = CaseRules.Iso(candidate.UpdatedAt),
        };
        if (candidate.DocumentId is not null)
        {
            body["documentId"] = candidate.DocumentId;
        }

        if (candidate.Confidence is { } confidence)
        {
            body["confidence"] = confidence;
        }

        if (candidate.Locator is not null)
        {
            body["locator"] = candidate.Locator.DeepClone();
        }

        if (candidate.Note is not null)
        {
            body["note"] = candidate.Note;
        }

        if (candidate.ExternalRef is { } external)
        {
            var reference = new JsonObject { ["system"] = external.System, ["externalId"] = external.ExternalId };
            if (external.ExternalUrl is not null)
            {
                reference["externalUrl"] = external.ExternalUrl;
            }

            body["externalRef"] = reference;
        }

        if (candidate.ConfirmedBy is not null)
        {
            body["confirmedBy"] = candidate.ConfirmedBy;
        }

        if (candidate.ConfirmedAt is not null)
        {
            body["confirmedAt"] = candidate.ConfirmedAt;
        }

        if (candidate.CorrectedPayload is not null)
        {
            body["correctedPayload"] = BodyJson.Prune((JsonObject)candidate.CorrectedPayload.DeepClone());
        }

        if (candidate.ResultingRecordId is not null)
        {
            body["resultingRecordId"] = candidate.ResultingRecordId;
        }

        return body;
    }

    /// <summary>The <c>question</c> block the staff queue shows beside a client's answer (<c>review_question_json</c>).</summary>
    public static JsonObject ReviewQuestionJson(AnswerRef answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var title = Questionnaire.Catalogue.FirstOrDefault(section => section.Id == answer.Question.Section)?.Title ?? answer.Question.Section;
        return new JsonObject
        {
            ["id"] = answer.Question.Id,
            ["sectionId"] = answer.Question.Section,
            ["sectionTitle"] = title,
            ["text"] = answer.Question.Text,
            ["filingRole"] = answer.FilingRole,
        };
    }

    // An unchanged field's pointers: the document, the page anchor (never a
    // client answer's question locator) and the model's confidence.
    private static IEnumerable<(string Key, JsonNode? Value)> SourcePointers(Candidate candidate)
    {
        if (candidate.DocumentId is not null)
        {
            yield return ("document_id", candidate.DocumentId);
        }

        if (candidate.Locator is not null && candidate.Origin.Channel != OriginChannel.Client)
        {
            yield return ("locator", candidate.Locator.DeepClone());
        }

        if (candidate.Confidence is { } confidence)
        {
            yield return ("confidence", confidence);
        }
    }

    private static bool Survives(string path, JsonObject before, JsonObject after)
    {
        var value = ProvenanceRules.ValueAt(after, path);
        return value is not null && JsonNode.DeepEquals(value, ProvenanceRules.ValueAt(before, path));
    }

    // Python's truthiness for a link value: absent, null and "" are empty.
    private static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonValue text when Json.AsString(text) is { } s => s.Length > 0,
        _ => true,
    };

    [GeneratedRegex("^([a-z][a-z0-9_]*)")]
    private static partial Regex SegmentRoot();

    private static FieldValidationException Field(string field, string message) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { [field] = message });
}
