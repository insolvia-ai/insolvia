using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Core.Documents;

namespace Insolvia.Core.Jobs;

// Async pipeline jobs (ADR 0018; from services/api core/jobs.py): the record,
// its transitions, and the queue contract both sides of SQS meet at. The
// dispatch loop that runs them is the worker host's (Insolvia.Worker); what
// lives here is everything the API and the worker must agree on, so they
// cannot drift apart.

/// <summary>
/// A job's lifecycle. <c>queued</c> is written by the accept endpoint BEFORE
/// the message is sent; the worker moves it forward. Only <c>succeeded</c> is
/// immutable: a redelivered message may re-run a <c>running</c> job (a crashed
/// attempt) or a <c>failed</c> one (an infrastructure failure a retry fixes).
/// </summary>
public enum JobStatus
{
    /// <summary>Accepted; not started.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("queued")]
    Queued,

    /// <summary>An attempt is (or was, before a crash) in flight.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("running")]
    Running,

    /// <summary>Finished; immutable.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("succeeded")]
    Succeeded,

    /// <summary>Failed — terminal for a deterministic failure, retryable for an infrastructure one.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>What the preparer is told: a category and a message SAFE TO RENDER — never an exception's text.</summary>
public sealed record JobFailure(string Category, string Message);

/// <summary>
/// A job record — a child row of its case. Reached only through its case: the
/// routes resolve the case through the case store first, so ownership is not
/// re-derived here.
/// </summary>
/// <param name="Id">The server-minted uuid.</param>
/// <param name="CaseId">The case — half the key.</param>
/// <param name="Kind">One of <see cref="JobRules.Kinds"/>.</param>
/// <param name="Status">Where it is.</param>
/// <param name="CreatedBy">The firm user who accepted it.</param>
/// <param name="CreatedAt">Accepted at — the list order (with the id).</param>
/// <param name="UpdatedAt">Last transition.</param>
public sealed record Job(
    string Id,
    string CaseId,
    string Kind,
    JobStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Which document a document-scoped kind works on — an identifier, never a payload.</summary>
    public string? DocumentId { get; init; }

    /// <summary>The output options an options-scoped kind was accepted with — validated and canonical, by the route.</summary>
    public JsonObject? Options { get; init; }

    /// <summary>How many attempts started.</summary>
    public int Attempts { get; init; }

    /// <summary>Why it failed, when it did.</summary>
    public JobFailure? Failure { get; init; }

    /// <summary>What a worker answered.</summary>
    public JsonObject? Result { get; init; }
}

/// <summary>The decoded queue message: identifiers only, never case data.</summary>
public sealed record JobMessage(string JobId, string CaseId, string Kind, string? DocumentId = null);

/// <summary>
/// A worker's EXPECTED failure (Python's <c>JobError</c>), with a message safe to
/// show the preparer. It marks the job failed and is NOT retried: the failure
/// is deterministic, so a retry would only delay the person waiting. Anything
/// else a worker throws is infrastructure — marked failed with a generic
/// message and re-thrown so SQS redelivers.
/// </summary>
public sealed class JobException : Exception
{
    /// <summary>The default category.</summary>
    public const string DefaultCategory = "job_error";

    /// <summary>Creates the exception.</summary>
    public JobException()
        : this("The job failed.")
    {
    }

    /// <summary>Creates the exception with a preparer-safe message.</summary>
    public JobException(string message)
        : this(message, DefaultCategory)
    {
    }

    /// <summary>Creates the exception with a preparer-safe message and its cause.</summary>
    public JobException(string message, Exception innerException)
        : base(message, innerException) => Category = DefaultCategory;

    /// <summary>Creates the exception with a preparer-safe message and a category.</summary>
    public JobException(string message, string category, Exception? innerException = null)
        : base(message, innerException) => Category = category;

    /// <summary>The failure category the job row records.</summary>
    public string Category { get; }
}

/// <summary>The job rules (core/jobs.py).</summary>
public static class JobRules
{
    /// <summary>The wire envelope's version. Bumped only with a consumer that understands both.</summary>
    public const int MessageVersion = 1;

    /// <summary>The walking skeleton's kind: proves accept → deliver → run → status; reads no case data.</summary>
    public const string EchoKind = "echo";

    /// <summary>Packet assembly (9.6).</summary>
    public const string PacketAssemblyKind = "packet_assembly";

    /// <summary>The AI petition review (9.7, ADR 0019).</summary>
    public const string PetitionReviewKind = "petition_review";

    /// <summary>Extraction of one document into candidates (8.7/8.8).</summary>
    public const string DocumentExtractionKind = "document_extraction";

    /// <summary>
    /// What the accept endpoint admits, in Python's order (<c>(*WORKERS, …)</c>).
    /// The worker's registry may lack one (the deploy window); its
    /// unknown-kind branch covers that.
    /// </summary>
    public static readonly IReadOnlyList<string> Kinds = [EchoKind, PacketAssemblyKind, PetitionReviewKind, DocumentExtractionKind];

    /// <summary>The kinds that work on ONE document: <c>documentId</c> required, and part of the idempotency key.</summary>
    public static readonly IReadOnlySet<string> DocumentScopedKinds = new HashSet<string>(StringComparer.Ordinal) { DocumentExtractionKind };

    /// <summary>The kinds that take output options: <c>options</c> admitted, and part of the idempotency key.</summary>
    public static readonly IReadOnlySet<string> OptionsScopedKinds = new HashSet<string>(StringComparer.Ordinal) { PacketAssemblyKind };

    /// <summary>Which document kinds have an extractor (core/extraction.EXTRACTABLE_DOCUMENT_KINDS).</summary>
    public static readonly IReadOnlyList<DocumentKind> ExtractableDocumentKinds = [DocumentKind.CreditReport, DocumentKind.PayStub];

    /// <summary>The statuses that mean "this work is already in flight".</summary>
    public static readonly IReadOnlySet<JobStatus> ActiveStatuses = new HashSet<JobStatus> { JobStatus.Queued, JobStatus.Running };

    /// <summary>The job store's list order: creation, tie-broken by id (Python's <c>list_order</c>).</summary>
    public static IComparer<Job> ListOrder { get; } = Comparer<Job>.Create((left, right) =>
    {
        var byTime = left.CreatedAt.CompareTo(right.CreatedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(left.Id, right.Id);
    });

    /// <summary>
    /// Validate POST /v1/cases/{id}/jobs; unknown keys are ignored. <c>kind</c>
    /// always; <c>documentId</c> exactly when the kind is document-scoped;
    /// <c>options</c> (an object, structurally — its fields are the route's
    /// business) exactly when the kind is options-scoped.
    /// </summary>
    public static (string Kind, string? DocumentId, JsonObject? Options) ParseAcceptance(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var kind = Json.AsString(payload["kind"]);
        if (kind is null || !Kinds.Contains(kind, StringComparer.Ordinal))
        {
            throw Field("kind", "Kind must be one of " + string.Join(", ", Kinds) + ".");
        }

        string? documentId = null;
        var hasDocument = payload.TryGetPropertyValue("documentId", out var rawDocument) && rawDocument is not null;
        if (DocumentScopedKinds.Contains(kind))
        {
            var text = Json.AsString(rawDocument);
            if (text is null || TextRules.Strip(text).Length == 0)
            {
                throw Field("documentId", "This kind works on one document; name it.");
            }

            documentId = TextRules.Strip(text);
        }
        else if (hasDocument)
        {
            throw Field("documentId", "This kind does not take a documentId.");
        }

        JsonObject? options = null;
        var hasOptions = payload.TryGetPropertyValue("options", out var rawOptions) && rawOptions is not null;
        if (OptionsScopedKinds.Contains(kind))
        {
            if (hasOptions)
            {
                options = rawOptions as JsonObject ?? throw Field("options", "options must be a JSON object.");
                options = (JsonObject)options.DeepClone();
            }
        }
        else if (hasOptions)
        {
            throw Field("options", "This kind does not take options.");
        }

        return (kind, documentId, options);
    }

    /// <summary>
    /// A freshly accepted job. The scoping fields come from the route — the
    /// document only after it was resolved against the case, the options only
    /// after they were validated and canonicalised — never the request body.
    /// </summary>
    public static Job NewJob(string kind, string caseId, string createdBy, string? documentId = null, JsonObject? options = null, TimeProvider? clock = null)
    {
        var now = Clock.NowMillis(clock);
        return new Job(Guid.NewGuid().ToString(), caseId, kind, JobStatus.Queued, createdBy, now, now)
        {
            DocumentId = documentId,
            Options = options,
        };
    }

    /// <summary>
    /// The queued-or-running job of this kind — per (case, kind, document) and
    /// per (case, kind, options) — the accept endpoint's idempotency rule.
    /// </summary>
    public static Job? FindActive(IEnumerable<Job> jobs, string kind, string? documentId = null, JsonObject? options = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return jobs.FirstOrDefault(job =>
            job.Kind == kind
            && ActiveStatuses.Contains(job.Status)
            && job.DocumentId == documentId
            && JsonNode.DeepEquals(job.Options, options));
    }

    // ── Transitions ─────────────────────────────────────────────────
    // Each returns a new Job; the store's conditional update (expected
    // status) is what makes them safe under at-least-once delivery.

    /// <summary>Start an attempt: running, one more attempt. A succeeded job refuses.</summary>
    public static Job StartAttempt(Job job, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Status == JobStatus.Succeeded
            ? throw new ValidationException("a succeeded job cannot be re-run")
            : job with { Status = JobStatus.Running, Attempts = job.Attempts + 1, UpdatedAt = Clock.NowMillis(clock) };
    }

    /// <summary>Succeeded with this result, any earlier failure cleared.</summary>
    public static Job Complete(Job job, JsonObject result, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job with { Status = JobStatus.Succeeded, Result = result, Failure = null, UpdatedAt = Clock.NowMillis(clock) };
    }

    /// <summary>Failed, with what the preparer is told.</summary>
    public static Job Fail(Job job, JobFailure failure, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job with { Status = JobStatus.Failed, Failure = failure, UpdatedAt = Clock.NowMillis(clock) };
    }

    /// <summary>Millisecond UTC with a literal Z — the access log's (and the stored jobs') format.</summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The API representation (<c>job_json</c>). <c>caseId</c> is absent — the
    /// client named the case in the URL; <c>failure</c>/<c>result</c> appear only
    /// when set, so a client tells "no result yet" from "empty result".
    /// </summary>
    public static JsonObject ToJson(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var body = new JsonObject
        {
            ["id"] = job.Id,
            ["kind"] = job.Kind,
            ["status"] = job.Status.ToWire(),
            ["createdBy"] = job.CreatedBy,
            ["attempts"] = job.Attempts,
            ["createdAt"] = Timestamp(job.CreatedAt),
            ["updatedAt"] = Timestamp(job.UpdatedAt),
        };
        if (job.DocumentId is not null)
        {
            body["documentId"] = job.DocumentId;
        }

        if (job.Options is not null)
        {
            body["options"] = job.Options.DeepClone();
        }

        if (job.Failure is not null)
        {
            body["failure"] = FailureJson(job.Failure);
        }

        if (job.Result is not null)
        {
            body["result"] = job.Result.DeepClone();
        }

        return body;
    }

    /// <summary>A failure's stored and wire shape.</summary>
    public static JsonObject FailureJson(JobFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new JsonObject { ["category"] = failure.Category, ["message"] = failure.Message };
    }

    // ── The queue contract ──────────────────────────────────────────

    /// <summary>
    /// The exact message body (<c>job_message</c>): identifiers only, in
    /// the contract's key order. <c>documentId</c> appears exactly when the job
    /// carries one, so every earlier kind's version-1 shape is unchanged.
    /// </summary>
    public static JsonObject Message(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var body = new JsonObject
        {
            ["version"] = MessageVersion,
            ["jobId"] = job.Id,
            ["caseId"] = job.CaseId,
            ["kind"] = job.Kind,
        };
        if (job.DocumentId is not null)
        {
            body["documentId"] = job.DocumentId;
        }

        return body;
    }

    /// <summary>
    /// The SQS body, byte for byte what
    /// <c>json.dumps(job_message(job), separators=(",", ":"))</c> produces:
    /// compact, insertion order, ASCII-escaped (every value is an identifier).
    /// </summary>
    public static string MessageBody(Job job) => AsciiJson.Dumps(Message(job), compact: true);

    /// <summary>Inverse of <see cref="Message"/>, refusing anything this service did not mint.</summary>
    public static JobMessage ParseMessage(JsonNode? payload)
    {
        if (payload is not JsonObject body
            || !Json.TryGetWholeNumber(body["version"], out var version)
            || version != MessageVersion)
        {
            throw new ValidationException("job message is not valid");
        }

        var jobId = Json.AsString(body["jobId"]);
        var caseId = Json.AsString(body["caseId"]);
        var kind = Json.AsString(body["kind"]);
        var hasDocument = body.TryGetPropertyValue("documentId", out var rawDocument) && rawDocument is not null;
        var documentId = Json.AsString(rawDocument);
        if (jobId is null || caseId is null || kind is null || (hasDocument && documentId is null))
        {
            throw new ValidationException("job message is not valid");
        }

        return new JobMessage(jobId, caseId, kind, documentId);
    }

    /// <summary>Parse an SQS record body — invalid JSON is the same refusal as an invalid envelope.</summary>
    public static JobMessage ParseMessageBody(string body)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException error)
        {
            throw new ValidationException("job message is not valid", error);
        }

        return ParseMessage(node);
    }

    private static FieldValidationException Field(string field, string message) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { [field] = message });
}
