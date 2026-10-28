using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.SQS;
using Insolvia.Worker;

// One binary, two hosts, like the migrator. Inside Lambda (the runtime API
// is set) the SQS event source mapping delivers one message per invocation
// (batch size 1); an unhandled exception IS the retry signal — SQS
// redelivers, and after maxReceiveCount parks the message on the DLQ whose
// depth alarms. Everywhere else (`dotnet run`) it is the local poller over
// this machine's own dev queue (JOB_QUEUE_URL), Ctrl-C between polls.
var runtime = WorkerRuntime.FromEnvironment(Environment.GetEnvironmentVariable);
await using (runtime.ConfigureAwait(false))
{
    if (Environment.GetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API") is not null)
    {
        var handler = async (Stream payload, ILambdaContext context) =>
            await runtime.HandleAsync(JobDispatch.ParseEvent(payload)).ConfigureAwait(false);
        await LambdaBootstrapBuilder.Create(handler).Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    var queueUrl = Environment.GetEnvironmentVariable("JOB_QUEUE_URL");
    if (string.IsNullOrEmpty(queueUrl))
    {
        await Console.Error.WriteLineAsync("worker: JOB_QUEUE_URL must be set for the local poller (scripts/dev-aws-setup.sh writes it)").ConfigureAwait(false);
        return 1;
    }

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, args) =>
    {
        args.Cancel = true;
        cancellation.Cancel();
    };
    using var sqs = Environment.GetEnvironmentVariable("AWS_REGION") is { Length: > 0 } region
        ? new AmazonSQSClient(RegionEndpoint.GetBySystemName(region))
        : new AmazonSQSClient();
    await runtime.PollAsync(sqs, queueUrl, cancellation.Token).ConfigureAwait(false);
    return 0;
}
