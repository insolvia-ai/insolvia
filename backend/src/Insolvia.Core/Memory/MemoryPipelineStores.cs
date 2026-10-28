using System.Text.Json.Nodes;
using Insolvia.Core.Cases;
using Insolvia.Core.Jobs;
using Insolvia.Core.Packets;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IJobStore"/>. Keyed by (case, id); the update is
/// the compare-and-swap on the stored status the at-least-once races are
/// decided by — the same rule the Postgres store's <c>WHERE status = :read</c> is.
/// </summary>
public class MemoryJobStore : IJobStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, string Id), Job> _rows = [];

    /// <inheritdoc />
    public Task CreateAsync(Job job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            if (!_rows.TryAdd((job.CaseId, job.Id), job))
            {
                throw new InvalidOperationException("job id already exists in this case");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Job?> GetAsync(string caseId, string jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.GetValueOrDefault((caseId, jobId)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Job> listed = [.. _rows.Where(pair => pair.Key.CaseId == caseId).Select(pair => pair.Value).Order(JobRules.ListOrder)];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public virtual Task<Job?> UpdateAsync(Job job, JobStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            var key = (job.CaseId, job.Id);
            if (!_rows.TryGetValue(key, out var stored) || stored.Status != expectedStatus)
            {
                return Task.FromResult<Job?>(null);
            }

            _rows[key] = job;
            return Task.FromResult<Job?>(job);
        }
    }
}

/// <summary>
/// The in-memory <see cref="IJobSender"/>: records the exact WIRE body, not the
/// job, so a route test asserts what would cross the seam. It does not run the
/// job — the worker's poller does, against a real queue.
/// </summary>
public sealed class MemoryJobSender : IJobSender
{
    private readonly Lock _gate = new();
    private readonly List<string> _bodies = [];

    /// <summary>Every body sent, oldest first.</summary>
    public IReadOnlyList<string> Bodies
    {
        get
        {
            lock (_gate)
            {
                return [.. _bodies];
            }
        }
    }

    /// <summary>Every body sent, parsed.</summary>
    public IReadOnlyList<JsonObject> Messages => [.. Bodies.Select(body => (JsonObject)JsonNode.Parse(body)!)];

    /// <summary>When set, every enqueue throws it — the "queue is down" test seam.</summary>
    public Exception? FailWith { get; set; }

    /// <inheritdoc />
    public Task EnqueueAsync(Job job, CancellationToken cancellationToken = default)
    {
        if (FailWith is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            _bodies.Add(JobRules.MessageBody(job));
        }

        return Task.CompletedTask;
    }

    /// <summary>Take every body sent so far, leaving the queue empty — what a poller's receive-and-delete does.</summary>
    public IReadOnlyList<string> Drain()
    {
        lock (_gate)
        {
            var drained = _bodies.ToList();
            _bodies.Clear();
            return drained;
        }
    }
}

/// <summary>
/// The in-memory <see cref="IPacketStore"/>. Takes the <see cref="MemoryCaseStore"/>
/// it shares a "table" with, because the create is a transaction over both:
/// the packet and the case's pins land together or not at all, under the same
/// conditions the Postgres store's transaction states.
/// </summary>
/// <param name="cases">The case store the pins are written into.</param>
public sealed class MemoryPacketStore(MemoryCaseStore cases) : IPacketStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, string Id), PacketItem> _rows = [];

    /// <summary>The case store this packet store pins into.</summary>
    public MemoryCaseStore Cases { get; } = cases ?? throw new ArgumentNullException(nameof(cases));

    /// <inheritdoc />
    public Task<bool> CreateAsync(PacketItem packet, Case pinnedCase, DateTimeOffset expectedUpdatedAt, bool allowFiled = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(pinnedCase);
        lock (_gate)
        {
            if (_rows.ContainsKey((packet.CaseId, packet.Id)))
            {
                throw new InvalidOperationException("packet id already exists in this case");
            }

            return Task.FromResult(Cases.TryPin(pinnedCase, expectedUpdatedAt, allowFiled, () => _rows[(packet.CaseId, packet.Id)] = packet));
        }
    }

    /// <inheritdoc />
    public Task<PacketItem?> GetAsync(string caseId, string packetId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.GetValueOrDefault((caseId, packetId)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PacketItem>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<PacketItem> listed = [.. _rows.Where(pair => pair.Key.CaseId == caseId).Select(pair => pair.Value).Order(PacketItems.NewestFirst)];
            return Task.FromResult(listed);
        }
    }
}

/// <summary>
/// Answers every review with the findings it was built with and records each
/// document it was shown — how tests assert what would leave for the model API.
/// </summary>
/// <param name="findings">The raw findings to answer with.</param>
/// <param name="model">The model id to report.</param>
public sealed class ScriptedReviewModel(IReadOnlyList<JsonObject>? findings = null, string model = "scripted") : IReviewModel
{
    private readonly List<string> _documents = [];

    /// <summary>Every document shown, oldest first.</summary>
    public IReadOnlyList<string> Documents => _documents;

    /// <inheritdoc />
    public Task<ReviewModelResult> ReviewAsync(string document, CancellationToken cancellationToken = default)
    {
        _documents.Add(document);
        var raw = new JsonObject { ["findings"] = new JsonArray([.. (findings ?? []).Select(finding => (JsonNode)finding.DeepClone())]) };
        return Task.FromResult(new ReviewModelResult(model, raw));
    }
}

/// <summary>
/// Answers every extraction with the raw output it was built with and records
/// each request — the bytes, the media type, the exact prompt and schema.
/// </summary>
/// <param name="raw">The raw output to answer with; an empty object when none.</param>
/// <param name="model">The model id to report.</param>
public sealed class ScriptedExtractionModel(JsonObject? raw = null, string model = "scripted") : IExtractionModel
{
    private readonly List<ExtractionRequest> _requests = [];

    /// <summary>Every request shown, oldest first.</summary>
    public IReadOnlyList<ExtractionRequest> Requests => _requests;

    /// <inheritdoc />
    public Task<ExtractionModelResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        _requests.Add(request);
        return Task.FromResult(new ExtractionModelResult(model, (JsonObject)(raw ?? []).DeepClone()));
    }
}
