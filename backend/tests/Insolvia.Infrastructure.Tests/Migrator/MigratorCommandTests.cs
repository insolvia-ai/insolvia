using System.Text.Json;

namespace Insolvia.Migrator.Tests;

// The commands, from a shell and from a Lambda payload — the same three,
// because on a deployed environment the Lambda is the only thing that
// reaches the database.
public sealed class MigratorCommandTests
{
    [Fact]
    public void No_arguments_and_an_empty_payload_are_migrate()
    {
        Assert.Equal(new MigratorCommand("migrate"), MigratorCommand.FromArguments([]));
        Assert.Equal(new MigratorCommand("migrate"), MigratorCommand.FromPayload(JsonDocument.Parse("{}").RootElement));
        Assert.Equal(new MigratorCommand("migrate"), MigratorCommand.FromPayload(JsonDocument.Parse("null").RootElement));
    }

    [Fact]
    public void Seed_and_purge_take_their_flags_from_either_source()
    {
        Assert.Equal(new MigratorCommand("seed", Check: true, Fixture: "../seeds/dev.json"), MigratorCommand.FromArguments(["seed", "--fixture", "../seeds/dev.json", "--check"]));
        Assert.Equal(new MigratorCommand("purge-pre-client", Apply: true), MigratorCommand.FromArguments(["purge-pre-client", "--apply"]));
        Assert.Equal(
            new MigratorCommand("purge-pre-client", Apply: true),
            MigratorCommand.FromPayload(JsonDocument.Parse("""{"command": "purge-pre-client", "apply": true}""").RootElement));
        Assert.Equal(
            new MigratorCommand("seed", Check: true),
            MigratorCommand.FromPayload(JsonDocument.Parse("""{"command": "seed", "check": true}""").RootElement));
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("seed", "--fixture")]
    [InlineData("seed", "--wat")]
    [InlineData("seed-publish", "--version", "seeds/fixtures/v4")]
    [InlineData("seed", "--fixture-bucket", "insolvia-shared-dev-fixtures-us-east-1")]
    public void A_malformed_command_line_is_refused(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => MigratorCommand.FromArguments(args));
    }

    [Fact]
    public void Publish_needs_its_version_and_bucket()
    {
        Assert.Equal(
            new MigratorCommand("seed-publish", Version: "seeds/fixtures/v4", FixtureBucket: "insolvia-shared-dev-fixtures-us-east-1"),
            MigratorCommand.FromArguments(["seed-publish", "--version", "seeds/fixtures/v4", "--fixture-bucket", "insolvia-shared-dev-fixtures-us-east-1"]));
    }
}
