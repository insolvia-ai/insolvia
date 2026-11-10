using Insolvia.Api.Auth;
using Insolvia.Api.Firms;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Domain.DocumentRequests;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Questionnaires;

namespace Insolvia.Api.Routes;

/// <summary>
/// The firm's client-questionnaire config (ADR 0023 PR 3 / #362; services/api
/// routes/firm_questionnaire.py): read, save, reset. STAFF routes, gated as
/// firm settings are — <c>firm_administration</c> view_only to read, add_edit
/// to save or reset. The staff view is every section, switched off or not.
/// No access-log row: the log is per case, and this is a firm setting.
/// </summary>
public sealed class FirmQuestionnaireRoutes : IEndpointModule
{
    // Six sections, each a flag and up to 2,000 characters, four bytes a character.
    private const int MaxRequestBytes = 64 * 1024;
    private const string Logger = "insolvia_api.api.routes.firm_questionnaire";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/firm/questionnaire", ReadAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/firm/questionnaire", SaveAsync).RequestBody<QuestionnaireRequest>().RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/firm/questionnaire", ResetAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
    }

    /// <summary>The firm's questionnaire config: every section, switched off or not.</summary>
    /// <remarks>A firm that never saved reads the defaults, <c>isDefault: true</c>.</remarks>
    internal static async Task<QuestionnaireView> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        return QuestionnaireView.Of(await context.FirmStore().GetQuestionnaireAsync(accessor.FirmId, cancellationToken));
    }

    /// <summary>Save the firm's questionnaire config.</summary>
    /// <remarks>Answers what was saved, so the screen renders the server's normalisation rather than what it sent.</remarks>
    internal static async Task<QuestionnaireView> SaveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var settings = Questionnaire.ParseUpdate(await context.BodyAsync(MaxRequestBytes));
        var config = Questionnaire.BuildConfig(settings, accessor.FirmId, accessor.Subject, context.Clock());
        await context.FirmStore().PutQuestionnaireAsync(config, cancellationToken);
        context.Log(Logger, "questionnaire config saved");
        return QuestionnaireView.Of(config);
    }

    /// <summary>Reset the firm's questionnaire config to the defaults.</summary>
    /// <remarks>Idempotent: the same 200 and body whether or not anything was stored.</remarks>
    internal static async Task<QuestionnaireView> ResetAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        if (await context.FirmStore().DeleteQuestionnaireAsync(accessor.FirmId, cancellationToken))
        {
            context.Log(Logger, "questionnaire config reset to defaults");
        }

        return QuestionnaireView.Of(null);
    }
}

/// <summary>
/// The firm's document checklist (ADR 0023 PR 5 / #364; the firm half of
/// services/api routes/document_requests.py): read, save, reset — gated
/// exactly as the questionnaire is. A case's requests (the other half of
/// that module) are case work and arrive with the case routes (18.15).
/// </summary>
public sealed class DocumentChecklistRoutes : IEndpointModule
{
    // Forty entries of a 120-character title and a 1,000-character description.
    private const int MaxRequestBytes = 256 * 1024;
    private const string Logger = "insolvia_api.api.routes.document_requests";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/firm/document-checklist", ReadAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/firm/document-checklist", SaveAsync).RequestBody<ChecklistRequest>().RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/firm/document-checklist", ResetAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
    }

    /// <summary>The firm's document checklist, with the shipped default beside it.</summary>
    internal static async Task<ChecklistView> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        return ChecklistView.Of(await context.FirmStore().GetDocumentChecklistAsync(accessor.FirmId, cancellationToken));
    }

    /// <summary>Save the firm's document checklist, whole.</summary>
    internal static async Task<ChecklistView> SaveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var items = DocumentChecklists.ParseUpdate(await context.BodyAsync(MaxRequestBytes));
        var checklist = DocumentChecklists.Build(items, accessor.FirmId, accessor.Subject, context.Clock());
        await context.FirmStore().PutDocumentChecklistAsync(checklist, cancellationToken);
        context.Log(Logger, "document checklist saved", ("items", items.Count));
        return ChecklistView.Of(checklist);
    }

    /// <summary>Reset the firm's document checklist to the shipped default.</summary>
    /// <remarks>Idempotent: the same 200 and body whether or not anything was stored.</remarks>
    internal static async Task<ChecklistView> ResetAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        if (await context.FirmStore().DeleteDocumentChecklistAsync(accessor.FirmId, cancellationToken))
        {
            context.Log(Logger, "document checklist reset to the default");
        }

        return ChecklistView.Of(null);
    }
}
