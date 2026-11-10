using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Domain;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Notes;
using Insolvia.Domain.Ports;
using Insolvia.Domain.Provenance;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Insolvia.Api.Routes;

/// <summary>
/// The generic case collections (routes/case_entities.py; issue #249): one
/// set of five routes for every kind in <see cref="CaseCollections"/>, gated
/// by <c>intake</c>. An unknown collection is a 404. <c>amended</c> may be set
/// only once the case is filed (409), and a PUT that omits it keeps the stored
/// value. The literal routes beside these (<c>debtors</c>, <c>notes</c>,
/// <c>documents</c>, …) win by ASP.NET's route precedence, as Flask's did.
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
        routes.MapPost("/v1/cases/{caseId}/{collection}", CreateAsync).RequestBody<EntityRequest>().RequireAuth().Requires(Feature.Intake, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/{collection}", ListAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/{collection}/{entityId}", GetAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/cases/{caseId}/{collection}/{entityId}", PutAsync).RequestBody<EntityRequest>().RequireAuth().Requires(Feature.Intake, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/{collection}/{entityId}", DeleteAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.AddEdit);
    }

    /// <summary>Create a record in a case collection.</summary>
    /// <remarks>An unknown collection is a 404; <c>amended</c> before the case is filed is a 409.</remarks>
    internal static async Task<Created<EntityView>> CreateAsync(HttpContext context, string caseId, string collection, CancellationToken cancellationToken)
    {
        var kind = KindOf(collection);
        var draft = kind.ParseDraft(await context.BodyAsync(MaxRequestBytes));
        RequireServerSourcesKept(draft, null);
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        RefuseAmendedBeforeFiled(target, draft);

        var now = Clock.Now(context.Clock());
        var entity = kind.Restore(Guid.NewGuid().ToString(), caseId, now, now, draft);
        await context.Port<ICaseEntityStore>().CreateAsync(entity, cancellationToken);
        LogSaved(context, caseId, kind, entity.Id);
        return TypedResults.Created((string?)null, EntityView.Of(entity));
    }

    /// <summary>A case collection's records, oldest first.</summary>
    /// <remarks>The one key is the collection's name, as in the URL (<c>{"creditors": [...]}</c>).</remarks>
    internal static async Task<Dictionary<string, IReadOnlyList<EntityView>>> ListAsync(HttpContext context, string caseId, string collection, CancellationToken cancellationToken)
    {
        var kind = KindOf(collection);
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var entities = await context.Port<ICaseEntityStore>().ListForCaseAsync(caseId, kind, cancellationToken);
        return new(StringComparer.Ordinal) { [kind.Collection] = [.. entities.Select(EntityView.Of)] };
    }

    /// <summary>One record of a case collection.</summary>
    internal static async Task<EntityView> GetAsync(HttpContext context, string caseId, string collection, string entityId, CancellationToken cancellationToken)
    {
        var kind = KindOf(collection);
        await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        return EntityView.Of(await EntityOr404Async(context, caseId, kind, entityId, cancellationToken));
    }

    /// <summary>Replace a record of a case collection, whole.</summary>
    /// <remarks>The id and <c>created_at</c> are kept; so is <c>amended</c> when the request omits it.</remarks>
    internal static async Task<EntityView> PutAsync(HttpContext context, string caseId, string collection, string entityId, CancellationToken cancellationToken)
    {
        var kind = KindOf(collection);
        var payload = await context.BodyAsync(MaxRequestBytes);
        var draft = kind.ParseDraft(payload);
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        RefuseAmendedBeforeFiled(target, draft);
        var stored = await EntityOr404Async(context, caseId, kind, entityId, cancellationToken);
        RequireServerSourcesKept(draft, stored);
        if (!payload.ContainsKey("amended"))
        {
            // Python's replace(draft, amended=stored.amended): the store's
            // kinds rebuild only their own typed draft, so parse it again
            // with the stored flag in place.
            payload["amended"] = stored.Amended;
            draft = kind.ParseDraft(payload);
        }

        var entity = kind.Restore(stored.Id, stored.CaseId, stored.CreatedAt, Clock.Now(context.Clock()), draft);
        if (!await context.Port<ICaseEntityStore>().PutAsync(entity, cancellationToken))
        {
            throw new NotFoundException(RecordNotFound);
        }

        LogSaved(context, caseId, kind, entity.Id);
        return EntityView.Of(entity);
    }

    /// <summary>Delete a record of a case collection.</summary>
    internal static async Task<NoContent> DeleteAsync(HttpContext context, string caseId, string collection, string entityId, CancellationToken cancellationToken)
    {
        var kind = KindOf(collection);
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        if (!await context.Port<ICaseEntityStore>().DeleteAsync(caseId, kind, entityId, cancellationToken))
        {
            throw new NotFoundException(RecordNotFound);
        }

        context.Log(Logger, "case entity deleted", ("case_id", caseId), ("collection", kind.Collection), ("id", entityId));
        return TypedResults.NoContent();
    }

    private static IEntityKind KindOf(string collection) =>
        CaseCollections.Find(collection) ?? throw new NotFoundException("no such collection");

    private static async Task<ICaseEntity> EntityOr404Async(HttpContext context, string caseId, IEntityKind kind, string entityId, CancellationToken cancellationToken) =>
        await context.Port<ICaseEntityStore>().GetAsync(caseId, kind, entityId, cancellationToken) ?? throw new NotFoundException(RecordNotFound);

    // A staff save may KEEP a client_answered entry and never mint one.
    private static void RequireServerSourcesKept(IEntityDraft draft, ICaseEntity? stored) =>
        ProvenanceRules.RequireServerSourcesKept(draft.BodyNode(), draft.Provenance, stored?.BodyNode(), stored?.Provenance);

    private static void RefuseAmendedBeforeFiled(Case target, IEntityDraft draft)
    {
        if (draft.Amended && !CaseRules.IsFiled(target.Status))
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
        routes.MapPost("/v1/cases/{caseId}/notes", CreateAsync).RequestBody<NoteRequest>().RequireAuth().Requires(Feature.Notes, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/notes", ListAsync).RequireAuth().Requires(Feature.Notes, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/cases/{caseId}/notes/{noteId}", PutAsync).RequestBody<NoteRequest>().RequireAuth().Requires(Feature.Notes, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/notes/{noteId}", DeleteAsync).RequireAuth().Requires(Feature.Notes, PermissionLevel.AddEdit);
    }

    /// <summary>Write a note on a case, authored by the caller.</summary>
    internal static async Task<Created<NoteView>> CreateAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var body = NoteRules.Kind.ParseBody(await context.BodyAsync(MaxRequestBytes));
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var stamped = NoteRules.StampAuthor(body, accessor.Subject, accessor.User.FullName);
        var note = CaseEntityRules.Create(NoteRules.Kind, new EntityDraft<NoteBody>(stamped, ValueCollections.EmptyDictionary<string, ProvenanceEntry>()), caseId, context.Clock());
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
    internal static async Task<NoteView> PutAsync(HttpContext context, string caseId, string noteId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var body = NoteRules.Kind.ParseBody(await context.BodyAsync(MaxRequestBytes));
        await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var stored = await NoteOr404Async(context, caseId, noteId, cancellationToken);
        RequireOwnerOrAdmin(context, accessor.Subject, accessor.User.IsAdmin, stored);
        var restamped = NoteRules.StampAuthor(body, stored.Body.AuthorSubject ?? accessor.Subject, stored.Body.AuthorName ?? accessor.User.FullName);
        var note = CaseEntityRules.Replace(stored, new EntityDraft<NoteBody>(restamped, ValueCollections.EmptyDictionary<string, ProvenanceEntry>()), context.Clock());
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
