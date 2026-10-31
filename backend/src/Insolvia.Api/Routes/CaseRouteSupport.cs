using Insolvia.Api.Auth;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Routes;

/// <summary>
/// What every case route module's private helpers did in Python
/// (<c>_stores()</c>, <c>_reachable_case_or_404</c>), written once.
/// </summary>
internal static class CaseRouteSupport
{
    /// <summary>The 404 every unreachable case answers — "no such case", "another firm's" and "not linked" alike.</summary>
    public const string CaseNotFound = "case not found";

    /// <summary>
    /// A composed port, or a 500 naming it — a composition bug, never an
    /// answer a caller can provoke (Python's <c>RuntimeError("… not composed")</c>).
    /// </summary>
    public static T Port<T>(this HttpContext context)
        where T : class =>
        context.RequestServices.GetService<T>() ?? throw new InvalidOperationException($"{typeof(T).Name} is not composed");

    /// <summary>The case store.</summary>
    public static ICaseStore Cases(this HttpContext context) => context.Port<ICaseStore>();

    /// <summary>
    /// The case through the WHOLE access rule, its read recorded as
    /// <paramref name="action"/> — <c>denied</c> when refused, because someone
    /// walking case ids is what the access log is for — or a 404.
    /// </summary>
    public static async Task<Case> ReachableCaseAsync(this HttpContext context, string caseId, AccessAction action, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var found = await context.Cases().GetAsync(caseId, accessor, cancellationToken);
        await context.RecordCaseAccessAsync(caseId, action, found is not null, cancellationToken);
        return found ?? throw new NotFoundException(CaseNotFound);
    }

    /// <summary>The case through the access rule, NOT logged (the assignee list, the history), or a 404.</summary>
    public static async Task<Case> VisibleCaseAsync(this HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        return await context.Cases().GetAsync(caseId, accessor, cancellationToken) ?? throw new NotFoundException(CaseNotFound);
    }

    /// <summary>One access-log row keyed to a case, the caller as principal.</summary>
    public static async Task RecordCaseAccessAsync(this HttpContext context, string caseId, AccessAction action, bool allowed, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await context.AccessLog().RecordAsync(
            AccessEvents.Record(accessor.Subject, action, caseId: caseId, outcome: allowed ? AccessOutcome.Allowed : AccessOutcome.Denied, clock: context.Clock()),
            cancellationToken);
    }

    /// <summary>One access-log row keyed to a client.</summary>
    public static async Task RecordClientAccessAsync(this HttpContext context, string clientId, AccessAction action, bool allowed, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await context.AccessLog().RecordAsync(
            AccessEvents.Record(accessor.Subject, action, clientId: clientId, outcome: allowed ? AccessOutcome.Allowed : AccessOutcome.Denied, clock: context.Clock()),
            cancellationToken);
    }

    /// <summary>"Today" for every task in one response — the UTC date.</summary>
    public static DateOnly Today(this HttpContext context) => Clock.Today(context.Clock());
}
