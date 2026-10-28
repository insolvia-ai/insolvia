using System.Text.Json.Nodes;
using Amazon;
using Amazon.KeyManagementService;
using Amazon.S3;
using Amazon.SQS;
using Amazon.SQS.Model;
using Insolvia.Aws.Documents;
using Insolvia.Aws.Sealing;
using Insolvia.Core.Ports;
using Insolvia.Data;
using Insolvia.Data.AccessLogging;
using Insolvia.Data.Cases;
using Insolvia.Data.Connections;
using Insolvia.Data.Pagination;
using Insolvia.Worker.Anthropic;
using Insolvia.Worker.Workers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Insolvia.Worker;

/// <summary>The ports one message is handled over — what a request scope is to the API.</summary>
/// <param name="Jobs">The job store.</param>
/// <param name="Cases">The case store.</param>
/// <param name="Debtors">The debtor store.</param>
/// <param name="Entities">The collections.</param>
/// <param name="Packets">The packet store.</param>
/// <param name="Documents">The document store.</param>
/// <param name="Candidates">The candidate store.</param>
/// <param name="AccessLog">The access log.</param>
/// <param name="TaxIds">The sealed tax ids.</param>
public sealed record WorkerStores(
    IJobStore Jobs,
    ICaseStore Cases,
    IDebtorStore Debtors,
    ICaseEntityStore Entities,
    IPacketStore Packets,
    IDocumentStore Documents,
    ICandidateStore Candidates,
    IAccessLog AccessLog,
    ITaxIdStore TaxIds);

/// <summary>
/// The worker's composition (entrypoints/worker_lambda.py and worker_poller.py):
/// the full registry around <see cref="JobDispatch.PureWorkers"/>, over the
/// stores one message sees. Process-wide: the S3 bucket, the KMS cipher (the
/// worker role holds Decrypt on the case key for the tax-id context and
/// nothing more) and the model adapters — NOT hard-required: without
/// <c>ANTHROPIC_API_KEY</c> the review and extraction fail honestly with
/// <c>not_configured</c> and every other kind runs.
/// </summary>
/// <param name="Blobs">The document bucket.</param>
/// <param name="TaxIdCipher">The tax-id cipher.</param>
/// <param name="ReviewModel">The review model, or null.</param>
/// <param name="ExtractionModel">The extraction model, or null.</param>
public sealed record WorkerComposition(IDocumentBlobStore Blobs, ITaxIdCipher TaxIdCipher, IReviewModel? ReviewModel, IExtractionModel? ExtractionModel)
{
    /// <summary>The clock.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The log.</summary>
    public JobLog Log { get; init; } = JobLog.Console;

    /// <summary>The registry over one message's stores.</summary>
    public IReadOnlyDictionary<string, JobWorker> Workers(WorkerStores stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        return new Dictionary<string, JobWorker>(JobDispatch.PureWorkers, StringComparer.Ordinal)
        {
            [Core.Jobs.JobRules.PacketAssemblyKind] = PacketAssemblyWorker.For(new PacketAssemblyDeps(stores.Cases, stores.Debtors, stores.Entities, stores.Packets, Blobs, stores.AccessLog, stores.TaxIds, TaxIdCipher) { Clock = Clock, Log = Log }),
            [Core.Jobs.JobRules.PetitionReviewKind] = PetitionReview.For(new PetitionReviewDeps(stores.Cases, stores.Debtors, stores.Entities, stores.Packets, stores.AccessLog, stores.TaxIds, TaxIdCipher, ReviewModel) { Clock = Clock, Log = Log }),
            [Core.Jobs.JobRules.DocumentExtractionKind] = DocumentExtraction.For(new DocumentExtractionDeps(stores.Cases, stores.Documents, Blobs, stores.Candidates, stores.AccessLog, ExtractionModel) { Clock = Clock, Log = Log }),
        };
    }

    /// <summary>One SQS event over <paramref name="stores"/>.</summary>
    public Task HandleAsync(JsonNode? sqsEvent, WorkerStores stores, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        return JobDispatch.HandleSqsEventAsync(sqsEvent, stores.Jobs, Workers(stores), Log, Clock, cancellationToken);
    }
}

/// <summary>
/// The deployed shape: Postgres (as the <c>worker</c> role), the S3 bucket, KMS
/// and — when the key is set — Anthropic. Refuses to start without what every
/// job needs (the database, the bucket), discovered by the deploy rather than
/// by a stuck job.
/// </summary>
public sealed class WorkerRuntime : IAsyncDisposable
{
    private static readonly CursorCodec Cursors = CursorCodec.FromEnvironment(_ => null);
    private readonly NpgsqlDataSource _dataSource;
    private readonly DbContextOptions<InsolviaDbContext> _options;

    private WorkerRuntime(NpgsqlDataSource dataSource, WorkerComposition composition)
    {
        _dataSource = dataSource;
        var builder = new DbContextOptionsBuilder<InsolviaDbContext>();
        builder.UseInsolviaNpgsql(dataSource);
        _options = builder.Options;
        Composition = composition;
    }

    /// <summary>The composition.</summary>
    public WorkerComposition Composition { get; }

    /// <summary>Builds the runtime from the environment, or throws naming what is missing.</summary>
    public static WorkerRuntime FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        string? Optional(string name) => read(name) is { Length: > 0 } value ? value : null;
        var bucket = Optional("CASE_DOCUMENT_BUCKET") ?? throw new InvalidOperationException("CASE_DOCUMENT_BUCKET must be set for the worker");
        var caseKey = Optional("INSOLVIA_KMS_CASE_KEY_ALIAS")
            ?? (Optional("CASE_TABLE_NAME") is { } table ? KeyAliases.CaseKey(table) : null)
            ?? throw new InvalidOperationException("INSOLVIA_KMS_CASE_KEY_ALIAS (or CASE_TABLE_NAME, which derives it) must be set for the worker");
        var settings = DatabaseSettings.FromEnvironment(read, applicationName: "insolvia-worker");
        var region = Optional("AWS_REGION");
        var kms = region is null ? new AmazonKeyManagementServiceClient() : new AmazonKeyManagementServiceClient(RegionEndpoint.GetBySystemName(region));
        var s3 = new AmazonS3Client(S3DocumentBlobStore.ClientConfig());
        IReviewModel? review = null;
        IExtractionModel? extraction = null;
        if (Optional("ANTHROPIC_API_KEY") is { } key)
        {
            var client = AnthropicPolicy.NewClient(key);
            review = new AnthropicReviewModel(client);
            extraction = new AnthropicExtractionModel(client);
        }

        return new WorkerRuntime(
            InsolviaDataSource.Create(settings),
            new WorkerComposition(new S3DocumentBlobStore(s3, bucket), new KmsTaxIdCipher(kms, caseKey), review, extraction));
    }

    /// <summary>One SQS event, over one fresh context — the message's unit of work.</summary>
    public async Task HandleAsync(JsonNode? sqsEvent, CancellationToken cancellationToken = default)
    {
        var db = new InsolviaDbContext(_options);
        await using (db.ConfigureAwait(false))
        {
            await Composition.HandleAsync(sqsEvent, Stores(db), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The local poller (worker_poller.py; ADR 0018's local story): long-polls
    /// this machine's dev queue and feeds each message, as the Lambda event
    /// shape at batch size 1, through the SAME handler — delete on a clean
    /// return, leave it for redelivery when the handler throws (exactly what an
    /// erroring Lambda does; maxReceiveCount still walks a poison message to the DLQ).
    /// </summary>
    public async Task PollAsync(IAmazonSQS sqs, string queueUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sqs);
        Composition.Log.Info("worker poller listening", ("queue", queueUrl));
        while (!cancellationToken.IsCancellationRequested)
        {
            ReceiveMessageResponse response;
            try
            {
                response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 20 }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var message in response.Messages ?? [])
            {
                try
                {
                    await HandleAsync(new JsonObject { ["Records"] = new JsonArray(new JsonObject { ["body"] = message.Body }) }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Composition.Log.Error("job message failed; leaving for redelivery", ("exception", error.GetType().Name));
                    continue;
                }

                await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private static WorkerStores Stores(InsolviaDbContext db) => new(
        new PostgresJobStore(db),
        new PostgresCaseStore(db, Cursors),
        new PostgresDebtorStore(db),
        new PostgresCaseEntityStore(db),
        new PostgresPacketStore(db),
        new PostgresDocumentStore(db),
        new PostgresCandidateStore(db),
        new PostgresAccessLog(db),
        new PostgresTaxIdStore(db));
}
