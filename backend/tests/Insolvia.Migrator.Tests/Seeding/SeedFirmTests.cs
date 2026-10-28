using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Firms;
using Insolvia.Migrator.Seeding;
using static Insolvia.Migrator.Tests.Seeding.SeedWorld;

namespace Insolvia.Migrator.Tests.Seeding;

// services/admin tests/unit/test_seed.py, the firm half, one test per Python
// test in intent. What it holds down — none of which is "seeding works":
//   1. IT CANNOT REACH PROD (here: the environment is the fence).
//   2. AN UNSET ${VAR} IS A REFUSAL, NEVER AN EMPTY STRING.
//   3. IT CONVERGES RATHER THAN DUPLICATES.
//   4. A REFUSAL WRITES NOTHING.
// Every identifier is obviously fake; this repo is public.
public sealed class SeedFirmTests : IDisposable
{
    private readonly SeedWorld _world = new() { Accounts = new FakeAccounts() };

    public void Dispose() => _world.Dispose();

    private string OneFirm(params JsonObject[] users)
    {
        _world.Write("seed.json", new JsonObject
        {
            ["firms"] = new JsonArray(new JsonObject { ["name"] = "Example & Partners", ["users"] = new JsonArray([.. users]) }),
        });
        return Path.Combine(_world.Root, "seed.json");
    }

    private Task<int> Run(string fixture, bool check = false, string environment = "local", Func<string, string?>? variables = null) =>
        _world.LoadAsync(check, environment, fixture, variables);

    // ── the guard ───────────────────────────────────────────────────

    [Theory]
    [InlineData("production")]
    [InlineData("prod")]
    [InlineData("dev")]
    [InlineData("Staging")]
    public async Task An_unseedable_environment_is_refused(string environment)
    {
        Assert.Equal(2, await Run(OneFirm(User()), environment: environment));
        Assert.Null(await _world.Firms.FindUserAsync(FakeAccounts.Alice, Ct));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("staging")]
    public async Task Local_and_staging_are_both_seedable(string environment)
    {
        Assert.Equal(0, await Run(OneFirm(User()), environment: environment));
        Assert.NotNull(await _world.Firms.FindUserAsync(FakeAccounts.Alice, Ct));
    }

    [Fact]
    public void An_unset_environment_is_local()
    {
        Assert.Equal("local", SeedTarget.For(null).Environment);
        Assert.Equal("insolvia-local-cases", SeedTarget.For(string.Empty).CaseTarget);
    }

    // ── ${VAR} expansion ────────────────────────────────────────────

    [Fact]
    public async Task An_unset_variable_is_refused_rather_than_emptied()
    {
        Assert.Equal(2, await Run(OneFirm(User("${SEED_TEST_EMAIL}"))));
        Assert.Empty(_world.Firms.Firms);
    }

    [Fact]
    public async Task An_empty_variable_is_refused_too()
    {
        // Set-but-empty is the shape a missing CI secret actually takes.
        Assert.Equal(2, await Run(OneFirm(User("${SEED_TEST_EMAIL}")), variables: _ => string.Empty));
        Assert.Contains("${SEED_TEST_EMAIL}, which is unset or empty", _world.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_set_variable_is_expanded()
    {
        Assert.Equal(0, await Run(OneFirm(User("${SEED_TEST_EMAIL}")), variables: name => name == "SEED_TEST_EMAIL" ? "alice@insolvia.test" : null));

        var seeded = await _world.Firms.FindUserAsync(FakeAccounts.Alice, Ct);
        Assert.Equal("alice@insolvia.test", seeded!.Email);
    }

    // ── what gets written ───────────────────────────────────────────

    [Fact]
    public async Task The_seeded_person_is_an_active_admin_of_an_active_firm()
    {
        Assert.Equal(0, await Run(OneFirm(User())));

        var seeded = (await _world.Firms.FindUserAsync(FakeAccounts.Alice, Ct))!;
        Assert.Equal(FirmUserStatus.Active, seeded.Status);
        Assert.True(seeded.IsAdmin);
        Assert.True(seeded.AccessAllCases);
        var firm = (await _world.Firms.GetFirmAsync(seeded.FirmId, Ct))!;
        Assert.Equal(FirmStatus.Active, firm.Status);
        Assert.Equal("Example & Partners", firm.Name);

        // A fixture names no provisioner — the admin portal reads null.
        Assert.Null(firm.CreatedBy);
    }

    [Fact]
    public async Task Two_people_in_one_fixture_firm_share_it()
    {
        Assert.Equal(0, await Run(OneFirm(User(), User("bob@insolvia.test"))));

        Assert.Equal(
            (await _world.Firms.FindUserAsync(FakeAccounts.Alice, Ct))!.FirmId,
            (await _world.Firms.FindUserAsync(FakeAccounts.Bob, Ct))!.FirmId);
    }

    // ── a fixture that supplies its own subjects ────────────────────

    [Fact]
    public async Task A_password_in_the_fixture_creates_the_account()
    {
        _world.Accounts = new FakeAccounts([]);

        Assert.Equal(0, await Run(OneFirm(User("e2e-admin@insolvia.test", new JsonObject { ["password"] = "hunter2ABCDEF" })), environment: "staging"));

        Assert.Equal(["e2e-admin@insolvia.test"], _world.Accounts.Created);
        Assert.Equal("hunter2ABCDEF", _world.Accounts.Passwords["e2e-admin@insolvia.test"]);
        Assert.NotNull(await _world.Firms.FindUserAsync(_world.Accounts.Subjects["e2e-admin@insolvia.test"], Ct));
    }

    [Fact]
    public async Task The_password_is_reset_on_an_account_that_already_exists()
    {
        // Converging the password is what makes a rotated secret take effect.
        _world.Accounts = new FakeAccounts(new Dictionary<string, string> { ["alice@insolvia.test"] = FakeAccounts.Alice });

        Assert.Equal(0, await Run(OneFirm(User(overrides: new JsonObject { ["password"] = "rotatedABCDEF1" })), environment: "staging"));

        Assert.Empty(_world.Accounts.Created);
        Assert.Equal("rotatedABCDEF1", _world.Accounts.Passwords["alice@insolvia.test"]);
    }

    [Fact]
    public async Task Without_a_password_the_account_must_already_exist()
    {
        _world.Accounts = new FakeAccounts([]);

        Assert.Equal(2, await Run(OneFirm(User())));

        Assert.Empty(_world.Accounts.Created);
        Assert.Empty(_world.Firms.Firms);
    }

    [Fact]
    public async Task Check_never_creates_an_account()
    {
        // --check promises to write nothing, and a pool account is a write.
        _world.Accounts = new FakeAccounts([]);

        Assert.Equal(2, await Run(OneFirm(User("e2e-admin@insolvia.test", new JsonObject { ["password"] = "hunter2ABCDEF" })), check: true, environment: "staging"));

        Assert.Empty(_world.Accounts.Created);
        Assert.Empty(_world.Accounts.Passwords);
    }

    [Fact]
    public async Task Several_people_across_two_firms_are_all_provisioned()
    {
        // The shape seeds/staging.json has, and the reason for all this:
        // cross-tenant isolation cannot be tested from inside one firm.
        _world.Accounts = new FakeAccounts([]);
        var password = new JsonObject { ["password"] = "hunter2ABCDEF" };
        _world.Write("seed.json", new JsonObject
        {
            ["firms"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "Insolvia E2E",
                    ["users"] = new JsonArray(
                        User("e2e-admin@insolvia.test", password),
                        User("e2e-paralegal@insolvia.test", new JsonObject { ["password"] = "hunter2ABCDEF", ["role"] = "paralegal", ["isAdmin"] = false, ["accessAllCases"] = false })),
                },
                new JsonObject { ["name"] = "Other Firm LLP", ["users"] = new JsonArray(User("e2e-outsider@insolvia.test", password)) }),
        });

        Assert.Equal(0, await Run(Path.Combine(_world.Root, "seed.json"), environment: "staging"));

        Assert.Equal(3, _world.Accounts.Created.Count);
        var admin = (await _world.Firms.FindUserAsync(_world.Accounts.Subjects["e2e-admin@insolvia.test"], Ct))!;
        var paralegal = (await _world.Firms.FindUserAsync(_world.Accounts.Subjects["e2e-paralegal@insolvia.test"], Ct))!;
        var outsider = (await _world.Firms.FindUserAsync(_world.Accounts.Subjects["e2e-outsider@insolvia.test"], Ct))!;
        Assert.Equal(admin.FirmId, paralegal.FirmId);
        Assert.NotEqual(admin.FirmId, outsider.FirmId);
        Assert.False(paralegal.IsAdmin);
        Assert.False(paralegal.AccessAllCases);
    }

    // ── convergence ─────────────────────────────────────────────────

    [Fact]
    public async Task Seeding_twice_leaves_one_firm_and_one_membership()
    {
        var fixture = OneFirm(User());
        Assert.Equal(0, await Run(fixture));
        var first = (await _world.Firms.FindUserAsync(FakeAccounts.Alice, Ct))!;

        Assert.Equal(0, await Run(fixture));

        Assert.Equal(first.FirmId, (await _world.Firms.FindUserAsync(FakeAccounts.Alice, Ct))!.FirmId);
        Assert.Single(await _world.Firms.ListUsersAsync(first.FirmId, Ct));
        Assert.Single(_world.Firms.Firms);
        Assert.Contains("firm 'Example & Partners': already seeded", _world.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fixture_that_grows_a_colleague_extends_the_existing_firm()
    {
        Assert.Equal(0, await Run(OneFirm(User())));
        var firmId = (await _world.Firms.FindUserAsync(FakeAccounts.Alice, Ct))!.FirmId;

        Assert.Equal(0, await Run(OneFirm(User(), User("bob@insolvia.test"))));

        Assert.Equal(firmId, (await _world.Firms.FindUserAsync(FakeAccounts.Bob, Ct))!.FirmId);
        Assert.Equal(2, (await _world.Firms.ListUsersAsync(firmId, Ct)).Count);
    }

    [Fact]
    public async Task People_already_split_across_firms_is_refused()
    {
        Assert.Equal(0, await Run(OneFirm(User())));
        Assert.Equal(0, await Run(OneFirm(User("bob@insolvia.test"))));

        Assert.Equal(2, await Run(OneFirm(User(), User("bob@insolvia.test"))));
    }

    // ── refusals write nothing ──────────────────────────────────────

    [Fact]
    public async Task An_unknown_account_is_refused_before_the_firm_is_created()
    {
        Assert.Equal(2, await Run(OneFirm(User(), User("nobody@insolvia.test"))));

        Assert.Null(await _world.Firms.FindUserAsync(FakeAccounts.Alice, Ct));
        Assert.Empty(_world.Firms.Firms);
    }

    [Fact]
    public async Task A_firm_with_no_users_is_refused()
    {
        _world.Write("seed.json", new JsonObject { ["firms"] = new JsonArray(new JsonObject { ["name"] = "Empty LLP", ["users"] = new JsonArray() }) });

        Assert.Equal(2, await Run(Path.Combine(_world.Root, "seed.json")));
    }

    [Fact]
    public async Task A_malformed_user_is_refused_by_the_same_parser_a_route_uses()
    {
        var refusal = await Assert.ThrowsAsync<FieldValidationException>(() => Run(OneFirm(User(overrides: new JsonObject { ["role"] = "wizard" }))));

        Assert.Contains("role", refusal.Fields.Keys);
        Assert.Empty(_world.Firms.Firms);
    }

    [Fact]
    public async Task A_missing_fixture_is_refused()
    {
        Assert.Equal(2, await Run(Path.Combine(_world.Root, "absent.json")));
    }

    [Fact]
    public async Task Invalid_json_is_refused()
    {
        File.WriteAllText(Path.Combine(_world.Root, "broken.json"), "{not json");

        Assert.Equal(2, await Run(Path.Combine(_world.Root, "broken.json")));
    }

    // ── check ───────────────────────────────────────────────────────

    [Fact]
    public async Task Check_reports_missing_rows_without_writing()
    {
        Assert.Equal(1, await Run(OneFirm(User()), check: true));

        Assert.Null(await _world.Firms.FindUserAsync(FakeAccounts.Alice, Ct));
        Assert.Contains("firm 'Example & Partners': 1 user(s) missing", _world.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_succeeds_once_the_fixture_is_loaded()
    {
        var fixture = OneFirm(User());
        Assert.Equal(0, await Run(fixture));

        Assert.Equal(0, await Run(fixture, check: true));
    }

    [Fact]
    public async Task Check_is_refused_against_production_too()
    {
        // --check is what someone reaches for to "just look".
        Assert.Equal(2, await Run(OneFirm(User()), check: true, environment: "production"));
    }
}
