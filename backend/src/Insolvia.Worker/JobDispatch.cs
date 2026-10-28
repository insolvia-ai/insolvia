using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Jobs;
using Insolvia.Core.Ports;

namespace Insolvia.Worker;

/// <summary>A worker: a job in, a JSON-shaped result out. Tests call these directly — no Lambda, no queue.</summary>
public delegate Task<JsonObject> JobWorker(Job job, CancellationToken cancellationToken);

/// <summary>
/// The dispatch loop (core/jobs.py's <c>run_job</c> / <c>handle_sqs_event</c>;
/// ADR 0018), shared by the Lambda handler and the local poller so a laptop
/// runs the exact consume path the cloud runs.
/// </summary>
/// <remarks>
/// <para>The state machine, every edge a conditional write on the status read:</para>
/// <list type="bullet">
/// <item><c>queued|running|failed → running</c> (start an attempt; the CAS loser runs nothing);</item>
/// <item><c>running → succeeded</c> (the worker answered);</item>
/// <item><c>running → failed</c>, swallowed (a <see cref="JobException"/>: deterministic, no retry);</item>
/// <item><c>running → failed</c>, re-thrown (anything else: SQS redelivers, and the
/// next attempt may still succeed; after maxReceiveCount the DLQ alarms);</item>
/// <item><c>* → failed</c> <c>unknown_kind</c>, re-thrown (the deploy window).</item>
/// </list>
/// <para>A <c>succeeded</c> job is immutable — a redelivery is a no-op — and a
/// message with no row is stray: logged and dropped, never retried.</para>
/// </remarks>
public static class JobDispatch
{
    /// <summary>The generic, preparer-safe message for an infrastructure failure — the exception's text stays in the log.</summary>
    public const string InternalMessage = "The job failed unexpectedly and will be retried.";

    /// <summary>The dependency-free workers (Python's <c>WORKERS</c>).</summary>
    public static IReadOnlyDictionary<string, JobWorker> PureWorkers { get; } = new Dictionary<string, JobWorker>(StringComparer.Ordinal)
    {
        [JobRules.EchoKind] = RunEchoAsync,
    };

    /// <summary>The walking skeleton's worker: proves the loop end to end; reads no case data.</summary>
    public static Task<JsonObject> RunEchoAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Task.FromResult(new JsonObject { ["echo"] = job.Id });
    }

    /// <summary>Execute one delivered message — at-least-once safe by construction (see the type's remarks).</summary>
    public static async Task RunJobAsync(
        JobMessage message,
        IJobStore store,
        IReadOnlyDictionary<string, JobWorker> workers,
        JobLog? log = null,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(workers);
        log ??= JobLog.Console;
        var job = await store.GetAsync(message.CaseId, message.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            // Retrying a message that can never resolve only poisons the queue.
            log.Error("job message has no record; dropping", ("job_id", message.JobId), ("case_id", message.CaseId));
            return;
        }

        if (job.Status == JobStatus.Succeeded)
        {
            return;
        }

        if (!workers.TryGetValue(job.Kind, out var worker))
        {
            // The API accepted a kind before the worker image carrying it
            // deployed: an honest status now, and the redelivery after the
            // deploy can still succeed.
            await store.UpdateAsync(
                JobRules.Fail(job, new JobFailure("unknown_kind", "This job type is not available yet. It will be retried."), clock),
                job.Status,
                cancellationToken).ConfigureAwait(false);
            throw new ValidationException($"no worker registered for kind '{job.Kind}'");
        }

        var started = await store.UpdateAsync(JobRules.StartAttempt(job, clock), job.Status, cancellationToken).ConfigureAwait(false);
        if (started is null)
        {
            log.Info("job attempt lost the start race", ("job_id", job.Id));
            return;
        }

        JsonObject result;
        try
        {
            result = await worker(started, cancellationToken).ConfigureAwait(false);
        }
        catch (JobException error)
        {
            await store.UpdateAsync(JobRules.Fail(started, new JobFailure(error.Category, error.Message), clock), JobStatus.Running, cancellationToken).ConfigureAwait(false);
            log.Info("job failed deterministically", ("job_id", job.Id), ("category", error.Category));
            return;
        }
        catch (Exception error)
        {
            // GLBA: a stack trace over case data is case data — the record
            // says something generic; the log names the exception's type.
            await store.UpdateAsync(JobRules.Fail(started, new JobFailure("internal", InternalMessage), clock), JobStatus.Running, CancellationToken.None).ConfigureAwait(false);
            log.Error("job failed unexpectedly", ("job_id", job.Id), ("exception", error.GetType().Name));
            throw;
        }

        await store.UpdateAsync(JobRules.Complete(started, result, clock), JobStatus.Running, cancellationToken).ConfigureAwait(false);
        log.Info("job succeeded", ("job_id", job.Id), ("attempts", started.Attempts));
    }

    /// <summary>
    /// One SQS→Lambda event (<c>{"Records": [{"body": …}]}</c>). Batch size 1,
    /// so the loop is a formality, but it throws on the first failing record
    /// so the whole delivery retries. A body that does not parse throws: it
    /// will never parse, so retries walk it to the DLQ, under an alarm.
    /// </summary>
    public static async Task HandleSqsEventAsync(
        JsonNode? sqsEvent,
        IJobStore store,
        IReadOnlyDictionary<string, JobWorker> workers,
        JobLog? log = null,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        if (sqsEvent?["Records"] is not JsonArray records)
        {
            return;
        }

        foreach (var record in records)
        {
            var body = Json.AsString(record?["body"]) ?? throw new ValidationException("job message is not valid");
            await RunJobAsync(JobRules.ParseMessageBody(body), store, workers, log, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The raw Lambda payload, parsed — an event that is not JSON is the same loud refusal.</summary>
    public static JsonNode? ParseEvent(Stream payload)
    {
        try
        {
            return JsonNode.Parse(payload);
        }
        catch (JsonException error)
        {
            throw new ValidationException("the SQS event is not JSON", error);
        }
    }
}
