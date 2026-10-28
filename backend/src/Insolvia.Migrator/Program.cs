using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Insolvia.Migrator;

// One binary, two hosts, like the API. Inside Lambda (the runtime API is
// set) each invocation is one run, chosen by its JSON payload, and its result
// is the invocation's response; a failure is a function error, which is what
// CI's "invoke the migrator before shifting the alias" step checks. Anywhere
// else it runs once, chosen by its arguments, and the exit code says how it
// went: `dotnet run`, docker compose. MigratorCommand.cs owns the commands.
if (Environment.GetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API") is not null)
{
    var handler = async (JsonElement payload, ILambdaContext context) =>
    {
        var run = await MigratorCommand.FromPayload(payload).RunAsync(Environment.GetEnvironmentVariable, isLambda: true, CancellationToken.None).ConfigureAwait(false);
        return run.ExitCode == MigratorRun.Refused
            ? throw new InvalidOperationException(run.Refusal)
            : run;
    };
    await LambdaBootstrapBuilder.Create(handler, new DefaultLambdaJsonSerializer()).Build().RunAsync().ConfigureAwait(false);
    return 0;
}

MigratorCommand command;
try
{
    command = MigratorCommand.FromArguments(args);
}
catch (ArgumentException error)
{
    await Console.Error.WriteLineAsync($"migrator: {error.Message}\n\n{MigratorCommand.Usage}").ConfigureAwait(false);
    return MigratorRun.Refused;
}

try
{
    var run = await command.RunAsync(Environment.GetEnvironmentVariable, isLambda: false, CancellationToken.None).ConfigureAwait(false);
    foreach (var line in run.Lines)
    {
        Console.WriteLine(line);
    }

    if (run.Seed is { } seed)
    {
        Console.WriteLine($"seed {seed.Environment} from {seed.Fixture}: {seed.Missing} missing thing(s) {(seed.Check ? "found" : "written")}");
    }

    if (run.Refusal is not null)
    {
        await Console.Error.WriteLineAsync($"refusing: {run.Refusal}").ConfigureAwait(false);
    }

    if (run.Migration is not null)
    {
        Console.WriteLine(JsonSerializer.Serialize(run.Migration, new JsonSerializerOptions { WriteIndented = true }));
    }

    return run.ExitCode;
}
catch (Exception error) when (error is InvalidOperationException or Npgsql.NpgsqlException)
{
    await Console.Error.WriteLineAsync($"migrator: {error.Message}").ConfigureAwait(false);
    return 1;
}
