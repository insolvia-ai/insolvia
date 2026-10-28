using System.Text.Json.Nodes;
using Insolvia.Api.Cases;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.Candidates;
using Insolvia.Core.Clients;
using Insolvia.Core.Questionnaires;
using Insolvia.Core.Questions;

namespace Insolvia.Api.Portal;

/// <summary>
/// The portal's JSON representations — insolvia_core's <c>binding_json</c>
/// (the FIRM's view of an invited client), <c>question_json</c>,
/// <c>portal_sections_json</c> and <c>answer_json</c> (the CLIENT's), and the
/// route's <c>portal_me_json</c>. Key order is <see cref="FlaskJson"/>'s.
/// </summary>
/// <remarks>
/// Here rather than in Core for the reason <see cref="CaseViews"/> gives.
/// </remarks>
public static class PortalViews
{
    /// <summary>
    /// <c>binding_json</c>: what a FIRM USER sees of a client on their case —
    /// enough to manage the invitation. Never shown to the client.
    /// </summary>
    /// <param name="binding">The binding.</param>
    /// <returns>The body.</returns>
    public static JsonObject Binding(ClientBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new JsonObject
        {
            ["subject"] = binding.Subject,
            ["email"] = binding.Email,
            ["displayName"] = binding.DisplayName,
            ["roles"] = Roles(binding.Roles),
            ["status"] = binding.Status.ToWire(),
            ["invitedBy"] = binding.InvitedBy,
            ["createdAt"] = FlaskJson.Micros(binding.CreatedAt),
            ["updatedAt"] = FlaskJson.Micros(binding.UpdatedAt),
        };
    }

    /// <summary>
    /// <c>portal_me_json</c>: the name the FIRM gave at invitation, the firm's
    /// name alone, and the case's public status — deliberately no case id.
    /// </summary>
    /// <param name="client">The resolved client.</param>
    /// <param name="status">The bound case's chapter and stage.</param>
    /// <returns>The body.</returns>
    public static JsonObject Me(ClientAccessor client, CasePublicStatus status)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new JsonObject
        {
            ["subject"] = client.Subject,
            ["displayName"] = client.Binding.DisplayName,
            ["roles"] = Roles(client.Roles),
            ["firm"] = new JsonObject { ["name"] = client.Firm.Name },
            ["case"] = CaseViews.PublicStatus(status),
        };
    }

    /// <summary>
    /// <c>portal_sections_json</c>: the enabled sections only, each with the
    /// instructions in force and its live questions in order. Nothing about
    /// what is switched off, nothing staff-facing, nothing about who saved it.
    /// </summary>
    /// <param name="config">The firm's questionnaire, or null for the defaults.</param>
    /// <returns>The body.</returns>
    public static JsonObject Sections(QuestionnaireConfig? config) => new()
    {
        ["sections"] = new JsonArray([.. Questionnaire.ClientSections(config).Select(resolved => (JsonNode)new JsonObject
        {
            ["id"] = resolved.Section.Id,
            ["title"] = resolved.Section.Title,
            ["instructions"] = resolved.Instructions,
            ["questions"] = new JsonArray([.. QuestionRules.For(resolved.Section.Id).Select(entry => (JsonNode)Question(entry))]),
        })]),
    };

    /// <summary><c>question_json</c>: one question as the portal renders it; <c>help</c> absent when unset.</summary>
    /// <param name="entry">The question.</param>
    /// <returns>The body.</returns>
    public static JsonObject Question(Question entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var body = new JsonObject
        {
            ["id"] = entry.Id,
            ["text"] = entry.Text,
            ["repeats"] = entry.Repeats,
            ["perDebtor"] = entry.PerDebtor,
            ["inputs"] = new JsonArray([.. entry.Inputs.Select(input =>
            {
                var shaped = new JsonObject
                {
                    ["key"] = input.Key,
                    ["label"] = input.Label,
                    ["type"] = input.Type.ToWire(),
                    ["required"] = input.Required,
                };

                // Python's `**({"help": …} if answer_input.help else {})`: an empty help is absent too.
                if (!string.IsNullOrEmpty(input.Help))
                {
                    shaped["help"] = input.Help;
                }

                return (JsonNode)shaped;
            })]),
        };
        if (entry.Help is not null)
        {
            body["help"] = entry.Help;
        }

        return body;
    }

    /// <summary>
    /// <c>answer_json</c>: a client's own answer — the value THEY gave and
    /// where it stands. No corrected payload, no reviewer, no resulting record.
    /// </summary>
    /// <param name="candidate">The answer's candidate.</param>
    /// <param name="answer">Its question and role.</param>
    /// <returns>The body.</returns>
    public static JsonObject Answer(Candidate candidate, AnswerRef answer)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(answer);
        return new JsonObject
        {
            ["id"] = candidate.Id,
            ["questionId"] = answer.Question.Id,
            ["sectionId"] = answer.Question.Section,
            ["filingRole"] = answer.FilingRole,
            ["value"] = QuestionRules.AnswerValue(answer.Question, candidate.Payload),
            ["status"] = candidate.Status.ToWire(),
            ["createdAt"] = FlaskJson.Micros(candidate.CreatedAt),
            ["updatedAt"] = FlaskJson.Micros(candidate.UpdatedAt),
        };
    }

    private static JsonArray Roles(IEnumerable<ClientRole> roles) =>
        new([.. roles.Select(role => (JsonNode)JsonValue.Create(role.ToWire()))]);
}
