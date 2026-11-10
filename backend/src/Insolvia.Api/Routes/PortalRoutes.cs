using Insolvia.Api.Auth;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Api.Portal;
using Insolvia.Domain;
using Insolvia.Domain.Access;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.Candidates;
using Insolvia.Domain.Clients;
using Insolvia.Domain.Ports;
using Insolvia.Domain.Questionnaires;
using Insolvia.Domain.Questions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Insolvia.Api.Routes;

/// <summary>
/// The client portal's own routes (ADR 0023; routes/portal.py) — what a
/// CLIENT reaches.
/// </summary>
/// <remarks>
/// <para><b>Every route here carries <see cref="ClientAuth.RequireClient{TBuilder}"/>
/// and nothing else authenticates them</b>; the route-table tests walk the
/// whole table to hold that from both sides.</para>
/// <para><b>No route here names a case.</b> The case is the binding's, from
/// the verified token — nothing for a client to guess, nothing to probe.</para>
/// <para><b>Reads go through projections that take the
/// <see cref="ClientAccessor"/></b> (<see cref="ICaseStore.PublicStatusAsync"/>),
/// never <see cref="ICaseStore.GetAsync"/>: a client reads the firm's name, the
/// questionnaire, their own candidates and their document requests, and never
/// the confirmed case record.</para>
/// <para><b>An answer is a CANDIDATE</b> in the case's one review queue, its
/// origin the verified token's. Nothing here writes or reads a case record:
/// the only way an answer reaches case data is a person accepting it.</para>
/// </remarks>
public sealed class PortalRoutes : IEndpointModule
{
    /// <summary>The refusal for a binding whose case is gone — the same as no binding at all.</summary>
    internal const string NoCase = "you do not have access to a case through the client portal";

    // An answer is a handful of short fields; a bigger body is a mistake.
    private const int MaxAnswerBytes = 16 * 1024;
    private const string Logger = "insolvia_api.api.routes.portal";
    private const string Reviewed = "this answer has already been reviewed";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/portal/me", MeAsync).RequireClient();
        routes.MapGet("/v1/portal/questionnaire", QuestionnaireAsync).RequireClient();
        routes.MapGet("/v1/portal/answers", ListAnswersAsync).RequireClient();
        routes.MapPost("/v1/portal/answers", CreateAnswerAsync).RequestBody<PortalAnswerRequest>().RequireClient();
        routes.MapPut("/v1/portal/answers/{answerId}", UpdateAnswerAsync).RequestBody<PortalAnswerRevision>().RequireClient();
        routes.MapDelete("/v1/portal/answers/{answerId}", WithdrawAnswerAsync).RequireClient();
    }

    /// <summary>One access-log row against the binding's case, the client as principal.</summary>
    internal static Task RecordAsync(HttpContext context, ClientAccessor client, AccessAction action, CancellationToken cancellationToken) =>
        context.AccessLog().RecordAsync(AccessEvents.Record(client.Subject, action, caseId: client.CaseId, clock: context.Clock()), cancellationToken);

    /// <summary>
    /// The bound case's chapter and stage through the projection that takes
    /// the binding. A live binding whose case is gone is a 403, the same
    /// refusal as no binding at all — which of the two it is belongs to the firm.
    /// </summary>
    internal static async Task<CasePublicStatus> PublicStatusAsync(HttpContext context, ClientAccessor client, CancellationToken cancellationToken) =>
        await context.Cases().PublicStatusAsync(client, cancellationToken) ?? throw new ForbiddenException(NoCase);

    /// <summary>Whose portal this is: the client's name, the firm's, and the case's public status.</summary>
    /// <remarks>"Is my session good?" — recorded as <c>portal.read</c>.</remarks>
    internal static async Task<PortalMeView> MeAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var client = context.CurrentClient();
        await RecordAsync(context, client, AccessAction.PortalRead, cancellationToken);
        return PortalMeView.Of(client, await PublicStatusAsync(context, client, cancellationToken));
    }

    /// <summary>The questionnaire as the client's firm configured it — the enabled sections only.</summary>
    // Read from the binding's FIRM, never from anything in the request.
    internal static async Task<PortalQuestionnaireView> QuestionnaireAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var client = context.CurrentClient();
        var config = await context.FirmStore().GetQuestionnaireAsync(client.FirmId, cancellationToken);
        await RecordAsync(context, client, AccessAction.PortalRead, cancellationToken);
        return PortalQuestionnaireView.Of(config);
    }

    /// <summary>The client's own answers, every status, in sections the firm still shows.</summary>
    // Nobody else's candidates, not even a count.
    internal static async Task<PortalAnswerList> ListAnswersAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var client = context.CurrentClient();
        var candidates = context.Port<ICandidateStore>();
        var sections = await SectionsAsync(context, client, cancellationToken);
        var enabled = sections.Where(resolved => resolved.Enabled).Select(resolved => resolved.Section.Id).ToHashSet(StringComparer.Ordinal);
        var answers = new List<PortalAnswerView>();
        foreach (var candidate in await candidates.ListForCaseAsync(client.CaseId, cancellationToken))
        {
            if (!QuestionRules.IsOwnAnswer(candidate, client.Subject))
            {
                continue;
            }

            if (QuestionRules.RefOf(candidate) is { } answer && enabled.Contains(answer.Question.Section))
            {
                answers.Add(PortalAnswerView.Of(candidate, answer));
            }
        }

        await RecordAsync(context, client, AccessAction.PortalRead, cancellationToken);
        return new PortalAnswerList(answers);
    }

    /// <summary>Answer a question; the answer waits in the firm's review queue.</summary>
    /// <remarks>Its origin is the verified token's — never anything the body says.</remarks>
    // Channel client, the portal app client, the subject.
    internal static async Task<Created<PortalAnswerView>> CreateAnswerAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var client = context.CurrentClient();
        var principal = context.CurrentClientPrincipal();
        var candidates = context.Port<ICandidateStore>();
        var body = await context.BodyAsync(MaxAnswerBytes);
        var parsed = QuestionRules.ParseAnswer(body, await SectionsAsync(context, client, cancellationToken), client.Roles);
        var candidate = QuestionRules.CreateAnswer(
            parsed,
            client.CaseId,
            new CandidateOrigin(QuestionRules.Channel, principal.ClientId, client.Subject),
            await candidates.ListForCaseAsync(client.CaseId, cancellationToken),
            context.Clock());
        await candidates.CreateAsync(candidate, cancellationToken);
        await RecordAsync(context, client, AccessAction.PortalAnswer, cancellationToken);
        LogAnswer(context, client, candidate.Id, "created");
        var answer = QuestionRules.RefOf(candidate) ?? throw new InvalidOperationException("an answer was written without its question");
        return TypedResults.Created((string?)null, PortalAnswerView.Of(candidate, answer));
    }

    /// <summary>Change a pending answer's value.</summary>
    /// <remarks>
    /// Someone else's answer, or one in a section since switched off, is a 404;
    /// one already reviewed is a 409.
    /// </remarks>
    // The CAS on pending means an edit that races a reviewer loses rather
    // than rewriting what they accepted.
    internal static async Task<PortalAnswerView> UpdateAnswerAsync(HttpContext context, string answerId, CancellationToken cancellationToken)
    {
        var client = context.CurrentClient();
        var candidates = context.Port<ICandidateStore>();
        var body = await context.BodyAsync(MaxAnswerBytes);
        var (candidate, answer) = QuestionRules.OwnAnswerOr404(
            await candidates.GetAsync(client.CaseId, answerId, cancellationToken),
            client.Subject,
            await SectionsAsync(context, client, cancellationToken));
        var revised = QuestionRules.ReviseAnswer(candidate, answer, body["value"], context.Clock());
        if (await candidates.UpdateAsync(revised, CandidateStatus.Pending, cancellationToken) is null)
        {
            throw new ConflictException(Reviewed);
        }

        await RecordAsync(context, client, AccessAction.PortalAnswer, cancellationToken);
        LogAnswer(context, client, revised.Id, "updated");
        return PortalAnswerView.Of(revised, answer);
    }

    /// <summary>Withdraw a pending answer.</summary>
    /// <remarks>The withdrawn answer is kept. 409 once reviewed; 404 for anything not the caller's.</remarks>
    internal static async Task<PortalAnswerView> WithdrawAnswerAsync(HttpContext context, string answerId, CancellationToken cancellationToken)
    {
        var client = context.CurrentClient();
        var candidates = context.Port<ICandidateStore>();
        var (candidate, answer) = QuestionRules.OwnAnswerOr404(
            await candidates.GetAsync(client.CaseId, answerId, cancellationToken),
            client.Subject,
            await SectionsAsync(context, client, cancellationToken));
        var withdrawn = CandidateRules.Withdraw(candidate, client.Subject, context.Clock());
        if (await candidates.UpdateAsync(withdrawn, CandidateStatus.Pending, cancellationToken) is null)
        {
            throw new ConflictException(Reviewed);
        }

        await RecordAsync(context, client, AccessAction.PortalAnswer, cancellationToken);
        LogAnswer(context, client, withdrawn.Id, "withdrawn");
        return PortalAnswerView.Of(withdrawn, answer);
    }

    // The firm's questionnaire as it stands NOW — read on every answer
    // request, so a section switched off stops taking answers immediately.
    private static async Task<IReadOnlyList<ResolvedSection>> SectionsAsync(HttpContext context, ClientAccessor client, CancellationToken cancellationToken) =>
        Questionnaire.Resolve(await context.FirmStore().GetQuestionnaireAsync(client.FirmId, cancellationToken));

    // GLBA: ids and the verb — never the value, never the question's text.
    private static void LogAnswer(HttpContext context, ClientAccessor client, string candidateId, string what) =>
        context.Log(Logger, "portal answer", ("case_id", client.CaseId), ("candidate_id", candidateId), ("what", what));
}
