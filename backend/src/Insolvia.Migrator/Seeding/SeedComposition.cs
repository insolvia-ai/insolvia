using Amazon.CognitoIdentityProvider;
using Amazon.KeyManagementService;
using Amazon.S3;
using Insolvia.Aws.Documents;
using Insolvia.Aws.Sealing;
using Insolvia.Core.Local;
using Insolvia.Core.Ports;
using Insolvia.Data;
using Insolvia.Data.Cases;
using Insolvia.Data.Firms;
using Insolvia.Data.Pagination;
using Insolvia.Data.Portal;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Insolvia.Migrator.Seeding;

/// <summary>What one seed run did: the JSON a Lambda invocation returns, and the report's lines.</summary>
/// <param name="Environment">The environment loaded into.</param>
/// <param name="Fixture">The environment fixture loaded.</param>
/// <param name="Check">Whether it only reported.</param>
/// <param name="Missing">Things that were missing — written now, or still missing under check.</param>
/// <param name="Lines">The report, one line per thing seen.</param>
public sealed record SeedReport(string Environment, string Fixture, bool Check, int Missing, IReadOnlyList<string> Lines);

/// <summary>
/// Where the seeder's adapters come from, per place it runs (ADR 0028 §
/// Runtime shape: "the migrator Lambda applies migrations and, in dev and
/// staging, loads seeds" — the database is private and CI cannot reach it).
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>Database</term><description>A login AS the <c>migrator</c>
/// role — an IAM token deployed, <c>INSOLVIA_DB_ROLE_PASSWORD</c> locally — so
/// every statement runs with that role's grants (GrantTable: INSERT and
/// SELECT on what a load writes, UPDATE only where a store's write needs it)
/// and not the master user's: the role ADR 0028 reserved for exactly this.
/// Never SET ROLE on the master's login (MigratorSettings says why).</description></item>
/// <item><term>Accounts</term><description><c>AUTH_USER_POOL_ID</c> → the
/// pool (Cognito: AdminGetUser, AdminCreateUser SUPPRESS,
/// AdminSetUserPassword). Unset, locally only, subjects are derived from the
/// address (<see cref="LocalSeedAccounts"/>); a Lambda refuses.</description></item>
/// <item><term>Bytes</term><description><c>CASE_DOCUMENT_BUCKET</c> and
/// <c>INSOLVIA_SEED_FIXTURE_BUCKET</c> → server-side S3 copies from the shared
/// fixture bucket with Python's keys (<c>&lt;version&gt;/objects/…</c>); a
/// Lambda requires both. Unset, locally only, the committed
/// <c>seeds/fixtures</c> tree is the source and
/// <c>INSOLVIA_SEED_DOCUMENT_DIR</c> (default: a temp directory) the target.</description></item>
/// <item><term>Case key</term><description><c>INSOLVIA_KMS_CASE_KEY_ALIAS</c>,
/// or in a Lambda <c>alias/insolvia-&lt;env&gt;-cases</c> — the API's own
/// derivation; locally with neither, the local key the local API opens with.</description></item>
/// </list>
/// </remarks>
public static class SeedComposition
{
    /// <summary>The variable that turns seeding on in the default (migrate) run: <c>true</c> or <c>1</c>.</summary>
    public const string SeedVariable = "INSOLVIA_SEED";

    /// <summary>The environment fixture to load; default <c>seeds/dev.json</c> (local) or <c>seeds/staging.json</c>.</summary>
    public const string FixtureVariable = "INSOLVIA_SEED_FIXTURE";

    /// <summary>Whether <paramref name="read"/> turns seeding on.</summary>
    /// <param name="read">Reads one variable.</param>
    /// <returns>True for <c>true</c> or <c>1</c>.</returns>
    public static bool Enabled(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read(SeedVariable)?.Trim().ToLowerInvariant() is "true" or "1";
    }

    /// <summary>Seed the environment <c>INSOLVIA_ENV</c> names — refused for production before anything connects.</summary>
    /// <param name="database">Where the migrator connects.</param>
    /// <param name="read">Reads one variable.</param>
    /// <param name="fixture">Overrides <see cref="FixtureVariable"/>.</param>
    /// <param name="check">Report only; write nothing.</param>
    /// <param name="isLambda">A deployed run: every AWS adapter is required, no local fallback.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The report.</returns>
    public static async Task<SeedReport> RunAsync(
        MigratorSettings database,
        Func<string, string?> read,
        string? fixture,
        bool check,
        bool isLambda,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(read);
        var target = SeedTarget.For(read("INSOLVIA_ENV"));
        var fixturePath = ResolveFixture(fixture ?? Optional(read, FixtureVariable), target);

        var accounts = Accounts(read, isLambda);
        var dataSource = await database.CreateDataSourceAsync(cancellationToken, role: ServiceRoles.Migrator).ConfigureAwait(false);
        await using (dataSource.ConfigureAwait(false))
        {
            var output = new StringWriter();
            var stores = Stores(dataSource, read, target, fixturePath, isLambda, out var context);
            await using (context.ConfigureAwait(false))
            {
                var loader = new SeedLoader(stores, accounts, target, output);
                var missing = await loader.LoadAsync(fixturePath, read, check, cancellationToken).ConfigureAwait(false);
                return new SeedReport(target.Environment, fixturePath, check, missing, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList());
            }
        }
    }

    /// <summary>The stores a load writes through, over one context on <paramref name="dataSource"/>.</summary>
    internal static SeedStores Stores(NpgsqlDataSource dataSource, Func<string, string?> read, SeedTarget target, string fixturePath, bool isLambda, out InsolviaDbContext context)
    {
        var options = new DbContextOptionsBuilder<InsolviaDbContext>();
        options.UseInsolviaNpgsql(dataSource);
        context = new InsolviaDbContext(options.Options);
        var (blobs, objects) = Bytes(read, fixturePath, isLambda);
        return new SeedStores(
            new PostgresFirmStore(context, TimeProvider.System),
            new PostgresCaseStore(context, CursorCodec.FromEnvironment(read)),
            new PostgresDebtorStore(context),
            new PostgresCaseEntityStore(context),
            new PostgresDocumentStore(context),
            blobs,
            objects,
            new PostgresTaxIdStore(context),
            Cipher(read, target, isLambda),
            new PostgresClientBindingStore(context));
    }

    private static ISeedAccounts Accounts(Func<string, string?> read, bool isLambda)
    {
        if (Optional(read, "AUTH_USER_POOL_ID") is { } pool)
        {
            return new CognitoSeedAccounts(new AmazonCognitoIdentityProviderClient(), pool);
        }

        return isLambda
            ? throw new SeedRefusedException("AUTH_USER_POOL_ID must name the firm pool for a deployed seed: staging's people sign in for real")
            : new LocalSeedAccounts();
    }

    private static (IDocumentBlobStore Blobs, IFixtureObjects Objects) Bytes(Func<string, string?> read, string fixturePath, bool isLambda)
    {
        var bucket = Optional(read, "CASE_DOCUMENT_BUCKET");
        var fixtureBucket = Optional(read, "INSOLVIA_SEED_FIXTURE_BUCKET");
        if (bucket is not null && fixtureBucket is not null)
        {
            SeedPublisher.RequireFixtureBucket(fixtureBucket);
            var s3 = new AmazonS3Client(S3DocumentBlobStore.ClientConfig());
            return (new S3DocumentBlobStore(s3, bucket), new S3FixtureObjects(s3, fixtureBucket, bucket));
        }

        if (isLambda)
        {
            throw new SeedRefusedException("CASE_DOCUMENT_BUCKET and INSOLVIA_SEED_FIXTURE_BUCKET must both be set for a deployed seed");
        }

        var directory = Optional(read, "INSOLVIA_SEED_DOCUMENT_DIR") ?? Path.Combine(Path.GetTempPath(), "insolvia-local-case-documents");
        var blobs = new DirectoryDocumentBlobStore(directory);
        var fixturesRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!, SeedFixtures.FixturesDirectory);
        return (blobs, new DirectoryFixtureObjects(fixturesRoot, blobs));
    }

    private static ITaxIdCipher Cipher(Func<string, string?> read, SeedTarget target, bool isLambda)
    {
        var alias = Optional(read, "INSOLVIA_KMS_CASE_KEY_ALIAS") ?? (isLambda ? KeyAliases.CaseKey(target.CaseTarget) : null);
        return alias is null ? new LocalTaxIdCipher() : new KmsTaxIdCipher(new AmazonKeyManagementServiceClient(), alias);
    }

    /// <summary>
    /// The fixture to load: the explicit path, or <c>seeds/dev.json</c> /
    /// <c>seeds/staging.json</c> found beside the binary (the image carries
    /// <c>seeds/</c>) or above the working directory (a checkout).
    /// </summary>
    internal static string ResolveFixture(string? path, SeedTarget target)
    {
        if (path is not null)
        {
            return path;
        }

        var name = target.Environment == "local" ? "dev.json" : $"{target.Environment}.json";
        var roots = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
        foreach (var root in roots)
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "seeds", name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new SeedRefusedException($"no seeds/{name} beside the migrator or above {Directory.GetCurrentDirectory()}; set {FixtureVariable}");
    }

    private static string? Optional(Func<string, string?> read, string name) => read(name) is { } value && value.Trim().Length > 0 ? value.Trim() : null;
}
