using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Insolvia.Migrator;

// One binary, two hosts, like the API. Inside Lambda (the runtime API is
// set) each invocation is one run, and its result is the invocation's JSON
// response; a failure is a function error, which is what CI's "invoke the
// migrator before shifting the alias" step checks. Everywhere else it runs
// once and the exit code says how it went: `dotnet run`, docker compose.
if (Environment.GetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API") is not null)
{
    var handler = (ILambdaContext context) => RunOnceAsync(CancellationToken.None);
    await LambdaBootstrapBuilder.Create(handler, new DefaultLambdaJsonSerializer()).Build().RunAsync().ConfigureAwait(false);
    return 0;
}

try
{
    var report = await RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error) when (error is InvalidOperationException or Npgsql.NpgsqlException)
{
    await Console.Error.WriteLineAsync($"migrator: {error.Message}").ConfigureAwait(false);
    return 1;
}

static async Task<MigrationReport> RunOnceAsync(CancellationToken cancellationToken)
{
    var settings = MigratorSettings.FromEnvironment(Environment.GetEnvironmentVariable);
    var dataSource = await settings.CreateDataSourceAsync(cancellationToken).ConfigureAwait(false);
    await using (dataSource.ConfigureAwait(false))
    {
        return await MigrationRunner.RunAsync(dataSource, GrantTable.All, settings.RolePassword, cancellationToken).ConfigureAwait(false);
    }
}
