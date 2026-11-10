using Insolvia.Api.Http;
using Insolvia.Domain.DocumentRequests;
using Insolvia.Domain.Documents;
using Insolvia.Domain.Questionnaires;

namespace Insolvia.Api.Firms;

/// <summary>
/// The firm's client-questionnaire config as staff read it (<c>questionnaire_json</c>):
/// every section, switched off or not, with the firm's instructions beside the default.
/// </summary>
/// <param name="IsDefault">True when the firm has saved nothing and reads the defaults.</param>
/// <param name="UpdatedAt">Last saved, ISO 8601 UTC to the microsecond; null while default.</param>
/// <param name="UpdatedBy">The subject who last saved it; null while default.</param>
/// <param name="Sections">Every section, in the questionnaire's order.</param>
public sealed record QuestionnaireView(bool IsDefault, string? UpdatedAt, string? UpdatedBy, IReadOnlyList<QuestionnaireSectionView> Sections)
{
    /// <summary>The view of <paramref name="config"/>.</summary>
    /// <param name="config">The stored config, or null for the defaults.</param>
    /// <returns>The view.</returns>
    public static QuestionnaireView Of(QuestionnaireConfig? config) => new(
        config is null,
        config is null ? null : FlaskJson.Micros(config.UpdatedAt),
        config?.UpdatedBy,
        [.. Questionnaire.Resolve(config).Select(QuestionnaireSectionView.Of)]);
}

/// <summary>One questionnaire section with the firm's choice applied.</summary>
/// <param name="Id">The section's id; ids are a contract and never renamed.</param>
/// <param name="Title">The section's title.</param>
/// <param name="Covers">What the section covers, for a person.</param>
/// <param name="Switchable">Whether the firm may switch it off; <c>personal_information</c> never.</param>
/// <param name="Enabled">Whether clients see it.</param>
/// <param name="Instructions">The firm's own instructions; null for the default.</param>
/// <param name="DefaultInstructions">The instructions shipped with the section.</param>
public sealed record QuestionnaireSectionView(string Id, string Title, string Covers, bool Switchable, bool Enabled, string? Instructions, string DefaultInstructions)
{
    /// <summary>The view of <paramref name="resolved"/>.</summary>
    /// <param name="resolved">The section with the firm's config applied.</param>
    /// <returns>The view.</returns>
    public static QuestionnaireSectionView Of(ResolvedSection resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return new(resolved.Section.Id, resolved.Section.Title, resolved.Section.Covers, resolved.Section.Switchable, resolved.Enabled, resolved.CustomInstructions, resolved.Section.DefaultInstructions);
    }
}

/// <summary>
/// The firm's questionnaire config as saved (<c>PUT /v1/firm/questionnaire</c>):
/// every section, each named once. Parsed by <c>Questionnaire.ParseUpdate</c>,
/// which owns validation; this record is its shape for the OpenAPI document.
/// </summary>
/// <param name="Sections">One entry for every section.</param>
public sealed record QuestionnaireRequest(IReadOnlyList<QuestionnaireSectionRequest> Sections);

/// <summary>The firm's choice for one questionnaire section.</summary>
/// <param name="Id">The section's id.</param>
/// <param name="Enabled">Whether clients see it; an unswitchable section must be true.</param>
/// <param name="Instructions">The firm's instructions; absent, or the default text, means the default.</param>
public sealed record QuestionnaireSectionRequest(string Id, bool Enabled, string? Instructions);

/// <summary>
/// The firm's document checklist (<c>checklist_json</c>): the list in force
/// and the shipped default beside it.
/// </summary>
/// <param name="IsDefault">True when the firm has saved nothing and uses the default.</param>
/// <param name="UpdatedAt">Last saved, ISO 8601 UTC to the microsecond; null while default.</param>
/// <param name="UpdatedBy">The subject who last saved it; null while default.</param>
/// <param name="Items">The checklist in force.</param>
/// <param name="DefaultItems">The checklist Insolvia ships.</param>
public sealed record ChecklistView(bool IsDefault, string? UpdatedAt, string? UpdatedBy, IReadOnlyList<ChecklistItemView> Items, IReadOnlyList<ChecklistItemView> DefaultItems)
{
    /// <summary>The view of <paramref name="checklist"/>.</summary>
    /// <param name="checklist">The stored checklist, or null for the default.</param>
    /// <returns>The view.</returns>
    public static ChecklistView Of(DocumentChecklist? checklist) => new(
        checklist is null,
        checklist is null ? null : FlaskJson.Micros(checklist.UpdatedAt),
        checklist?.UpdatedBy,
        [.. DocumentChecklists.Resolve(checklist).Select(ChecklistItemView.Of)],
        [.. DocumentChecklists.DefaultChecklist.Select(ChecklistItemView.Of)]);
}

/// <summary>One document a firm asks its clients for.</summary>
/// <param name="Title">What the document is called.</param>
/// <param name="Kind">The kind of document.</param>
/// <param name="Description">More about what to send; null when none.</param>
public sealed record ChecklistItemView(string Title, DocumentKind Kind, string? Description)
{
    /// <summary>The view of <paramref name="item"/>.</summary>
    /// <param name="item">The checklist entry.</param>
    /// <returns>The view.</returns>
    public static ChecklistItemView Of(ChecklistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new(item.Title, item.Kind, item.Description);
    }
}

/// <summary>
/// The firm's document checklist as saved (<c>PUT /v1/firm/document-checklist</c>).
/// Parsed by <c>DocumentChecklists.ParseUpdate</c>, which owns validation;
/// this record is its shape for the OpenAPI document.
/// </summary>
/// <param name="Items">The whole list, in order.</param>
public sealed record ChecklistRequest(IReadOnlyList<ChecklistItemView> Items);
