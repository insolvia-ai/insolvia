using System.Text.Json.Nodes;
using Insolvia.Core.Candidates;

namespace Insolvia.Core.Tests;

/// <summary>Candidates — proposals awaiting review — translated from test_candidates.py.</summary>
public sealed class CandidateTests
{
    private const string CreditorProposal = """{"entityType": "creditors", "payload": {"name": "First Example Bank"}}""";

    private static readonly CandidateOrigin Origin = new(OriginChannel.Mcp, "client-1", "subject-1");

    public static TheoryData<CandidateStatus> ReviewedStatuses() =>
        [CandidateStatus.Accepted, CandidateStatus.Corrected, CandidateStatus.Rejected, CandidateStatus.Withdrawn];

    private static Candidate NewCandidate() =>
        CandidateRules.Create(CandidateRules.ParseProposals(Body.Value($"[{CreditorProposal}]"))[0], "case-1", Origin);

    [Fact]
    public void A_valid_batch_parses_whole()
    {
        var drafts = CandidateRules.ParseProposals(Body.Value($$"""
            [{{CreditorProposal}},
             {"entityType": "claims", "payload": {}, "externalRef": {"system": "mycase", "externalId": "42", "externalUrl": "https://example.invalid/42"},
              "note": "from the PMS ledger"}]
            """));
        Assert.Equal(["creditors", "claims"], drafts.Select(d => d.EntityType));
        Assert.Equal("mycase", drafts[1].ExternalRef?.System);
        Assert.Equal("from the PMS ledger", drafts[1].Note);
    }

    [Fact]
    public void Debtor_proposals_run_the_debtor_parser() =>
        Assert.Equal("debtors", Assert.Single(CandidateRules.ParseProposals(Body.Value("""[{"entityType": "debtors", "payload": {"filing_role": "debtor_1"}}]"""))).EntityType);

    [Fact]
    public void Batch_shape_violations_are_rejected()
    {
        Assert.Throws<ValidationException>(() => CandidateRules.ParseProposals(Body.Value("\"not-a-list\"")));
        Assert.Throws<ValidationException>(() => CandidateRules.ParseProposals(new JsonArray()));
        var tooMany = new JsonArray([.. Enumerable.Range(0, CandidateRules.MaxProposalsPerCall + 1).Select(_ => Body.Value(CreditorProposal))]);
        Assert.Throws<ValidationException>(() => CandidateRules.ParseProposals(tooMany));
    }

    [Theory]
    [InlineData("\"not-an-object\"", "proposals[0]")]
    [InlineData("""{"entityType": "documents", "payload": {}}""", "proposals[0].entityType")]
    [InlineData("""{"entityType": "cases", "payload": {}}""", "proposals[0].entityType")]
    [InlineData("""{"entityType": "creditors"}""", "proposals[0].payload")]
    [InlineData("""{"entityType": "creditors", "payload": {"name": 42}}""", "proposals[0].payload.name")]
    [InlineData("""{"entityType": "creditors", "payload": {}, "note": 7}""", "proposals[0].note")]
    [InlineData("""{"entityType": "creditors", "payload": {}, "externalRef": "x"}""", "proposals[0].externalRef")]
    [InlineData("""{"entityType": "creditors", "payload": {}, "externalRef": {"system": "s"}}""", "proposals[0].externalRef.externalId")]
    public void Malformed_proposals_name_the_field(string proposal, string failingField) =>
        Assert.Contains(failingField, Assert.Throws<FieldValidationException>(() => CandidateRules.ParseProposals(Body.Value($"[{proposal}]"))).Fields.Keys);

    [Fact]
    public void A_bad_third_proposal_rejects_the_whole_batch() =>
        Assert.Contains("proposals[2].entityType", Assert.Throws<FieldValidationException>(() => CandidateRules.ParseProposals(Body.Value($$"""[{{CreditorProposal}}, {{CreditorProposal}}, {"entityType": "nope"}]"""))).Fields.Keys);

    [Fact]
    public void Documents_and_the_case_root_are_not_proposable()
    {
        Assert.DoesNotContain("documents", CandidateRules.ProposableEntityTypes);
        Assert.DoesNotContain("cases", CandidateRules.ProposableEntityTypes);
        Assert.Contains("debtors", CandidateRules.ProposableEntityTypes);
    }

    [Fact]
    public void The_proposer_withdraws_a_pending_candidate() =>
        Assert.Equal(CandidateStatus.Withdrawn, CandidateRules.Withdraw(NewCandidate(), "subject-1").Status);

    [Fact]
    public void Only_the_proposer_may_withdraw() =>
        Assert.Throws<ForbiddenException>(() => CandidateRules.Withdraw(NewCandidate(), "somebody-else"));

    [Theory]
    [MemberData(nameof(ReviewedStatuses))]
    public void A_reviewed_candidate_refuses_withdrawal(CandidateStatus status) =>
        Assert.Throws<ConflictException>(() => CandidateRules.Withdraw(NewCandidate() with { Status = status }, "subject-1"));

    // test_candidate_round_trips_through_its_item,
    // test_an_extraction_candidate_round_trips_its_extras,
    // test_a_proposal_without_extras_stores_none_of_them and the two
    // review-JSON tests: the item shape and the MCP view; dropped. The extras
    // a record carries are pinned here:

    [Fact]
    public void New_candidates_are_pending_with_the_verified_origin()
    {
        var candidate = NewCandidate();
        Assert.Equal(CandidateStatus.Pending, candidate.Status);
        Assert.Equal(Origin, candidate.Origin);
        Assert.Null(candidate.DocumentId);
        Assert.Null(candidate.Confidence);
        Assert.Null(candidate.Locator);
    }

    [Fact]
    public void An_extraction_candidate_carries_its_extras()
    {
        var draft = CandidateRules.ParseProposals(Body.Value($"[{CreditorProposal}]"))[0];
        var candidate = CandidateRules.Create(draft, "case-1", Origin with { Channel = OriginChannel.Extraction }, "doc-1", 0.87, Body.Of("""{"page": 2}"""));
        Assert.Equal(("doc-1", 0.87), (candidate.DocumentId, candidate.Confidence!.Value));
        Assert.Equal(2, (int)candidate.Locator!["page"]!);
    }

    [Fact]
    public void The_candidate_vocabularies_keep_their_wire_strings()
    {
        Assert.Equal(["pending", "accepted", "corrected", "rejected", "withdrawn"], Wire.Names<CandidateStatus>());
        Assert.Equal(["mcp", "extraction", "client"], Wire.Names<OriginChannel>());
    }

    // test_clients.py's test_client_is_an_origin_channel.
    [Fact]
    public void Client_is_an_origin_channel() => Assert.Contains("client", Wire.Names<OriginChannel>());
}
