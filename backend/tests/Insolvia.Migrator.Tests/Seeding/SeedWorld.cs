using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Insolvia.Aws.Sealing;
using Insolvia.Core.Memory;
using Insolvia.Migrator.Seeding;

namespace Insolvia.Migrator.Tests.Seeding;

/// <summary>
/// A pool that starts with <c>known</c> in it and records what is created —
/// test_seed.py's <c>FakeAccounts</c>. A miss is a <see cref="SeedRefusedException"/>,
/// as the contract says; a fake that threw anything else would honour a
/// contract nobody implements.
/// </summary>
internal sealed class FakeAccounts(Dictionary<string, string>? known = null) : ISeedAccounts
{
    public const string Alice = "11111111-2222-3333-4444-555555555555";
    public const string Bob = "99999999-8888-7777-6666-555555555555";

    private int _next;

    public Dictionary<string, string> Subjects { get; } = known ?? new(StringComparer.Ordinal)
    {
        ["alice@insolvia.test"] = Alice,
        ["bob@insolvia.test"] = Bob,
    };

    public List<string> Created { get; } = [];

    public Dictionary<string, string> Passwords { get; } = new(StringComparer.Ordinal);

    public Task<string> SubjectOfAsync(string email, CancellationToken cancellationToken = default) =>
        Subjects.TryGetValue(email, out var subject)
            ? Task.FromResult(subject)
            : throw new SeedRefusedException($"no pool user for {email}");

    public Task<string> EnsureAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (!Subjects.TryGetValue(email, out var subject))
        {
            _next++;
            subject = $"aaaaaaaa-bbbb-cccc-dddd-{_next:D12}";
            Subjects[email] = subject;
            Created.Add(email);
        }

        Passwords[email] = password;
        return Task.FromResult(subject);
    }
}

/// <summary>The shared fixture bucket in memory: a copy lands bytes in the target blob store as S3's server-side copy would.</summary>
internal sealed class FakeFixtureObjects(MemoryDocumentBlobStore blobs, Dictionary<string, byte[]> objects) : IFixtureObjects
{
    public Dictionary<string, byte[]> Objects { get; } = objects;

    public List<string> Copied { get; } = [];

    public async Task CopyAsync(string sourceKey, string targetKey, string contentType, CancellationToken cancellationToken = default)
    {
        if (!Objects.TryGetValue(sourceKey, out var content))
        {
            throw new InvalidOperationException($"no fixture object {sourceKey}");
        }

        await blobs.PutBytesAsync(targetKey, content, contentType, cancellationToken);
        Copied.Add(targetKey);
    }

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        Objects[key] = content;
        return Task.CompletedTask;
    }

    public Task<string?> DigestAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Objects.TryGetValue(key, out var found) ? Convert.ToHexStringLower(SHA256.HashData(found)) : null);
}

/// <summary>
/// A whole environment's stores over the memory twins, and a fixture folder —
/// test_seed.py's <c>Env</c>. One debtor store behind the case store and the
/// loader, as one database is behind both.
/// </summary>
internal sealed class SeedWorld : IDisposable
{
    public static readonly byte[] Pdf = "%PDF-1.4\n% fixture bytes\n"u8.ToArray();

    public static readonly JsonObject Typed = new() { ["source"] = "staff_typed" };

    public SeedWorld()
    {
        Directory.CreateDirectory(Path.Combine(Root, "fixtures", "v1", "objects"));
        File.WriteAllBytes(Path.Combine(Root, "fixtures", "v1", "objects", "stub.pdf"), Pdf);
        Write("fixtures/v1/manifest.json", new JsonObject
        {
            ["version"] = "v1",
            ["objects"] = new JsonObject
            {
                ["objects/stub.pdf"] = new JsonObject { ["size"] = Pdf.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Pdf)) },
            },
        });
        Write("fixtures/v1/cases.json", new JsonObject
        {
            ["version"] = "v1",
            ["clients"] = new JsonArray(new JsonObject { ["handle"] = "sample-client", ["name"] = new JsonObject { ["given"] = "Sample" } }),
            ["cases"] = new JsonArray(new JsonObject
            {
                ["handle"] = "sample",
                ["chapter"] = 7,
                ["court"] = "flmb",
                ["division"] = "tampa",
                ["debtors"] = new JsonObject
                {
                    ["debtor_1"] = new JsonObject
                    {
                        ["client_id"] = new JsonObject { ["$ref"] = "clients/sample-client" },
                        ["name"] = new JsonObject { ["given"] = "Sample" },

                        // The SSA's never-issued advertising block — the
                        // fixture value the parser accepts on purpose.
                        ["tax_id"] = new JsonObject { ["kind"] = "ssn", ["value"] = "987-65-4321" },
                        ["provenance"] = new JsonObject { ["name.given"] = Typed.DeepClone(), ["tax_id"] = Typed.DeepClone() },
                    },
                },
                ["collections"] = new JsonObject
                {
                    ["creditors"] = new JsonArray(new JsonObject { ["name"] = "Example Bank", ["provenance"] = new JsonObject { ["name"] = Typed.DeepClone() } }),
                },
                ["documents"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "pay_stub",
                    ["fileName"] = "stub.pdf",
                    ["contentType"] = "application/pdf",
                    ["object"] = "objects/stub.pdf",
                }),
            }),
        });
        Write("env.json", new JsonObject
        {
            ["firms"] = new JsonArray(new JsonObject
            {
                ["name"] = "Example & Partners",
                ["users"] = new JsonArray(User("e2e-admin@insolvia.test", new JsonObject { ["handle"] = "admin", ["password"] = "hunter2ABCDEF" })),
            }),
            ["cases"] = new JsonArray(new JsonObject { ["fixture"] = "v1", ["case"] = "sample", ["firm"] = "Example & Partners", ["openedBy"] = "admin" }),
        });

        Debtors = new MemoryDebtorStore(Firms);
        Cases = new MemoryCaseStore(Debtors);
        Objects = new FakeFixtureObjects(Blobs, new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["v1/objects/stub.pdf"] = Pdf });
        Stores = new SeedStores(Firms, Cases, Debtors, Entities, Documents, Blobs, Objects, TaxIds, Cipher, Bindings);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "insolvia-seed-tests", Guid.NewGuid().ToString("N"));

    public string EnvFixture => Path.Combine(Root, "env.json");

    public MemoryFirmStore Firms { get; } = new();

    public MemoryDebtorStore Debtors { get; }

    public MemoryCaseStore Cases { get; }

    public MemoryCaseEntityStore Entities { get; } = new();

    public MemoryDocumentStore Documents { get; } = new();

    public MemoryDocumentBlobStore Blobs { get; } = new();

    public FakeFixtureObjects Objects { get; }

    public MemoryTaxIdStore TaxIds { get; } = new();

    public LocalTaxIdCipher Cipher { get; } = new();

    public MemoryClientBindingStore Bindings { get; } = new();

    public FakeAccounts Accounts { get; set; } = new([]);

    public SeedStores Stores { get; set; }

    public StringWriter Output { get; private set; } = new();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static JsonObject User(string email = "alice@insolvia.test", JsonObject? overrides = null)
    {
        var user = new JsonObject
        {
            ["email"] = email,
            ["firstName"] = "Example",
            ["lastName"] = "Person",
            ["role"] = "attorney",
            ["isAdmin"] = true,
            ["accessAllCases"] = true,
        };
        foreach (var (key, value) in overrides ?? [])
        {
            user[key] = value?.DeepClone();
        }

        return user;
    }

    public JsonObject Read(string relative) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Root, relative)))!;

    public void Write(string relative, JsonNode body)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body.ToJsonString());
    }

    /// <summary>Edits a fixture file in place.</summary>
    public void Edit(string relative, Action<JsonObject> change)
    {
        var body = Read(relative);
        change(body);
        Write(relative, body);
    }

    /// <summary>
    /// One load, as the CLI answers it: 0 converged, 1 check found something
    /// missing, 2 refused. A body the API's parser refuses is THROWN, as the
    /// Python let it propagate — those tests assert the API's own error.
    /// </summary>
    public async Task<int> LoadAsync(bool check = false, string environment = "local", string? fixture = null, Func<string, string?>? variables = null)
    {
        Output = new StringWriter();
        try
        {
            var loader = new SeedLoader(Stores, Accounts, SeedTarget.For(environment), Output);
            var missing = await loader.LoadAsync(fixture ?? EnvFixture, variables ?? (_ => null), check, Ct);
            return check && missing > 0 ? 1 : 0;
        }
        catch (SeedRefusedException refusal)
        {
            Output.WriteLine("refusing: " + refusal.Message);
            return 2;
        }
    }

    /// <summary>The one fixture case, read with no access rule, at the id the local target derives.</summary>
    public async Task<Core.Cases.Case?> TheCaseAsync(string environment = "local")
    {
        var subject = Accounts.Subjects["e2e-admin@insolvia.test"];
        var firmId = (await Firms.FindUserAsync(subject, Ct))!.FirmId;
        return await Cases.ReadForWorkerAsync(SeedIds.Derive(SeedTarget.For(environment).CaseTarget, firmId, "v1", "sample"), Ct);
    }

    public void Dispose()
    {
        Output.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
