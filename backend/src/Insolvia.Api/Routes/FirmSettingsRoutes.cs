using Insolvia.Api.Auth;
using Insolvia.Api.Firms;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Firms;
using Insolvia.Core.Questionnaires;

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
        routes.MapPut("/v1/firm/questionnaire", SaveAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/firm/questionnaire", ResetAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
    }

    // A firm that never saved reads the defaults, isDefault: true.
    private static async Task<IResult> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        return FlaskJson.Result(FirmViews.Questionnaire(await context.FirmStore().GetQuestionnaireAsync(accessor.FirmId, cancellationToken)));
    }

    // Answers the staff view of what was SAVED, so the screen renders the
    // server's normalisation rather than what it sent.
    private static async Task<IResult> SaveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var settings = Questionnaire.ParseUpdate(await context.BodyAsync(MaxRequestBytes));
        var config = Questionnaire.BuildConfig(settings, accessor.FirmId, accessor.Subject, context.Clock());
        await context.FirmStore().PutQuestionnaireAsync(config, cancellationToken);
        context.Log(Logger, "questionnaire config saved");
        return FlaskJson.Result(FirmViews.Questionnaire(config));
    }

    // Reset deletes the stored config. Idempotent: the same 200 and body either way.
    private static async Task<IResult> ResetAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        if (await context.FirmStore().DeleteQuestionnaireAsync(accessor.FirmId, cancellationToken))
        {
            context.Log(Logger, "questionnaire config reset to defaults");
        }

        return FlaskJson.Result(FirmViews.Questionnaire(null));
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
        routes.MapPut("/v1/firm/document-checklist", SaveAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/firm/document-checklist", ResetAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
    }

    // The list in force and the shipped default beside it.
    private static async Task<IResult> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        return FlaskJson.Result(FirmViews.Checklist(await context.FirmStore().GetDocumentChecklistAsync(accessor.FirmId, cancellationToken)));
    }

    private static async Task<IResult> SaveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var items = DocumentChecklists.ParseUpdate(await context.BodyAsync(MaxRequestBytes));
        var checklist = DocumentChecklists.Build(items, accessor.FirmId, accessor.Subject, context.Clock());
        await context.FirmStore().PutDocumentChecklistAsync(checklist, cancellationToken);
        context.Log(Logger, "document checklist saved", ("items", items.Count));
        return FlaskJson.Result(FirmViews.Checklist(checklist));
    }

    // Back to the shipped default by deleting the stored list. Idempotent.
    private static async Task<IResult> ResetAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        if (await context.FirmStore().DeleteDocumentChecklistAsync(accessor.FirmId, cancellationToken))
        {
            context.Log(Logger, "document checklist reset to the default");
        }

        return FlaskJson.Result(FirmViews.Checklist(null));
    }
}
