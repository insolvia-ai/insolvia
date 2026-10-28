using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Insolvia.Core.Filings;

// The attorney's per-filing approval (ADR 0024 build PR 6, guardrail 1; the
// record half of services/api core/filing_approval.py). The rules that read
// the filing set — approve, consume, the digest — are Insolvia.Forms'
// FilingApprovals, because the basis is a Forms computation; the record, its
// store's port and the filing job's wire contract are here, where the filing
// worker and the API both reach them.

/// <summary>
/// One approval of one filing set. <see cref="AttorneyId"/> is the approver
/// AND the owner of <see cref="CredentialId"/>; <see cref="AuthenticatedAt"/>
/// the <c>auth_time</c> it was made under and <see cref="ExpiresAt"/> the
/// instant it stops being consumable, both epoch seconds (the consume's
/// conditional write compares a number).
/// </summary>
/// <param name="ApprovalId">The server-minted uuid.</param>
/// <param name="FilingId">The filing it authorizes — the worker's record id.</param>
/// <param name="CaseId">The case.</param>
/// <param name="FirmId">The case's firm.</param>
/// <param name="AttorneyId">The approver, whose credential files it.</param>
/// <param name="CredentialId">That credential.</param>
/// <param name="AuthorizationId">The signed authorization it stands on.</param>
/// <param name="Digest">The filing set's digest at approval.</param>
/// <param name="PacketId">The filing-set packet approved.</param>
/// <param name="Court">The court's code.</param>
/// <param name="Division">The division's code.</param>
/// <param name="RegistryRelease">The court-registry release the filing set was judged against.</param>
/// <param name="ApprovedAt">When.</param>
/// <param name="AuthenticatedAt">The sign-in's <c>auth_time</c>, epoch seconds.</param>
/// <param name="ExpiresAt">Epoch seconds.</param>
public sealed record FilingApproval(
    string ApprovalId,
    string FilingId,
    string CaseId,
    string FirmId,
    string AttorneyId,
    string CredentialId,
    string AuthorizationId,
    string Digest,
    string PacketId,
    string Court,
    string Division,
    string RegistryRelease,
    DateTimeOffset ApprovedAt,
    long AuthenticatedAt,
    long ExpiresAt)
{
    /// <summary>Pending until the worker consumes it or something voids it.</summary>
    public ApprovalStatus Status { get; init; } = ApprovalStatus.Pending;

    /// <summary>When the worker consumed it.</summary>
    public DateTimeOffset? ConsumedAt { get; init; }

    /// <summary>When it was voided.</summary>
    public DateTimeOffset? VoidedAt { get; init; }

    /// <summary>Why it was voided (<see cref="VoidReasons"/>).</summary>
    public string? VoidReason { get; init; }
}

/// <summary>An approval's stored state (Python's <c>ApprovalStatus</c>).</summary>
public enum ApprovalStatus
{
    /// <summary>Approved, not yet used.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>Used — once — by the filing worker.</summary>
    [JsonStringEnumMemberName("consumed")]
    Consumed,

    /// <summary>Voided before it was used.</summary>
    [JsonStringEnumMemberName("voided")]
    Voided,
}

/// <summary>Why an approval was voided (Python's <c>VOID_REASONS</c>).</summary>
public static class VoidReasons
{
    /// <summary>The filing set no longer has the approved digest.</summary>
    public const string Changed = "changed";

    /// <summary>A newer approval replaced it.</summary>
    public const string Superseded = "superseded";

    /// <summary>Somebody at the firm cancelled it.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The filing queue refused its job.</summary>
    public const string EnqueueFailed = "enqueue_failed";
}

/// <summary>The approval record's pure rules.</summary>
public static class FilingApprovalRecords
{
    /// <summary>The stored timestamp format (Python's <c>isoformat(timespec="milliseconds")</c> with a Z).</summary>
    public static string Iso(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Epoch seconds, as Python's <c>time.time()</c> (fractional).</summary>
    public static double Seconds(DateTimeOffset moment) => moment.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// What the approval is NOW: <c>pending</c>, <c>consumed</c>, <c>voided</c>,
    /// or <c>expired</c> — a pending approval past <see cref="FilingApproval.ExpiresAt"/>,
    /// which can never be consumed.
    /// </summary>
    public static string EffectiveStatus(FilingApproval approval, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return approval.Status == ApprovalStatus.Pending && Seconds(now) >= approval.ExpiresAt
            ? "expired"
            : approval.Status.ToWire();
    }

    /// <summary>The approval marked consumed.</summary>
    public static FilingApproval Consumed(FilingApproval approval, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return approval with { Status = ApprovalStatus.Consumed, ConsumedAt = at };
    }

    /// <summary>The approval marked voided.</summary>
    public static FilingApproval Voided(FilingApproval approval, string reason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return approval with { Status = ApprovalStatus.Voided, VoidedAt = at, VoidReason = reason };
    }
}

/// <summary>One filing job as the consumer parsed it.</summary>
/// <param name="ApprovalId">The approval to consume.</param>
/// <param name="FilingId">Its filing.</param>
/// <param name="CaseId">Its case.</param>
public sealed record FilingJob(string ApprovalId, string FilingId, string CaseId);

/// <summary>
/// THE filing job's wire contract (Python's <c>filing_job_message</c> /
/// <c>parse_filing_job_message</c>): which approval, which filing, which
/// case — ids, never contents. The worker reads everything else from the
/// stores under its own role; a message is not a place a secret or a
/// document can ride.
/// </summary>
public static class FilingJobs
{
    /// <summary>The <c>kind</c>.</summary>
    public const string Kind = "filing.submit";

    /// <summary>The <c>version</c>.</summary>
    public const int Version = 1;

    /// <summary>Exactly these five keys.</summary>
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
    {
        "kind", "version", "approval_id", "filing_id", "case_id",
    };

    /// <summary>
    /// The SQS body — byte-equal to Python's
    /// <c>json.dumps(filing_job_message(a), separators=(",", ":"), sort_keys=True)</c>.
    /// The ids are server-minted uuids, so ASCII escaping never applies.
    /// </summary>
    public static string Message(FilingApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return "{"
            + $"\"approval_id\":{Quote(approval.ApprovalId)},"
            + $"\"case_id\":{Quote(approval.CaseId)},"
            + $"\"filing_id\":{Quote(approval.FilingId)},"
            + $"\"kind\":\"{Kind}\","
            + $"\"version\":{Version}"
            + "}";
    }

    /// <summary>
    /// The consumer's half: refuses (<see cref="ValidationException"/>)
    /// anything but a JSON object with exactly the five keys, this kind and
    /// this version.
    /// </summary>
    public static FilingJob Parse(string body)
    {
        JsonNode? raw;
        try
        {
            raw = JsonNode.Parse(body ?? string.Empty);
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new ValidationException("a filing job is not JSON", error);
        }

        if (raw is not JsonObject map || map.Count != Keys.Count || map.Any(pair => !Keys.Contains(pair.Key)))
        {
            throw new ValidationException("a filing job carries exactly its five keys");
        }

        var version = map["version"] is JsonValue number && number.TryGetValue<double>(out var value) ? value : double.NaN;
        if (Json.AsString(map["kind"]) != Kind || version != Version)
        {
            throw new ValidationException("not a filing job this consumer understands");
        }

        return new FilingJob(Text(map["approval_id"]), Text(map["filing_id"]), Text(map["case_id"]));
    }

    private static string Text(JsonNode? node) => Json.AsString(node) ?? node?.ToJsonString() ?? "None";

    private static string Quote(string id) =>
        System.Text.Json.JsonSerializer.Serialize(id);
}
