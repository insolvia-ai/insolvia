using System.Text.Json.Serialization;
using Insolvia.Domain.Questionnaires;
using Insolvia.Domain.Questions;

namespace Insolvia.Api.Portal;

/// <summary>
/// The questionnaire as a client's firm configured it: the enabled sections
/// only, each with the instructions in force and its live questions in order.
/// Nothing about what is switched off, nothing staff-facing.
/// </summary>
/// <param name="Sections">The enabled sections, in order.</param>
public sealed record PortalQuestionnaireView(IReadOnlyList<PortalSectionView> Sections)
{
    /// <summary>The view of <paramref name="config"/>.</summary>
    /// <param name="config">The firm's questionnaire, or null for the defaults.</param>
    /// <returns>The view.</returns>
    public static PortalQuestionnaireView Of(QuestionnaireConfig? config) => new(
        [.. Questionnaire.ClientSections(config).Select(resolved => new PortalSectionView(
            resolved.Section.Id,
            resolved.Section.Title,
            resolved.Instructions,
            [.. QuestionRules.For(resolved.Section.Id).Select(PortalQuestionView.Of)]))]);
}

/// <summary>One questionnaire section.</summary>
/// <param name="Id">The section's id — stable; answers hang off it.</param>
/// <param name="Title">Its title.</param>
/// <param name="Instructions">The instructions in force: the firm's own, else the default.</param>
/// <param name="Questions">Its live questions, in order.</param>
public sealed record PortalSectionView(string Id, string Title, string Instructions, IReadOnlyList<PortalQuestionView> Questions);

/// <summary>One question as the portal renders it.</summary>
/// <param name="Id">Stable, <c>{section}.{name}</c>.</param>
/// <param name="Text">What the client reads.</param>
/// <param name="Repeats">Whether a client may give many answers (one per row).</param>
/// <param name="PerDebtor">Whether the answer is about the answering debtor.</param>
/// <param name="Inputs">The boxes, in order.</param>
/// <param name="Help">Help text under the question; absent when none.</param>
public sealed record PortalQuestionView(
    string Id,
    string Text,
    bool Repeats,
    bool PerDebtor,
    IReadOnlyList<PortalInputView> Inputs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Help)
{
    /// <summary>The view of <paramref name="entry"/>.</summary>
    /// <param name="entry">The question.</param>
    /// <returns>The view.</returns>
    public static PortalQuestionView Of(Question entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(
            entry.Id,
            entry.Text,
            entry.Repeats,
            entry.PerDebtor,
            [.. entry.Inputs.Select(input => new PortalInputView(input.Key, input.Label, input.Type, input.Required, string.IsNullOrEmpty(input.Help) ? null : input.Help))],
            entry.Help);
    }
}

/// <summary>One box on the portal screen.</summary>
/// <param name="Key">The key of this box's value in the answer.</param>
/// <param name="Label">Its label.</param>
/// <param name="Type">What kind of box it is.</param>
/// <param name="Required">Whether an answer must fill it.</param>
/// <param name="Help">Help text under the box; absent when none.</param>
public sealed record PortalInputView(
    string Key,
    string Label,
    AnswerInputType Type,
    bool Required,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Help);
