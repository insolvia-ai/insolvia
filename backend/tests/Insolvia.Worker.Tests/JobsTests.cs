using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Jobs;
using Insolvia.Core.Memory;

namespace Insolvia.Worker.Tests;

/// <summary>
/// test_jobs.py, translated: acceptance, transitions, the stored and wire
/// shapes, the QUEUE CONTRACT (the pinned seam), and the dispatch loop's
/// at-least-once semantics over the memory store.
/// </summary>
public sealed class JobsTests
{
    private const string Case = "00000000-0000-4000-8000-00000000ca5e";
    private const string Alice = "00000000-0000-4000-8000-00000000a11c";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Job MakeJob() => JobRules.NewJob(JobRules.EchoKind, Case, Alice);

    private static JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();

    // ── Acceptance validation ───────────────────────────────────────

    [Fact]
    public void A_registered_kind_is_accepted() =>
        Assert.Equal(("echo", (string?)null, (JsonObject?)null), JobRules.ParseAcceptance(Body("""{"kind": "echo"}""")));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"kind": "assemble_packet_from_the_future"}""")]
    [InlineData("""{"kind": 7}""")]
    [InlineData("""{"kind": null}""")]
    [InlineData("""{"kind": "document_extraction"}""")]
    [InlineData("""{"kind": "document_extraction", "documentId": "  "}""")]
    [InlineData("""{"kind": "document_extraction", "documentId": 7}""")]
    [InlineData("""{"kind": "echo", "documentId": "doc-1"}""")]
    [InlineData("""{"kind": "echo", "options": {"draftWatermark": true}}""")]
    [InlineData("""{"kind": "packet_assembly", "options": "not-an-object"}""")]
    public void Anything_but_a_registered_kind_is_rejected(string payload) =>
        Assert.Throws<FieldValidationException>(() => JobRules.ParseAcceptance(Body(payload)));

    [Fact]
    public void An_options_scoped_kind_accepts_and_returns_its_options()
    {
        var (kind, document, options) = JobRules.ParseAcceptance(Body("""{"kind": "packet_assembly", "options": {"draftWatermark": true}}"""));
        Assert.Equal("packet_assembly", kind);
        Assert.Null(document);
        Assert.True(JsonNode.DeepEquals(Body("""{"draftWatermark": true}"""), options));
        Assert.Null(JobRules.ParseAcceptance(Body("""{"kind": "packet_assembly"}""")).Options);
    }

    [Fact]
    public void A_document_scoped_kind_requires_and_returns_its_document() =>
        Assert.Equal("doc-1", JobRules.ParseAcceptance(Body("""{"kind": "document_extraction", "documentId": "doc-1"}""")).DocumentId);

    [Fact]
    public void Every_pure_worker_is_an_acceptable_kind()
    {
        Assert.Subset(JobRules.Kinds.ToHashSet(), JobDispatch.PureWorkers.Keys.ToHashSet());
        Assert.Contains("packet_assembly", JobRules.Kinds);
        Assert.Contains("petition_review", JobRules.Kinds);
        Assert.Contains("document_extraction", JobRules.Kinds);
    }

    // ── Identity and transitions ────────────────────────────────────

    [Fact]
    public void A_new_job_is_queued_with_no_attempts()
    {
        var job = MakeJob();
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.Failure);
        Assert.Null(job.Result);
        Assert.Equal(job.CreatedAt, job.UpdatedAt);
    }

    [Fact]
    public void Start_attempt_counts_and_runs()
    {
        var started = JobRules.StartAttempt(MakeJob());
        Assert.Equal(JobStatus.Running, started.Status);
        Assert.Equal(1, started.Attempts);
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Failed)]
    public void Every_non_terminal_status_can_start_an_attempt(JobStatus status) =>
        Assert.Equal(JobStatus.Running, JobRules.StartAttempt(MakeJob() with { Status = status }).Status);

    [Fact]
    public void A_succeeded_job_cannot_be_rerun() =>
        Assert.Throws<ValidationException>(() => JobRules.StartAttempt(MakeJob() with { Status = JobStatus.Succeeded }));

    [Fact]
    public void Complete_clears_any_earlier_failure()
    {
        var job = MakeJob() with { Status = JobStatus.Running, Failure = new JobFailure("internal", "boom") };
        var done = JobRules.Complete(job, new JsonObject { ["echo"] = job.Id });
        Assert.Equal(JobStatus.Succeeded, done.Status);
        Assert.Null(done.Failure);
        Assert.Equal(job.Id, done.Result!["echo"]!.GetValue<string>());
    }

    [Fact]
    public void Find_active_matches_kind_and_liveness()
    {
        var queued = MakeJob();
        Assert.Same(queued, JobRules.FindActive([queued], "echo"));
        Assert.Null(JobRules.FindActive([MakeJob() with { Status = JobStatus.Failed }], "echo"));
        Assert.Null(JobRules.FindActive([MakeJob() with { Status = JobStatus.Succeeded }], "echo"));
        Assert.Null(JobRules.FindActive([queued], "another-kind"));
    }

    [Fact]
    public void Find_active_scopes_document_kinds_per_document()
    {
        var extraction = MakeJob() with { Kind = "document_extraction", DocumentId = "doc-1" };
        Assert.Same(extraction, JobRules.FindActive([extraction], "document_extraction", "doc-1"));
        Assert.Null(JobRules.FindActive([extraction], "document_extraction", "doc-2"));
        Assert.Null(JobRules.FindActive([extraction], "document_extraction"));
    }

    [Fact]
    public void Find_active_scopes_options_kinds_per_options()
    {
        var draft = MakeJob() with { Kind = "packet_assembly", Options = Body("""{"draftWatermark": true}""") };
        Assert.Same(draft, JobRules.FindActive([draft], "packet_assembly", options: Body("""{"draftWatermark": true}""")));
        Assert.Null(JobRules.FindActive([draft], "packet_assembly", options: null));
        Assert.Null(JobRules.FindActive([draft], "packet_assembly", options: Body("""{"draftWatermark": false}""")));
    }

    // ── The wire shapes ─────────────────────────────────────────────

    [Fact]
    public void Job_json_carries_options_failure_and_result_only_when_set()
    {
        var plain = JobRules.ToJson(MakeJob());
        Assert.False(plain.ContainsKey("options"));
        Assert.False(plain.ContainsKey("failure"));
        Assert.False(plain.ContainsKey("result"));
        Assert.False(plain.ContainsKey("caseId"));
        Assert.True(JsonNode.DeepEquals(Body("""{"draftWatermark": true}"""), JobRules.ToJson(MakeJob() with { Options = Body("""{"draftWatermark": true}""") })["options"]));
        var failed = JobRules.Fail(MakeJob(), new JobFailure("job_error", "The petition has no debtor."));
        Assert.Equal("""{"category":"job_error","message":"The petition has no debtor."}""", JobRules.ToJson(failed)["failure"]!.ToJsonString());
    }

    [Fact]
    public void Job_json_stamps_milliseconds_with_a_literal_z()
    {
        var at = new DateTimeOffset(2026, 10, 28, 9, 1, 2, 345, TimeSpan.Zero);
        var json = JobRules.ToJson(MakeJob() with { CreatedAt = at, UpdatedAt = at });
        Assert.Equal("2026-10-28T09:01:02.345Z", json["createdAt"]!.GetValue<string>());
    }

    // ── The queue contract (the pinned seam) ────────────────────────

    [Fact]
    public void The_wire_message_is_exactly_ids_and_a_version()
    {
        // THE contract pin: this literal is what crosses SQS, byte for byte
        // Python's json.dumps(job_message(job), separators=(",", ":")).
        var job = MakeJob();
        Assert.Equal($$"""{"version":1,"jobId":"{{job.Id}}","caseId":"{{Case}}","kind":"echo"}""", JobRules.MessageBody(job));
        Assert.Equal(1, JobRules.MessageVersion);
    }

    [Fact]
    public void The_wire_message_carries_a_document_id_only_when_the_job_does()
    {
        var job = MakeJob() with { Kind = "document_extraction", DocumentId = "doc-1" };
        Assert.Equal($$"""{"version":1,"jobId":"{{job.Id}}","caseId":"{{Case}}","kind":"document_extraction","documentId":"doc-1"}""", JobRules.MessageBody(job));
    }

    [Fact]
    public void The_message_round_trips()
    {
        var job = MakeJob();
        Assert.Equal(new JobMessage(job.Id, Case, "echo"), JobRules.ParseMessageBody(JobRules.MessageBody(job)));
        var extraction = job with { Kind = "document_extraction", DocumentId = "doc-1" };
        Assert.Equal(new JobMessage(job.Id, Case, "document_extraction", "doc-1"), JobRules.ParseMessageBody(JobRules.MessageBody(extraction)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"a string\"")]
    [InlineData("{}")]
    [InlineData("""{"version": 2, "jobId": "j", "caseId": "c", "kind": "echo"}""")]
    [InlineData("""{"version": 1, "jobId": 7, "caseId": "c", "kind": "echo"}""")]
    [InlineData("""{"version": 1, "jobId": "j", "kind": "echo"}""")]
    [InlineData("""{"version": 1, "jobId": "j", "caseId": "c", "kind": "echo", "documentId": 3}""")]
    [InlineData("not json")]
    public void Anything_this_service_did_not_mint_is_rejected(string payload) =>
        Assert.Throws<ValidationException>(() => JobRules.ParseMessageBody(payload));

    // ── The dispatch loop ───────────────────────────────────────────

    private static async Task<(MemoryJobStore Store, Job Job)> AcceptedAsync(MemoryJobStore? store = null)
    {
        store ??= new MemoryJobStore();
        var job = MakeJob();
        await store.CreateAsync(job, Ct);
        return (store, job);
    }

    private static JobMessage MessageFor(Job job) => JobRules.ParseMessageBody(JobRules.MessageBody(job));

    private static Dictionary<string, JobWorker> Only(JobWorker worker) => new(StringComparer.Ordinal) { ["echo"] = worker };

    [Fact]
    public async Task A_delivered_job_runs_to_succeeded()
    {
        var (store, job) = await AcceptedAsync();
        await JobDispatch.RunJobAsync(MessageFor(job), store, JobDispatch.PureWorkers, new RecordingJobLog(), cancellationToken: Ct);
        var stored = (await store.GetAsync(Case, job.Id, Ct))!;
        Assert.Equal(JobStatus.Succeeded, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal($$"""{"echo":"{{job.Id}}"}""", stored.Result!.ToJsonString());
    }

    [Fact]
    public async Task A_redelivery_of_a_finished_job_is_a_no_op()
    {
        var (store, job) = await AcceptedAsync();
        await JobDispatch.RunJobAsync(MessageFor(job), store, JobDispatch.PureWorkers, new RecordingJobLog(), cancellationToken: Ct);
        var first = await store.GetAsync(Case, job.Id, Ct);
        await JobDispatch.RunJobAsync(MessageFor(job), store, JobDispatch.PureWorkers, new RecordingJobLog(), cancellationToken: Ct);
        Assert.Same(first, await store.GetAsync(Case, job.Id, Ct));
    }

    [Fact]
    public async Task A_message_with_no_record_is_dropped_not_raised()
    {
        var store = new MemoryJobStore();
        var stray = MakeJob();
        var log = new RecordingJobLog();
        await JobDispatch.RunJobAsync(MessageFor(stray), store, JobDispatch.PureWorkers, log, cancellationToken: Ct);
        Assert.Null(await store.GetAsync(Case, stray.Id, Ct));
        Assert.Contains(log.Records, record => record.Message == "job message has no record; dropping");
    }

    [Fact]
    public async Task A_deterministic_failure_is_terminal_and_preparer_readable()
    {
        var (store, job) = await AcceptedAsync();
        await JobDispatch.RunJobAsync(MessageFor(job), store, Only((_, _) => throw new JobException("The case has no debtor yet.", "case_incomplete")), new RecordingJobLog(), cancellationToken: Ct);
        var stored = (await store.GetAsync(Case, job.Id, Ct))!;
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Equal(new JobFailure("case_incomplete", "The case has no debtor yet."), stored.Failure);
    }

    [Fact]
    public async Task An_infrastructure_failure_marks_failed_and_reraises()
    {
        var (store, job) = await AcceptedAsync();
        var log = new RecordingJobLog();
        await Assert.ThrowsAsync<IOException>(() => JobDispatch.RunJobAsync(MessageFor(job), store, Only((_, _) => throw new IOException("socket closed")), log, cancellationToken: Ct));
        var stored = (await store.GetAsync(Case, job.Id, Ct))!;
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Equal("internal", stored.Failure!.Category);

        // GLBA: the exception's text stays out of the record — and out of the log.
        Assert.DoesNotContain("socket", stored.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Records, record => record.Fields.Values.Any(value => value?.ToString()?.Contains("socket", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task A_retry_after_an_infrastructure_failure_can_still_succeed()
    {
        var (store, job) = await AcceptedAsync();
        await Assert.ThrowsAsync<IOException>(() => JobDispatch.RunJobAsync(MessageFor(job), store, Only((_, _) => throw new IOException("socket closed")), new RecordingJobLog(), cancellationToken: Ct));
        await JobDispatch.RunJobAsync(MessageFor(job), store, JobDispatch.PureWorkers, new RecordingJobLog(), cancellationToken: Ct);
        var stored = (await store.GetAsync(Case, job.Id, Ct))!;
        Assert.Equal(JobStatus.Succeeded, stored.Status);
        Assert.Equal(2, stored.Attempts);
    }

    [Fact]
    public async Task An_unknown_kind_fails_the_job_and_raises_for_redelivery()
    {
        var (store, job) = await AcceptedAsync();
        await Assert.ThrowsAsync<ValidationException>(() => JobDispatch.RunJobAsync(MessageFor(job), store, new Dictionary<string, JobWorker>(), new RecordingJobLog(), cancellationToken: Ct));
        var stored = (await store.GetAsync(Case, job.Id, Ct))!;
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Equal("unknown_kind", stored.Failure!.Category);
    }

    [Fact]
    public async Task Losing_the_start_race_runs_nothing()
    {
        var (racing, job) = await AcceptedAsync(new RacingStore());
        var calls = new List<string>();
        await JobDispatch.RunJobAsync(
            MessageFor(job),
            racing,
            Only((running, _) =>
            {
                calls.Add(running.Id);
                return Task.FromResult(new JsonObject());
            }),
            new RecordingJobLog(),
            cancellationToken: Ct);
        Assert.Empty(calls);
        Assert.Equal(JobStatus.Queued, (await racing.GetAsync(Case, job.Id, Ct))!.Status);
    }

    [Fact]
    public async Task Two_deliveries_of_one_message_run_the_worker_exactly_once()
    {
        // The real race, not a scripted loss: two concurrent deliveries read
        // the same queued row; the status compare-and-swap picks one winner.
        var (store, job) = await AcceptedAsync(new SlowReadStore());
        var runs = 0;
        var worker = Only(async (_, token) =>
        {
            Interlocked.Increment(ref runs);
            await Task.Delay(10, token);
            return new JsonObject();
        });
        await Task.WhenAll(
            JobDispatch.RunJobAsync(MessageFor(job), store, worker, new RecordingJobLog(), cancellationToken: Ct),
            JobDispatch.RunJobAsync(MessageFor(job), store, worker, new RecordingJobLog(), cancellationToken: Ct));
        Assert.Equal(1, runs);
        Assert.Equal(JobStatus.Succeeded, (await store.GetAsync(Case, job.Id, Ct))!.Status);
    }

    [Fact]
    public async Task The_sqs_event_shape_reaches_run_job()
    {
        var (store, job) = await AcceptedAsync();
        var sqsEvent = new JsonObject { ["Records"] = new JsonArray(new JsonObject { ["body"] = JobRules.MessageBody(job) }) };
        await JobDispatch.HandleSqsEventAsync(sqsEvent, store, JobDispatch.PureWorkers, new RecordingJobLog(), cancellationToken: Ct);
        Assert.Equal(JobStatus.Succeeded, (await store.GetAsync(Case, job.Id, Ct))!.Status);
    }

    [Fact]
    public async Task An_unparseable_body_raises_toward_the_dlq() =>
        await Assert.ThrowsAsync<ValidationException>(() => JobDispatch.HandleSqsEventAsync(
            JsonNode.Parse("""{"Records": [{"body": "{\"version\": 99}"}]}"""),
            new MemoryJobStore(),
            JobDispatch.PureWorkers,
            new RecordingJobLog(),
            cancellationToken: Ct));

    [Fact]
    public async Task The_echo_worker_reads_no_case_data()
    {
        var job = MakeJob();
        Assert.Equal($$"""{"echo":"{{job.Id}}"}""", (await JobDispatch.RunEchoAsync(job, Ct)).ToJsonString());
    }

    /// <summary>The other delivery always wins the queued→running write.</summary>
    private sealed class RacingStore : MemoryJobStore
    {
        public override Task<Job?> UpdateAsync(Job job, JobStatus expectedStatus, CancellationToken cancellationToken = default) =>
            job.Status == JobStatus.Running && expectedStatus == JobStatus.Queued ? Task.FromResult<Job?>(null) : base.UpdateAsync(job, expectedStatus, cancellationToken);
    }

    /// <summary>Holds every claim long enough that both deliveries read the row before either writes.</summary>
    private sealed class SlowReadStore : MemoryJobStore
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public override async Task<Job?> UpdateAsync(Job job, JobStatus expectedStatus, CancellationToken cancellationToken = default)
        {
            if (job.Status == JobStatus.Running && expectedStatus == JobStatus.Queued)
            {
                if (Interlocked.Increment(ref _reads) == 2)
                {
                    _bothRead.SetResult();
                }

                await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            return await base.UpdateAsync(job, expectedStatus, cancellationToken);
        }
    }
}
