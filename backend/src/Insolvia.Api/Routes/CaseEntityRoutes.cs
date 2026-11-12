using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Domain;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.Assets;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Claims;
using Insolvia.Domain.Codebtors;
using Insolvia.Domain.ContractLeases;
using Insolvia.Domain.Creditors;
using Insolvia.Domain.Exemptions;
using Insolvia.Domain.Expenses;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Income;
using Insolvia.Domain.MeansTest;
using Insolvia.Domain.Notes;
using Insolvia.Domain.Petitions;
using Insolvia.Domain.Plans;
using Insolvia.Domain.Ports;
using Insolvia.Domain.Provenance;
using Insolvia.Domain.Sofa;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Insolvia.Api.Routes;

/// <summary>
/// The case collections (routes/case_entities.py; issue #249): five routes
/// for each kind in <see cref="CaseCollections"/>, typed by its body record
/// (<c>/v1/cases/{caseId}/creditors</c> takes <c>EntityRequest&lt;CreditorBody&gt;</c>
/// and answers <c>EntityView&lt;CreditorBody&gt;</c>), gated by <c>intake</c>.
/// A collection that does not exist is a route that does not exist.
/// <c>amended</c> may be set only once the case is filed (409), and a PUT
/// that omits it keeps the stored value.
/// </summary>
public sealed class CaseEntityRoutes : IEndpointModule
{
    private const int MaxRequestBytes = 256 * 1024;
    private const string Logger = "insolvia_api.api.routes.case_entities";
    private const string RecordNotFound = "record not found";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        MapCollection(routes, CreditorRules.Kind);
        MapCollection(routes, ClaimRules.Kind);
        MapCollection(routes, AssetRules.Kind);
        MapCollection(routes, IncomeRules.Employment);
        MapCollection(routes, IncomeRules.PayPeriodRecord);
        MapCollection(routes, IncomeRules.OtherIncomeRecord);
        MapCollection(routes, IncomeRules.IncomeSummary);
        MapCollection(routes, ExpenseRules.Household);
        MapCollection(routes, ExpenseRules.Expense);
        MapCollection(routes, ExpenseRules.Dependent);
        MapCollection(routes, CodebtorRules.Kind);
        MapCollection(routes, SofaRules.Kind);
        MapCollection(routes, PetitionRules.Petition);
        MapCollection(routes, PetitionRules.PriorCase);
        MapCollection(routes, PetitionRules.RelatedCase);
        MapCollection(routes, PetitionRules.SoleProprietorship);
        MapCollection(routes, PetitionRules.FilingProfessional);
        MapCollection(routes, ExemptionRules.Kind);
        MapCollection(routes, ContractLeaseRules.Kind);
        MapCollection(routes, CodebtorRules.CommunityHouseholdMember);
        MapCollection(routes, MeansTestInputRules.Kind);
        MapCollection(routes, PlanRules.Kind);
    }

    private static void MapCollection<TBody>(IEndpointRouteBuilder routes, EntityKind<TBody> kind)
        where TBody : class
    {
        var records = $"/v1/cases/{{caseId}}/{kind.Collection}";
        var record = $"{records}/{{entityId}}";
        var noun = kind.Name.Replace('_', ' ');
        var sizeLimit = new RequestSizeLimitAttribute(MaxRequestBytes);
        routes.MapPost(records, (string caseId, EntityRequest<TBody> request, HttpContext context, CancellationToken cancellationToken) => CreateAsync(context, caseId, kind, request, cancellationToken))
            .WithSummary($"Create a {noun}").WithDescription("`amended` before the case is filed is a 409.").WithTags(nameof(CaseEntityRoutes))
            .WithMetadata(sizeLimit).RequireAuth().Requires(Feature.Intake, PermissionLevel.AddEdit);
        routes.MapGet(records, (HttpContext context, string caseId, CancellationToken cancellationToken) => ListAsync(context, caseId, kind, cancellationToken))
            .WithSummary($"The case's {kind.Collection.Replace('_', ' ')}, oldest first").WithDescription($"The one key is `{kind.Collection}`.").WithTags(nameof(CaseEntityRoutes))
            .RequireAuth().Requires(Feature.Intake, PermissionLevel.ViewOnly);
        routes.MapGet(record, (HttpContext context, string caseId, string entityId, CancellationToken cancellationToken) => GetAsync(context, caseId, kind, entityId, cancellationToken))
            .WithSummary($"One {noun}").WithTags(nameof(CaseEntityRoutes))
            .RequireAuth().Requires(Feature.Intake, PermissionLevel.ViewOnly);
        routes.MapPut(record, (string caseId, string entityId, EntityRequest<TBody> request, HttpContext context, CancellationToken cancellationToken) => PutAsync(context, caseId, kind, entityId, request, cancellationToken))
            .WithSummary($"Replace a {noun}, whole").WithDescription("The id and `createdAt` are kept; so is `amended` when the request omits it.").WithTags(nameof(CaseEntityRoutes))
            .WithMetadata(sizeLimit).RequireAuth().Requires(Feature.Intake, PermissionLevel.AddEdit);
        routes.MapDelete(record, (HttpContext context, string caseId, string entityId, CancellationToken cancellationToken) => DeleteAsync(context, caseId, kind, entityId, cancellationToken))
            .WithSummary($"Delete a {noun}").WithTags(nameof(CaseEntityRoutes))
            .RequireAuth().Requires(Feature.Intake, PermissionLevel.AddEdit);
    }

    private static async Task<Created<EntityView<TBody>>> CreateAsync<TBody>(HttpContext context, string caseId, EntityKind<TBody> kind, EntityRequest<TBody> request, CancellationToken cancellationToken)
        where TBody : class
    {
        var provenance = ProvenanceRules.Check(request.Provenance);
        CaseEntityRules.RequireProvenance(request.Body, provenance, stored: null);
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var amended = request.Amended == true;
        RefuseAmendedBeforeFiled(target, amended);

        var entity = CaseEntityRules.Create(kind, caseId, request.Body, provenance, context.Clock()) with { Amended = amended };
        await context.Port<ICaseEntityStore>().CreateAsync(entity, cancellationToken);
        LogSaved(context, caseId, kind, entity.Id);
        return TypedResults.Created((string?)null, EntityViews.Of(entity));
    }

    private static async Task<Dictionary<string, IReadOnlyList<EntityView<TBody>>>> ListAsync<TBody>(HttpContext context, string caseId, EntityKind<TBody> kind, CancellationToken cancellationToken)
        where TBody : class
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var entities = await context.Port<ICaseEntityStore>().ListTypedAsync(caseId, kind, cancellationToken);
        return new(StringComparer.Ordinal) { [kind.Collection] = [.. entities.Select(EntityViews.Of)] };
    }

    private static async Task<EntityView<TBody>> GetAsync<TBody>(HttpContext context, string caseId, EntityKind<TBody> kind, string entityId, CancellationToken cancellationToken)
        where TBody : class
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        return EntityViews.Of(await EntityOr404Async(context, caseId, kind, entityId, cancellationToken));
    }

    private static async Task<EntityView<TBody>> PutAsync<TBody>(HttpContext context, string caseId, EntityKind<TBody> kind, string entityId, EntityRequest<TBody> request, CancellationToken cancellationToken)
        where TBody : class
    {
        var provenance = ProvenanceRules.Check(request.Provenance);
        var body = BodyJson.ToNode(request.Body);
        ProvenanceRules.RequireProvenance(body, provenance);
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        RefuseAmendedBeforeFiled(target, request.Amended == true);
        var stored = await EntityOr404Async(context, caseId, kind, entityId, cancellationToken);

        // A staff save may KEEP a client_answered entry and never mint one.
        ProvenanceRules.RequireServerSourcesKept(body, provenance, stored.BodyNode(), stored.Provenance);
        var entity = stored with { UpdatedAt = Clock.Now(context.Clock()), Body = request.Body, Provenance = provenance, Amended = request.Amended ?? stored.Amended };
        if (!await context.Port<ICaseEntityStore>().PutAsync(entity, cancellationToken))
        {
            throw new NotFoundException(RecordNotFound);
        }

        LogSaved(context, caseId, kind, entity.Id);
        return EntityViews.Of(entity);
    }

    private static async Task<NoContent> DeleteAsync<TBody>(HttpContext context, string caseId, EntityKind<TBody> kind, string entityId, CancellationToken cancellationToken)
        where TBody : class
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        if (!await context.Port<ICaseEntityStore>().DeleteAsync(caseId, kind, entityId, cancellationToken))
        {
            throw new NotFoundException(RecordNotFound);
        }

        context.Log(Logger, "case entity deleted", ("case_id", caseId), ("collection", kind.Collection), ("id", entityId));
        return TypedResults.NoContent();
    }

    private static async Task<CaseEntity<TBody>> EntityOr404Async<TBody>(HttpContext context, string caseId, EntityKind<TBody> kind, string entityId, CancellationToken cancellationToken)
        where TBody : class =>
        await context.Port<ICaseEntityStore>().GetTypedAsync(caseId, kind, entityId, cancellationToken) ?? throw new NotFoundException(RecordNotFound);

    private static void RefuseAmendedBeforeFiled(Case target, bool amended)
    {
        if (amended && !CaseRules.IsFiled(target.Status))
        {
            throw new ConflictException($"amended can only be set once the case is filed — this case's status is '{target.Status.ToWire()}'.");
        }
    }

    private static void LogSaved(HttpContext context, string caseId, IEntityKind kind, string entityId) =>
        context.Log(Logger, "case entity saved", ("case_id", caseId), ("collection", kind.Collection), ("id", entityId));
}

/// <summary>
/// Case notes (routes/notes.py; issue 14.5 / #357), gated by <c>notes</c>: the
/// author is stamped by the server from the caller (a request's author fields
/// are never trusted), only the author or a firm admin may change or remove
/// one (403 otherwise), and the list is newest first.
/// </summary>
public sealed class NoteRoutes : IEndpointModule
{
    private const int MaxRequestBytes = 8 * 1024;
    private const string Logger = "insolvia_api.api.routes.notes";
    private const string NoteNotFound = "note not found";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var sizeLimit = new RequestSizeLimitAttribute(MaxRequestBytes);
        routes.MapPost("/v1/cases/{caseId}/notes", CreateAsync).WithMetadata(sizeLimit).RequireAuth().Requires(Feature.Notes, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/notes", ListAsync).RequireAuth().Requires(Feature.Notes, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/cases/{caseId}/notes/{noteId}", PutAsync).WithMetadata(sizeLimit).RequireAuth().Requires(Feature.Notes, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/notes/{noteId}", DeleteAsync).RequireAuth().Requires(Feature.Notes, PermissionLevel.AddEdit);
    }

    /// <summary>Write a note on a case, authored by the caller.</summary>
    internal static async Task<Created<NoteView>> CreateAsync(HttpContext context, string caseId, NoteRequest request, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var stamped = NoteRules.StampAuthor(new NoteBody(request.Text, request.FormSeries), accessor.Subject, accessor.User.FullName);
        var note = CaseEntityRules.Create(NoteRules.Kind, caseId, stamped, ValueCollections.EmptyDictionary<string, ProvenanceEntry>(), context.Clock());
        await context.Port<ICaseEntityStore>().CreateAsync(note, cancellationToken);
        LogSaved(context, caseId, note.Id);
        return TypedResults.Created((string?)null, NoteView.Of(note));
    }

    /// <summary>A case's notes, newest first.</summary>
    internal static async Task<NoteList> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var notes = await context.Port<ICaseEntityStore>().ListTypedAsync(caseId, NoteRules.Kind, cancellationToken);
        return new NoteList([.. notes.Reverse().Select(NoteView.Of)]);
    }

    /// <summary>Edit a note: its author or a firm admin only.</summary>
    /// <remarks>The original author stays the author when an admin edits.</remarks>
    internal static async Task<NoteView> PutAsync(HttpContext context, string caseId, string noteId, NoteRequest request, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var stored = await NoteOr404Async(context, caseId, noteId, cancellationToken);
        RequireOwnerOrAdmin(context, accessor.Subject, accessor.User.IsAdmin, stored);
        var restamped = NoteRules.StampAuthor(new NoteBody(request.Text, request.FormSeries), stored.Body.AuthorSubject ?? accessor.Subject, stored.Body.AuthorName ?? accessor.User.FullName);
        var note = stored with { Body = restamped, UpdatedAt = Clock.Now(context.Clock()) };
        if (!await context.Port<ICaseEntityStore>().PutAsync(note, cancellationToken))
        {
            throw new NotFoundException(NoteNotFound);
        }

        LogSaved(context, caseId, note.Id);
        return NoteView.Of(note);
    }

    /// <summary>Delete a note: its author or a firm admin only.</summary>
    internal static async Task<NoContent> DeleteAsync(HttpContext context, string caseId, string noteId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var stored = await NoteOr404Async(context, caseId, noteId, cancellationToken);
        RequireOwnerOrAdmin(context, accessor.Subject, accessor.User.IsAdmin, stored);
        if (!await context.Port<ICaseEntityStore>().DeleteAsync(caseId, NoteRules.Kind, noteId, cancellationToken))
        {
            throw new NotFoundException(NoteNotFound);
        }

        context.Log(Logger, "note deleted", ("case_id", caseId), ("collection", NoteRules.Kind.Collection), ("id", noteId));
        return TypedResults.NoContent();
    }

    private static async Task<CaseEntity<NoteBody>> NoteOr404Async(HttpContext context, string caseId, string noteId, CancellationToken cancellationToken) =>
        await context.Port<ICaseEntityStore>().GetTypedAsync(caseId, NoteRules.Kind, noteId, cancellationToken) ?? throw new NotFoundException(NoteNotFound);

    private static void RequireOwnerOrAdmin(HttpContext context, string subject, bool isAdmin, CaseEntity<NoteBody> note)
    {
        if (!NoteRules.MayEdit(note, subject, isAdmin))
        {
            context.Log(Logger, "note edit refused", ("reason", "not_author_or_admin"));
            throw new ForbiddenException("only the author or a firm admin may change this note");
        }
    }

    private static void LogSaved(HttpContext context, string caseId, string noteId) =>
        context.Log(Logger, "note saved", ("case_id", caseId), ("collection", NoteRules.Kind.Collection), ("id", noteId));
}
