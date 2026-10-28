using System.Text;
using System.Text.Json.Nodes;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Insolvia.Filing;

/// <summary>
/// The filing worker's two hosts in one binary (from
/// <c>entrypoints/filing_lambda.py</c> and <c>filing_poller.py</c>):
/// <list type="bullet">
/// <item><b>Lambda</b> (<c>AWS_LAMBDA_RUNTIME_API</c> set) — the SQS event
/// source mapping's target, batch size 1. Composed once per cold start; each
/// record runs through <see cref="FilingWorker.RunAsync"/>. An exception
/// escapes only from before a filing record exists, which SQS may safely
/// retry: every later attempt resumes through the record and never submits twice.</item>
/// <item><b>The local poller</b> (<c>dotnet run --project src/Insolvia.Filing</c>,
/// <c>INSOLVIA_ENV=local</c>) — long-polls <c>FILING_QUEUE_URL</c> and runs
/// each job on the laptop: consume, dispatch, delete at batch size 1; a run
/// that throws leaves its message for redelivery.</item>
/// </list>
/// </summary>
public static class FilingProgram
{
    /// <summary>The entry point.</summary>
    public static async Task<int> Main()
    {
        var config = FilingConfig.Load(Environment.GetEnvironmentVariable);
        var log = new ConsoleFilingLog();
        using var services = FilingComposition.Services(config, Environment.GetEnvironmentVariable, packets: null, log);
        if (Environment.GetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API") is not null)
        {
            var handler = async (Stream input, ILambdaContext context) =>
            {
                using var reader = new StreamReader(input, Encoding.UTF8);
                var answer = await HandleAsync(await reader.ReadToEndAsync().ConfigureAwait(false), (body, token) => RunScopedAsync(services, config, body, token), log, CancellationToken.None).ConfigureAwait(false);
                return (Stream)new MemoryStream(Encoding.UTF8.GetBytes(answer.ToJsonString()));
            };
            await LambdaBootstrapBuilder.Create(handler).Build().RunAsync().ConfigureAwait(false);
            return 0;
        }

        return await PollAsync(services, config, log, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>One SQS event: each record's body through <paramref name="run"/>, the outcomes returned.</summary>
    public static async Task<JsonObject> HandleAsync(string eventJson, Func<string, CancellationToken, Task<FilingResult>> run, IFilingLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(log);
        var outcomes = new JsonArray();
        var records = JsonNode.Parse(eventJson)?["Records"] as JsonArray ?? [];
        foreach (var record in records)
        {
            var body = record?["body"] is JsonValue text && text.TryGetValue<string>(out var value) ? value : string.Empty;
            var result = await run(body, cancellationToken).ConfigureAwait(false);
            log.Write("INFO", "filing job done", ("outcome", result.Outcome), ("filing_id", result.FilingId), ("reason", result.Reason));
            outcomes.Add(result.Outcome);
        }

        return new JsonObject { ["outcomes"] = outcomes };
    }

    /// <summary>One message through the worker, over its own scope (one database context per message).</summary>
    public static async Task<FilingResult> RunScopedAsync(IServiceProvider services, FilingConfig config, string body, CancellationToken cancellationToken)
    {
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await FilingWorker.RunAsync(body, FilingComposition.Deps(scope.ServiceProvider, config), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> PollAsync(IServiceProvider services, FilingConfig config, ConsoleFilingLog log, CancellationToken cancellationToken)
    {
        if (config.Environment != "local")
        {
            throw new InvalidOperationException("the poller is the local worker; deployed runs are the Lambda");
        }

        var queue = config.FilingQueueUrl ?? throw new InvalidOperationException("FILING_QUEUE_URL must be set for the local poller");
        using var sqs = new AmazonSQSClient();
        log.Write("INFO", "filing poller listening");
        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest(queue) { MaxNumberOfMessages = 1, WaitTimeSeconds = 20 }, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages ?? [])
            {
                FilingResult result;
                try
                {
                    result = await RunScopedAsync(services, config, message.Body, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.Write("ERROR", "filing job raised; leaving it for redelivery", ("error", error.GetType().Name));
                    continue;
                }

                log.Write("INFO", "filing job done", ("outcome", result.Outcome), ("filing_id", result.FilingId), ("reason", result.Reason));
                await sqs.DeleteMessageAsync(queue, message.ReceiptHandle, cancellationToken).ConfigureAwait(false);
            }
        }

        return 0;
    }
}
