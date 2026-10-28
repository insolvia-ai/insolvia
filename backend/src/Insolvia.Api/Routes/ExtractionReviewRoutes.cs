using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Candidates;
using Insolvia.Core.Debtors;
using Insolvia.Core.Expenses;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using Insolvia.Core.Questions;

namespace Insolvia.Api.Routes;

/// <summary>
/// The extraction review queue (routes/extraction_review.py; issue #89 / 8.9):
/// the per-case candidate queue — extraction's, the MCP surface's and the
/// portal's answers, one queue — and the one confirmation act that turns a
/// candidate into case data. WHO MAY CONFIRM IS A PERMISSION: the
/// <c>extraction_review</c> feature (hidden → 403, view_only reads, add_edit
/// reviews). The case lookup is still the first gate on every path.
/// </summary>
public sealed class ExtractionReviewRoutes : IEndpointModule
{
    // The corrected payload is an entity body — the entity routes' ceiling.
    private const int MaxRequestBytes = 256 * 1024;
    private const string Logger = "insolvia_api.api.routes.extraction_review";
    private const string Reviewed = "candidate was reviewed by someone else";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/extraction/candidates", ListAsync).RequireAuth().Requires(Feature.ExtractionReview, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/cases/{caseId}/extraction/candidates/{candidateId}/review", ReviewAsync).RequireAuth().Requires(Feature.ExtractionReview, PermissionLevel.AddEdit);
    }

    /// <summary>
    /// The queue, creation order, every stream. <c>?status=</c> narrows; the
    /// unfiltered history keeps corrections and rejections on purpose.
    /// </summary>
    private static async Task<IResult> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var status = ExtractionReview.ParseStatusFilter(context.Request.Query.TryGetValue("status", out var raw) ? raw.ToString() : null);
        await context.ReachableCaseAsync(caseId, AccessAction.ExtractionRead, cancellationToken);
        var listed = (await context.Port<ICandidateStore>().ListForCaseAsync(caseId, cancellationToken))
            .Where(candidate => status is null || candidate.Status == status)
            .ToList();
        var names = listed.Any(candidate => candidate.Origin.Channel == OriginChannel.Client)
            ? await ClientNamesAsync(context, caseId, cancellationToken)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return FlaskJson.Result(new JsonObject { ["candidates"] = new JsonArray([.. listed.Select(candidate => (JsonNode)QueueJson(candidate, names))]) });
    }

    /// <summary>
    /// Accept (optionally corrected) or reject ONE pending candidate. The
    /// candidate is compare-and-swapped BEFORE the record is written; a second
    /// reviewer gets a 409 and nothing is written.
    /// </summary>
    private static async Task<IResult> ReviewAsync(HttpContext context, string caseId, string candidateId, CancellationToken cancellationToken)
    {
        var candidates = context.Port<ICandidateStore>();
        var accessor = await context.CurrentAccessorAsync();
        var decision = ExtractionReview.ParseReview(await context.BodyAsync(MaxRequestBytes));
        await context.ReachableCaseAsync(caseId, AccessAction.ExtractionReview, cancellationToken);

        var candidate = await candidates.GetAsync(caseId, candidateId, cancellationToken) ?? throw new NotFoundException("candidate not found");
        if (candidate.Status != CandidateStatus.Pending)
        {
            throw new ConflictException($"candidate has already been reviewed (status: {candidate.Status.ToWire()})");
        }

        var moment = ExtractionReview.ReviewMoment(context.Clock());
        if (!decision.Accept)
        {
            var rejected = ExtractionReview.Reject(candidate, accessor.Subject, moment, context.Clock());
            _ = await candidates.UpdateAsync(rejected, CandidateStatus.Pending, cancellationToken) ?? throw new ConflictException(Reviewed);
            LogReviewed(context, caseId, candidate.Id, "rejected");
            return FlaskJson.Result(new JsonObject { ["candidate"] = ExtractionReview.CandidateJson(rejected) });
        }

        if (ExtractionReview.IsDebtorAnswer(candidate))
        {
            return await AcceptDebtorAnswerAsync(context, candidate, decision.CorrectedPayload, moment, accessor.Subject, cancellationToken);
        }

        // Kind first (an unreviewable type is a 400 before any write), then
        // references, then the draft with its minted provenance.
        var kind = ExtractionReview.ReviewableKind(candidate);
        var siblings = (await candidates.ListForCaseAsync(caseId, cancellationToken))
            .Where(sibling => sibling.Id != candidate.Id)
            .ToDictionary(sibling => sibling.Id, StringComparer.Ordinal);
        var payload = ExtractionReview.ResolveCandidateReferences(candidate.EntityType, decision.CorrectedPayload ?? candidate.Payload, siblings);
        payload = await WithClientLinksAsync(context, candidate, payload, cancellationToken);
        var draft = ExtractionReview.BuildAcceptedDraft(candidate, payload, accessor.Subject, moment);
        var now = Clock.Now(context.Clock());
        var entity = kind.Restore(Guid.NewGuid().ToString(), caseId, now, now, draft);

        var reviewed = ExtractionReview.Accept(candidate, decision.CorrectedPayload, entity.Id, accessor.Subject, moment, context.Clock());
        _ = await candidates.UpdateAsync(reviewed, CandidateStatus.Pending, cancellationToken) ?? throw new ConflictException(Reviewed);
        await context.Port<ICaseEntityStore>().CreateAsync(entity, cancellationToken);
        LogReviewed(context, caseId, candidate.Id, reviewed.Status.ToWire());
        return FlaskJson.Result(new JsonObject { ["candidate"] = ExtractionReview.CandidateJson(reviewed), ["record"] = CaseViews.Entity(entity) });
    }

    /// <summary>
    /// A client's answer about a debtor's own fields MERGES into that debtor —
    /// keyed by filing role, existing since the case opened. A Debtor 2 not yet
    /// linked is a 409 naming the fix.
    /// </summary>
    private static async Task<IResult> AcceptDebtorAnswerAsync(HttpContext context, Candidate candidate, JsonObject? corrected, string moment, string subject, CancellationToken cancellationToken)
    {
        var debtors = context.Port<IDebtorStore>();
        var role = ExtractionReview.AnswerFilingRole(candidate);
        var stored = await debtors.GetAsync(candidate.CaseId, role, cancellationToken)
            ?? throw new ConflictException("link a client to this debtor before accepting answers about them");
        var draft = ExtractionReview.BuildAcceptedDebtor(candidate, corrected ?? candidate.Payload, stored, subject, moment);
        var debtor = DebtorRules.Replace(stored, draft, stored.TaxId, context.Clock());
        var reviewed = ExtractionReview.Accept(candidate, corrected, debtor.Id, subject, moment, context.Clock());
        _ = await context.Port<ICandidateStore>().UpdateAsync(reviewed, CandidateStatus.Pending, cancellationToken) ?? throw new ConflictException(Reviewed);
        await debtors.PutAsync(debtor, cancellationToken);
        LogReviewed(context, candidate.CaseId, candidate.Id, reviewed.Status.ToWire());
        return FlaskJson.Result(new JsonObject { ["candidate"] = ExtractionReview.CandidateJson(reviewed), ["record"] = CaseViews.Debtor(debtor) });
    }

    // A client record answer's link, from the case — read on the STAFF side,
    // because the portal never reads case records and so knows no ids.
    private static async Task<JsonObject> WithClientLinksAsync(HttpContext context, Candidate candidate, JsonObject payload, CancellationToken cancellationToken)
    {
        if (candidate.Origin.Channel != OriginChannel.Client || !ExtractionReview.ClientLinks.TryGetValue(candidate.EntityType, out var field))
        {
            return payload;
        }

        string? debtorId = null;
        string? householdId = null;
        if (field == "debtor_id")
        {
            debtorId = (await context.Port<IDebtorStore>().GetAsync(candidate.CaseId, ExtractionReview.AnswerFilingRole(candidate), cancellationToken))?.Id;
        }
        else
        {
            householdId = (await context.Port<ICaseEntityStore>().ListTypedAsync(candidate.CaseId, ExpenseRules.Household, cancellationToken))
                .FirstOrDefault(household => household.Body.WhichHousehold == WhichHousehold.Main)?.Id;
        }

        return ExtractionReview.FillClientLinks(candidate, payload, debtorId, householdId);
    }

    // subject → the display name the FIRM gave at invitation, for every
    // binding the case has had (revoked ones too: an answer outlives access).
    private static async Task<Dictionary<string, string>> ClientNamesAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var store = context.RequestServices.GetService<IClientBindingStore>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (store is null)
        {
            return names;
        }

        foreach (var binding in await store.ListForCaseAsync(caseId, cancellationToken))
        {
            names[binding.Subject] = binding.DisplayName;
        }

        return names;
    }

    private static JsonObject QueueJson(Candidate candidate, Dictionary<string, string> names)
    {
        var body = ExtractionReview.CandidateJson(candidate);
        if (QuestionRules.RefOf(candidate) is { } answer)
        {
            body["question"] = ExtractionReview.ReviewQuestionJson(answer);
            if (names.TryGetValue(candidate.Origin.Subject, out var name))
            {
                body["client"] = new JsonObject { ["displayName"] = name };
            }
        }

        return body;
    }

    // GLBA: ids and the outcome word — never a payload.
    private static void LogReviewed(HttpContext context, string caseId, string candidateId, string outcome) =>
        context.Log(Logger, "candidate reviewed", ("case_id", caseId), ("candidate_id", candidateId), ("outcome", outcome));
}
