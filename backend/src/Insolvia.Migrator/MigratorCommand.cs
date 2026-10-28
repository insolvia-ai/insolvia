using System.Text.Json;
using Amazon.S3;
using Insolvia.Core;
using Insolvia.Migrator.Purge;
using Insolvia.Migrator.Seeding;

namespace Insolvia.Migrator;

/// <summary>What one migrator run did — the Lambda's JSON response.</summary>
/// <param name="Command">The command that ran.</param>
/// <param name="ExitCode">0 done; 1 check found something missing (or a pre-client case left); 2 refused.</param>
/// <param name="Migration">The migration's report, when one ran.</param>
/// <param name="Seed">The seed's report, when one ran.</param>
/// <param name="Purge">The purge's report, when one ran.</param>
/// <param name="Lines">The human report.</param>
/// <param name="Refusal">Why it refused, when it did.</param>
public sealed record MigratorRun(
    string Command,
    int ExitCode,
    MigrationReport? Migration,
    SeedReport? Seed,
    PurgeReport? Purge,
    IReadOnlyList<string> Lines,
    string? Refusal = null)
{
    /// <summary>Done.</summary>
    public const int Ok = 0;

    /// <summary>Check found something missing.</summary>
    public const int Missing = 1;

    /// <summary>A guard or the fixture refused; nothing was written for what was refused.</summary>
    public const int Refused = 2;
}

/// <summary>
/// The migrator's commands — the same three from a shell (<c>dotnet run --
/// &lt;command&gt;</c>) and from a Lambda payload (<c>{"command": …}</c>),
/// because on a deployed environment the database is private and the Lambda
/// is the only thing that reaches it.
/// </summary>
/// <param name="Name">The command.</param>
/// <param name="Check">Report only, and exit 1 when something is missing.</param>
/// <param name="Apply">Purge only: delete (the default is a dry run).</param>
/// <param name="Fixture">Seed only: the environment fixture, overriding <c>INSOLVIA_SEED_FIXTURE</c>.</param>
/// <param name="Version">Publish only: the version folder.</param>
/// <param name="FixtureBucket">Publish only: the shared fixture bucket.</param>
public sealed record MigratorCommand(string Name, bool Check = false, bool Apply = false, string? Fixture = null, string? Version = null, string? FixtureBucket = null)
{
    /// <summary>The usage text.</summary>
    public const string Usage = """
        usage: Insolvia.Migrator [command] [options]

          migrate            (default) apply migrations, provision roles and grants,
                             then seed when INSOLVIA_SEED=true
          seed               converge INSOLVIA_ENV (local, staging — never production)
                             on seeds/<env>.json      [--fixture PATH] [--check]
          seed-publish       put a committed fixture version into the shared bucket
                             --version seeds/fixtures/vN --fixture-bucket NAME [--check]
          purge-pre-client   delete cases whose debtors name no firm client (ADR 0022)
                             dry run unless --apply; --check exits 1 while any remain
        """;

    /// <summary>Parses command-line arguments.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The command.</returns>
    public static MigratorCommand FromArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var start = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? 1 : 0;
        var command = new MigratorCommand(start == 1 ? args[0] : "migrate");
        for (var index = start; index < args.Count; index++)
        {
            string Value() => index + 1 < args.Count ? args[++index] : throw new ArgumentException($"{args[index]} needs a value");
            command = args[index] switch
            {
                "--check" => command with { Check = true },
                "--apply" => command with { Apply = true },
                "--fixture" => command with { Fixture = Value() },
                "--version" => command with { Version = Value() },
                "--fixture-bucket" => command with { FixtureBucket = Value() },
                var other => throw new ArgumentException($"unknown argument '{other}'"),
            };
        }

        return command.Validated();
    }

    /// <summary>Parses a Lambda payload: <c>{}</c> is migrate.</summary>
    /// <param name="payload">The invocation's JSON.</param>
    /// <returns>The command.</returns>
    public static MigratorCommand FromPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return new MigratorCommand("migrate");
        }

        string? Text(string key) => payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool Flag(string key) => payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
        return new MigratorCommand(Text("command") ?? "migrate", Flag("check"), Flag("apply"), Text("fixture"), Text("version"), Text("fixtureBucket")).Validated();
    }

    /// <summary>Runs the command.</summary>
    /// <param name="read">Reads one environment variable.</param>
    /// <param name="isLambda">A deployed run.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What it did.</returns>
    public async Task<MigratorRun> RunAsync(Func<string, string?> read, bool isLambda, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return Name switch
            {
                "migrate" => await MigrateAsync(read, isLambda, cancellationToken).ConfigureAwait(false),
                "seed" => FromSeed(await SeedAsync(read, isLambda, cancellationToken).ConfigureAwait(false), null),
                "seed-publish" => await PublishAsync(cancellationToken).ConfigureAwait(false),
                _ => await PurgeAsync(read, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (SeedRefusedException refusal)
        {
            return new MigratorRun(Name, MigratorRun.Refused, null, null, null, [], refusal.Message);
        }
        catch (FieldValidationException invalid)
        {
            // The API's own parser refused a fixture body: the same field
            // errors a route would answer with.
            return new MigratorRun(Name, MigratorRun.Refused, null, null, null, [], invalid.Message + ": " + string.Join("; ", invalid.Fields.Select(pair => $"{pair.Key}: {pair.Value}")));
        }
        catch (ValidationException invalid)
        {
            return new MigratorRun(Name, MigratorRun.Refused, null, null, null, [], invalid.Message);
        }
    }

    private MigratorCommand Validated() => Name switch
    {
        "migrate" or "seed" or "purge-pre-client" when Version is null && FixtureBucket is null => Name == "purge-pre-client" && Apply && Check
            ? throw new ArgumentException("--apply and --check are exclusive")
            : this,
        "seed-publish" when Version is not null && FixtureBucket is not null => this,
        "seed-publish" => throw new ArgumentException("seed-publish needs --version and --fixture-bucket"),
        "migrate" or "seed" or "purge-pre-client" => throw new ArgumentException("--version and --fixture-bucket are seed-publish's"),
        _ => throw new ArgumentException($"unknown command '{Name}'"),
    };

    private async Task<MigratorRun> MigrateAsync(Func<string, string?> read, bool isLambda, CancellationToken cancellationToken)
    {
        var settings = MigratorSettings.FromEnvironment(read);
        MigrationReport migration;
        var dataSource = await settings.CreateDataSourceAsync(cancellationToken).ConfigureAwait(false);
        await using (dataSource.ConfigureAwait(false))
        {
            migration = await MigrationRunner.RunAsync(dataSource, GrantTable.All, settings.RolePassword, cancellationToken).ConfigureAwait(false);
        }

        if (!SeedComposition.Enabled(read))
        {
            return new MigratorRun(Name, MigratorRun.Ok, migration, null, null, []);
        }

        return FromSeed(await SeedAsync(read, isLambda, cancellationToken).ConfigureAwait(false), migration);
    }

    private Task<SeedReport> SeedAsync(Func<string, string?> read, bool isLambda, CancellationToken cancellationToken) =>
        SeedComposition.RunAsync(MigratorSettings.FromEnvironment(read), read, Fixture, Check, isLambda, cancellationToken);

    private MigratorRun FromSeed(SeedReport seed, MigrationReport? migration) =>
        new(Name, seed.Check && seed.Missing > 0 ? MigratorRun.Missing : MigratorRun.Ok, migration, seed, null, seed.Lines);

    private async Task<MigratorRun> PublishAsync(CancellationToken cancellationToken)
    {
        SeedPublisher.RequireFixtureBucket(FixtureBucket);
        using var s3 = new AmazonS3Client();
        var output = new StringWriter();
        var missing = await SeedPublisher.PublishAsync(Version!, new S3FixtureObjects(s3, FixtureBucket!, null), Check, output, cancellationToken).ConfigureAwait(false);
        return new MigratorRun(Name, Check && missing > 0 ? MigratorRun.Missing : MigratorRun.Ok, null, null, null, Lines(output));
    }

    private async Task<MigratorRun> PurgeAsync(Func<string, string?> read, CancellationToken cancellationToken)
    {
        // Every environment may run it — the one fence that admits
        // production — because it only ever deletes what ADR 0022 condemned.
        var environment = InsolviaEnvironmentName(read);
        var settings = MigratorSettings.FromEnvironment(read);
        var dataSource = await settings.CreateDataSourceAsync(cancellationToken).ConfigureAwait(false);
        await using (dataSource.ConfigureAwait(false))
        {
            var report = await PreClientPurge.RunAsync(dataSource, Apply, cancellationToken).ConfigureAwait(false);
            var output = new StringWriter();
            PreClientPurge.Print(report, environment, Apply, output);
            return new MigratorRun(Name, Check && report.Planned.Count > 0 ? MigratorRun.Missing : MigratorRun.Ok, null, null, report, Lines(output));
        }
    }

    private static string InsolviaEnvironmentName(Func<string, string?> read)
    {
        var name = read("INSOLVIA_ENV") is { Length: > 0 } value ? value : "local";
        return name is "local" or "staging" or "production"
            ? name
            : throw new SeedRefusedException($"INSOLVIA_ENV must be one of local, staging, production, got '{name}'");
    }

    private static List<string> Lines(StringWriter output) =>
        [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];
}
