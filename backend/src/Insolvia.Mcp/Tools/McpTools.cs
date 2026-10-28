using Insolvia.Core.Candidates;

namespace Insolvia.Mcp.Tools;

/// <summary>
/// The eight tools of mcp-surface.md, registered in the order the listing
/// returns them (services/mcp <c>api/server.py</c>). The list is STATIC — the
/// same eight for every authenticated session (list-visible, call-denied):
/// permissions are revocable mid-hour, so a filtered list would be stale the
/// moment it mattered; a call they do not admit answers permission_denied.
/// </summary>
/// <remarks>
/// The camelCase parameter names ARE the wire (<c>caseId</c>,
/// <c>entityType</c>, <c>recordId</c>, <c>candidateIds</c>): the input schema's
/// property names are the contract the surface publishes.
/// </remarks>
public static class McpTools
{
    /// <summary>The server's name in <c>serverInfo</c>.</summary>
    public const string ServerName = "insolvia";

    /// <summary>The server's title in <c>serverInfo</c>.</summary>
    public const string ServerTitle = "Insolvia case management";

    /// <summary>What a harness is told about the server as a whole.</summary>
    public const string Instructions =
        "Bankruptcy case preparation for the signed-in attorney's firm. "
        + "Reads return live case data; writes land as candidate records a "
        + "human reviews before anything becomes case data. Start with "
        + "whoami, find matters with list_cases, and poll check_proposals "
        + "for review outcomes.";

    private static readonly ToolParameter CaseId = new("caseId", ArgumentKind.Text);
    private static readonly ToolParameter EntityType = new("entityType", ArgumentKind.Text);
    private static readonly ToolParameter Status = new("status", ArgumentKind.OptionalText);
    private static readonly ToolParameter Limit = new("limit", ArgumentKind.OptionalInteger);
    private static readonly ToolParameter Cursor = new("cursor", ArgumentKind.OptionalText);

    private static string EntityTypeDoc =>
        "One of: " + string.Join(", ", CaseTools.EntityTypes)
        + ". `clients` are the firm's client records this case's debtors are "
        + "linked to (recordId is the client id; read-only, tax id last four only).";

    /// <summary>The eight, in listing order.</summary>
    /// <returns>A fresh set of tool definitions.</returns>
    public static IReadOnlyList<InsolviaTool> All() =>
    [
        new(
            "whoami",
            "Who am I",
            "The caller's firm, display name, and per-feature permissions — or the fact that they have no firm. The /v1/me of this surface.",
            readOnly: true,
            [],
            ToolSchemas.WhoamiResult,
            async call => CaseTools.Whoami(await call.Resolve().ConfigureAwait(false))),

        new(
            "list_cases",
            "List cases",
            "The cases the caller may see, newest first, paginated; archived and deleted cases are not listed. Optional status filter: intake, ready_to_file, filed, discharged, dismissed, or closed.",
            readOnly: true,
            [Status, Limit, Cursor],
            ToolSchemas.ListCasesResult,
            async call => await call.Tools.ListCasesAsync(
                await call.AccessorOrRefuseAsync().ConfigureAwait(false),
                call.Arguments.Text("status"),
                call.Arguments.WholeNumber("limit"),
                call.Arguments.Text("cursor"),
                call.CancellationToken).ConfigureAwait(false)),

        new(
            "get_case",
            "Get case",
            "One case plus per-entity-type record counts.",
            readOnly: true,
            [CaseId],
            ToolSchemas.GetCaseResult,
            async call => await call.Tools.GetCaseAsync(
                await call.AccessorOrRefuseAsync().ConfigureAwait(false),
                call.Arguments.Text("caseId")!,
                call.CancellationToken).ConfigureAwait(false)),

        new(
            "list_case_records",
            "List case records",
            "One entity type's records within one case, paginated. entityType: " + EntityTypeDoc,
            readOnly: true,
            [CaseId, EntityType, Limit, Cursor],
            ToolSchemas.ListCaseRecordsResult,
            async call => await call.Tools.ListCaseRecordsAsync(
                await call.AccessorOrRefuseAsync().ConfigureAwait(false),
                call.Arguments.Text("caseId")!,
                call.Arguments.Text("entityType")!,
                call.Arguments.WholeNumber("limit"),
                call.Arguments.Text("cursor"),
                call.CancellationToken).ConfigureAwait(false)),

        new(
            "get_case_record",
            "Get case record",
            "One record by id. entityType: " + EntityTypeDoc,
            readOnly: true,
            [CaseId, EntityType, new("recordId", ArgumentKind.Text)],
            ToolSchemas.GetCaseRecordResult,
            async call => await call.Tools.GetCaseRecordAsync(
                await call.AccessorOrRefuseAsync().ConfigureAwait(false),
                call.Arguments.Text("caseId")!,
                call.Arguments.Text("entityType")!,
                call.Arguments.Text("recordId")!,
                call.CancellationToken).ConfigureAwait(false)),

        new(
            "propose_case_records",
            "Propose case records",
            "Write a batch of CANDIDATE records for human review — nothing becomes case data until a person accepts it in the app. "
                + "1-25 proposals per call, each {entityType, payload, externalRef?, note?}; payload mirrors the target entity's wire shape. "
                + "Proposable entity types: " + string.Join(", ", CandidateRules.ProposableEntityTypes) + ".",
            readOnly: false,
            [CaseId, new("proposals", ArgumentKind.ObjectList)],
            ToolSchemas.ProposeCaseRecordsResult,
            async call => await call.Tools.ProposeCaseRecordsAsync(
                await call.AccessorOrRefuseAsync().ConfigureAwait(false),
                call.Arguments.Text("caseId")!,
                call.Arguments.ObjectList("proposals"),
                call.ClientId,
                call.CancellationToken).ConfigureAwait(false)),

        new(
            "check_proposals",
            "Check proposals",
            "The review status of this surface's candidates: pending, accepted, corrected (with the human's correctedPayload), rejected, or withdrawn — "
                + "plus the resulting record id once accepted. Poll this; review happens in the app, often hours after the proposing session ends.",
            readOnly: true,
            [CaseId, new("candidateIds", ArgumentKind.OptionalTextList), Status, Limit, Cursor],
            ToolSchemas.CheckProposalsResult,
            async call => await call.Tools.CheckProposalsAsync(
                await call.AccessorOrRefuseAsync().ConfigureAwait(false),
                call.Arguments.Text("caseId")!,
                call.Arguments.TextList("candidateIds"),
                call.Arguments.Text("status"),
                call.Arguments.WholeNumber("limit"),
                call.Arguments.Text("cursor"),
                call.CancellationToken).ConfigureAwait(false)),

        new(
            "withdraw_proposal",
            "Withdraw proposal",
            "Retract the caller's own still-pending candidate so it does not sit in a reviewer's queue. Only the proposer may withdraw, and only while pending.",
            readOnly: false,
            [CaseId, new("candidateId", ArgumentKind.Text)],
            ToolSchemas.WithdrawProposalResult,
            async call => await call.Tools.WithdrawProposalAsync(
                await call.AccessorOrRefuseAsync().ConfigureAwait(false),
                call.Arguments.Text("caseId")!,
                call.Arguments.Text("candidateId")!,
                call.CancellationToken).ConfigureAwait(false)),
    ];
}
