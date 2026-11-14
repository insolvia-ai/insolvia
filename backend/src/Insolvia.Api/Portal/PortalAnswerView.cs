using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Domain.Candidates;
using Insolvia.Domain.Clients;
using Insolvia.Domain.Questions;

namespace Insolvia.Api.Portal;

/// <summary>
/// A client's own answer: the value THEY gave and where it stands. No
/// corrected payload, no reviewer, no resulting record.
/// </summary>
/// <param name="Id">The answer's id.</param>
/// <param name="QuestionId">The question it answers.</param>
/// <param name="SectionId">The question's section.</param>
/// <param name="FilingRole">The debtor it is about.</param>
/// <param name="Value">The value given, by input key; its members are the question's inputs.</param>
/// <param name="Status">Where it stands in the firm's review.</param>
/// <param name="CreatedAt">When given, ISO 8601 UTC with microseconds.</param>
/// <param name="UpdatedAt">When last changed, ISO 8601 UTC with microseconds.</param>
public sealed record PortalAnswerView(
    string Id,
    string QuestionId,
    string SectionId,
    string FilingRole,
    JsonObject Value,
    CandidateStatus Status,
    string CreatedAt,
    string UpdatedAt)
{
    /// <summary>The view of <paramref name="candidate"/>.</summary>
    /// <param name="candidate">The answer's candidate.</param>
    /// <param name="answer">Its question and role.</param>
    /// <returns>The view.</returns>
    public static PortalAnswerView Of(Candidate candidate, AnswerRef answer)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(answer);
        return new(
            candidate.Id,
            answer.Question.Id,
            answer.Question.Section,
            answer.FilingRole,
            QuestionRules.AnswerValue(answer.Question, candidate.Payload),
            candidate.Status,
            WireFormat.Micros(candidate.CreatedAt),
            WireFormat.Micros(candidate.UpdatedAt));
    }
}

/// <summary>The client's own answers, every status, in sections the firm still shows.</summary>
/// <param name="Answers">The answers.</param>
public sealed record PortalAnswerList(IReadOnlyList<PortalAnswerView> Answers);

/// <summary>
/// A new answer (<c>POST /v1/portal/answers</c>). Which questions are
/// answerable, and what a value must hold, are the firm's questionnaire's and
/// the question's (<c>QuestionRules.ParseAnswer</c>).
/// </summary>
/// <param name="QuestionId">The question, in a section the firm shows.</param>
/// <param name="FilingRole">The debtor it is about; absent when the login answers for one.</param>
/// <param name="Value">The value, by input key.</param>
public sealed record PortalAnswerRequest(
    [property: Required(ErrorMessage = "Not a question in your questionnaire.")] string? QuestionId,
    [property: Required(ErrorMessage = "Must be an object.")] JsonObject? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClientRole? FilingRole = null);

/// <summary>A pending answer's new value (<c>PUT /v1/portal/answers/{answerId}</c>).</summary>
/// <param name="Value">The value, by input key.</param>
public sealed record PortalAnswerRevision([property: Required(ErrorMessage = "Must be an object.")] JsonObject? Value);
