using Insolvia.Migrator.Seeding;

namespace Insolvia.Migrator.Tests.Seeding;

// THE DERIVATION IS PYTHON'S, BYTE FOR BYTE. Each expected id below was
// computed by the Python loader's own function for the same inputs:
//
//   python3 -c "import uuid; NS=uuid.UUID('6f1c2b8e-3d4a-4f5b-9c6d-7e8f9a0b1c2d'); \
//               print(uuid.uuid5(NS, '/'.join([...])))"
//
// — so a fixture derives the ids the Python loader already seeded, and a regression in
// the uuid5 (byte order, version bits, the joiner) is a failing vector.
public sealed class SeedIdTests
{
    private const string Firm = "00000000-0000-4000-8000-00000000f18a";
    private const string Case = "31e88549-1116-5d78-96ca-145e66c4b12f";

    [Fact]
    public void A_case_id_is_pythons()
    {
        Assert.Equal(Case, SeedIds.Derive("insolvia-staging-cases", Firm, "v4", "chapter-7-sample"));
        Assert.Equal(Case, SeedIds.Derive(SeedTarget.For("staging").CaseTarget, Firm, "v4", "chapter-7-sample"));
    }

    [Fact]
    public void A_client_debtor_item_and_document_id_are_pythons()
    {
        Assert.Equal("d09de26c-3ae2-5117-ab97-c4037f525506", SeedIds.Derive(SeedTarget.For("staging").FirmTarget, Firm, "v4", "clients", "chapter-7-sample-debtor-1"));
        Assert.Equal("c583646e-1725-5263-8524-29b20cbab749", SeedIds.Derive(Case, "debtor", "debtor_1"));
        Assert.Equal("0f41e451-554c-5b59-b96e-56643bbc1ac3", SeedIds.Derive(Case, "creditors", "0"));
        Assert.Equal("dfe18ad5-0830-5c25-9275-0b7ddbd268cf", SeedIds.Derive(Case, "document", "objects/sample-pay-stub.pdf"));
    }

    [Fact]
    public void Non_ascii_parts_are_utf8_as_in_python()
    {
        Assert.Equal("62b03fc7-0778-5178-bcef-0d04a832df59", SeedIds.Derive("insolvia-local-cases", "fïrm", "v4", "é"));
    }

    [Fact]
    public void The_contract_stacks_accounts_are_the_subjects_the_local_seed_derives()
    {
        // contract/stack/stack.json tells the suite whom to sign for; the .NET
        // contract stack seeds through LocalSeedAccounts, so the two must agree.
        var stack = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Seeds, "..", "contract", "stack", "stack.json")))!;
        var accounts = stack["accounts"]!.AsObject();

        Assert.NotEmpty(accounts);
        Assert.All(accounts, account => Assert.Equal(LocalSeedAccounts.SubjectFor(account.Key), account.Value!.GetValue<string>()));
    }

    [Fact]
    public void A_derived_id_is_a_version_5_uuid_the_object_key_rule_accepts()
    {
        var id = SeedIds.Derive("anything");

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", id);
    }
}
