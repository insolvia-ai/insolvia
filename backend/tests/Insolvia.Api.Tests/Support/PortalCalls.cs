using Insolvia.Core.Firms;

namespace Insolvia.Api.Tests.Support;

/// <summary>
/// The portal suites' helpers over <see cref="CaseWorld"/>: requests signed
/// with a PORTAL token (test_portal_routes.py's <c>portal()</c>) and the
/// firm's invitation (<c>invite</c> / <c>invited_subject</c>).
/// </summary>
internal static class PortalCalls
{
    /// <summary>Firm A's paralegal granted <c>client_portal</c> at add/edit (the Python's DANA).</summary>
    public const string Granted = "00000000-0000-4000-8000-0000000096a7";

    public static Task<Answer> PortalGet(this CaseWorld world, string path, string subject) => Send(world, HttpMethod.Get, path, subject, null);

    public static Task<Answer> PortalPost(this CaseWorld world, string path, string? json, string subject) => Send(world, HttpMethod.Post, path, subject, json);

    public static Task<Answer> PortalPut(this CaseWorld world, string path, string? json, string subject) => Send(world, HttpMethod.Put, path, subject, json);

    public static Task<Answer> PortalDelete(this CaseWorld world, string path, string subject) => Send(world, HttpMethod.Delete, path, subject, null);

    /// <summary>Adds <see cref="Granted"/> to firm A.</summary>
    public static Task GrantAsync(this CaseWorld world) =>
        world.World.WithAsync(FirmWorld.Member(Granted, role: FirmRole.Paralegal, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Paralegal, Feature.ClientPortal, PermissionLevel.AddEdit)));

    /// <summary>POST …/portal/invitation, as given.</summary>
    public static Task<Answer> Invite(this CaseWorld world, string caseId, string email = "pat@example.test", string? roles = null, string caller = CaseWorld.Alice)
    {
        var body = $$"""{"email": "{{email}}", "displayName": "Pat Example"{{(roles is null ? string.Empty : $", \"roles\": {roles}")}}}""";
        return world.Post($"/v1/cases/{caseId}/portal/invitation", body, caller);
    }

    /// <summary>An invitation that must succeed; the invited subject.</summary>
    public static async Task<string> InvitedAsync(this CaseWorld world, string caseId, string email = "pat@example.test", string? roles = null)
    {
        var answer = await world.Invite(caseId, email, roles);
        Assert.True(answer.Status == 201, answer.Body);
        return answer.Object.Str("subject")!;
    }

    private static Task<Answer> Send(CaseWorld world, HttpMethod method, string path, string subject, string? json) =>
        world.World.SendAsync(method, path, subject: null, json: json, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + Tokens.Portal(subject) });
}
