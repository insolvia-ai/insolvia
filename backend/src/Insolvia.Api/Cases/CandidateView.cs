using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Domain.Candidates;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Questionnaires;
using Insolvia.Domain.Questions;

namespace Insolvia.Api.Cases;

/// <summary>
/// A candidate in the review queue: a record proposed by extraction, an MCP
/// harness or the client portal, with everything a reviewer needs to verify
/// it. Optional members are absent, never null.
/// </summary>
/// <param name="Id">The candidate's id.</param>
/// <param name="EntityType">The kind of record it proposes, e.g. <c>creditor</c> or <c>debtor_answer</c>.</param>
/// <param name="Status">Where it is in review.</param>
/// <param name="Payload">The proposed record body, its unanswered fields absent; its shape is the entity type's.</param>
/// <param name="Origin">Who proposed it.</param>
/// <param name="CreatedAt">Proposed, ISO 8601 UTC.</param>
/// <param name="UpdatedAt">Last changed, ISO 8601 UTC.</param>
/// <param name="DocumentId">The source document (extraction only).</param>
/// <param name="Confidence">The model's confidence, 0 to 1 (extraction only).</param>
/// <param name="Locator">Where in the source it was found: a page anchor, or for a client's answer the question.</param>
/// <param name="Note">The proposer's note.</param>
/// <param name="ExternalRef">Where an MCP harness sourced it.</param>
/// <param name="ConfirmedBy">Who reviewed it.</param>
/// <param name="ConfirmedAt">When it was reviewed, ISO 8601 UTC.</param>
/// <param name="CorrectedPayload">The reviewer's corrected body, when it was accepted with corrections.</param>
/// <param name="ResultingRecordId">The record its acceptance wrote.</param>
/// <param name="Question">For a client's answer in the queue: the question it answers.</param>
/// <param name="Client">For a client's answer in the queue: who gave it, when known.</param>
public sealed record CandidateView(
    string Id,
    string EntityType,
    CandidateStatus Status,
    JsonObject Payload,
    CandidateOriginView Origin,
    string CreatedAt,
    string UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DocumentId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Confidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Locator,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExternalRefView? ExternalRef,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConfirmedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConfirmedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? CorrectedPayload,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResultingRecordId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewQuestionView? Question,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CandidateClientView? Client)
{
    /// <summary>The view of <paramref name="candidate"/>, without the queue's question and client.</summary>
    /// <param name="candidate">The stored candidate.</param>
    /// <returns>The view.</returns>
    public static CandidateView Of(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var external = candidate.ExternalRef;
        return new(
            candidate.Id,
            candidate.EntityType,
            candidate.Status,
            BodyJson.Prune(candidate.Payload),
            new(candidate.Origin.Channel, candidate.Origin.ClientId, candidate.Origin.Subject),
            CaseRules.Iso(candidate.CreatedAt),
            CaseRules.Iso(candidate.UpdatedAt),
            candidate.DocumentId,
            candidate.Confidence,
            candidate.Locator,
            candidate.Note,
            external is null ? null : new(external.System, external.ExternalId, external.ExternalUrl),
            candidate.ConfirmedBy,
            candidate.ConfirmedAt,
            candidate.CorrectedPayload is null ? null : BodyJson.Prune(candidate.CorrectedPayload),
            candidate.ResultingRecordId,
            null,
            null);
    }

    /// <summary>The queue's view of <paramref name="candidate"/>: a client's answer carries its question and, when known, who gave it.</summary>
    /// <param name="candidate">The stored candidate.</param>
    /// <param name="names">Client display names, by subject.</param>
    /// <returns>The view.</returns>
    public static CandidateView InQueue(Candidate candidate, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(names);
        var view = Of(candidate);
        if (QuestionRules.RefOf(candidate) is not { } answer)
        {
            return view;
        }

        return view with
        {
            Question = ReviewQuestionView.Of(answer),
            Client = names.TryGetValue(candidate.Origin.Subject, out var name) ? new CandidateClientView(name) : null,
        };
    }
}

/// <summary>Who proposed a candidate.</summary>
/// <param name="Channel">How it arrived.</param>
/// <param name="ClientId">The proposing client (an MCP harness or the portal).</param>
/// <param name="Subject">The proposer's subject.</param>
public sealed record CandidateOriginView(OriginChannel Channel, string ClientId, string Subject);

/// <summary>Where an MCP harness sourced a candidate.</summary>
/// <param name="System">The source system.</param>
/// <param name="ExternalId">The record's id there.</param>
/// <param name="ExternalUrl">A link to it; absent when none.</param>
public sealed record ExternalRefView(
    string System,
    string ExternalId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExternalUrl);

/// <summary>The question a client's answer answers.</summary>
/// <param name="Id">The question's id.</param>
/// <param name="SectionId">Its questionnaire section.</param>
/// <param name="SectionTitle">The section's title.</param>
/// <param name="Text">The question, as the client saw it.</param>
/// <param name="FilingRole">Which debtor it was about.</param>
public sealed record ReviewQuestionView(string Id, string SectionId, string SectionTitle, string Text, string FilingRole)
{
    /// <summary>The view of <paramref name="answer"/>'s question.</summary>
    /// <param name="answer">The answer's reference.</param>
    /// <returns>The view.</returns>
    public static ReviewQuestionView Of(AnswerRef answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var title = Questionnaire.Catalogue.FirstOrDefault(section => section.Id == answer.Question.Section)?.Title ?? answer.Question.Section;
        return new(answer.Question.Id, answer.Question.Section, title, answer.Question.Text, answer.FilingRole);
    }
}

/// <summary>The client who gave an answer.</summary>
/// <param name="DisplayName">The name the firm gave at invitation.</param>
public sealed record CandidateClientView(string DisplayName);

/// <summary>A case's review queue, creation order.</summary>
/// <param name="Candidates">The candidates.</param>
public sealed record CandidateList(IReadOnlyList<CandidateView> Candidates);

/// <summary>A reviewed candidate and, when it was accepted, the record its acceptance wrote.</summary>
/// <param name="Candidate">The candidate, as reviewed.</param>
/// <param name="Record">
/// Absent on a rejection. On an acceptance, the record written: an
/// <c>EntityView</c> of the candidate's collection's body, or — for a client's answer
/// about a debtor — the <c>DebtorView</c> it merged into.
/// </param>
public sealed record CandidateReview(
    CandidateView Candidate,
    // shortcut: object, because the document generator cannot say "EntityView or DebtorView" without a discriminator the wire does not carry.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Record);

/// <summary>
/// One review decision (<c>POST …/candidates/{candidateId}/review</c>).
/// Parsed by <c>ExtractionReview.ParseReview</c>, which owns validation.
/// </summary>
/// <param name="Action"><c>accept</c> or <c>reject</c>.</param>
/// <param name="CorrectedPayload">On accept only, the corrected record body to write instead of the proposal.</param>
public sealed record CandidateReviewRequest(
    string Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? CorrectedPayload);
