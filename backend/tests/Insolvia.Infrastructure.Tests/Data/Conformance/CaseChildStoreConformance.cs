using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Candidates;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.CaseTasks;
using Insolvia.Core.Creditors;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Documents;
using Insolvia.Core.Fields;
using Insolvia.Core.Notes;
using Insolvia.Core.Ports;
using Insolvia.Core.Provenance;
using Insolvia.Core.TaxIds;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What every case-scoped child store must do — the generic collections
/// (notes included), documents, candidates, tasks, document requests and the
/// sealed tax ids: once-only create, write-back only over a row that still
/// exists, a delete that answers once, the case (and kind) scope, each
/// listing's order, and the candidate review's compare-and-swap with its race.
/// </summary>
public abstract class CaseChildStoreConformance : CaseConformance
{
    // ── The generic collections ─────────────────────────────────────

    [Fact]
    public async Task An_entity_round_trips_body_provenance_and_amended()
    {
        var owner = await CreateCaseAsync();
        var creditor = Creditor(owner.Id, "Example Bank") with { Amended = true };
        await Connect().Entities.CreateAsync(creditor, Ct);
        Assert.Equal(creditor, await Connect().Entities.GetTypedAsync(owner.Id, CreditorRules.Kind, creditor.Id, Ct));
    }

    [Fact]
    public async Task An_entity_is_created_once_and_written_back_only_while_it_exists()
    {
        var owner = await CreateCaseAsync();
        var creditor = Creditor(owner.Id, "Example Bank");
        await Connect().Entities.CreateAsync(creditor, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Entities.CreateAsync(creditor with { Body = creditor.Body with { Name = "Other" } }, Ct));

        var renamed = creditor with { Body = creditor.Body with { Name = "Renamed Bank" }, UpdatedAt = Opened.AddHours(1) };
        Assert.True(await Connect().Entities.PutAsync(renamed, Ct));
        Assert.Equal(renamed, await Connect().Entities.GetTypedAsync(owner.Id, CreditorRules.Kind, creditor.Id, Ct));
        Assert.True(await Connect().Entities.DeleteAsync(owner.Id, CreditorRules.Kind, creditor.Id, Ct));
        Assert.False(await Connect().Entities.DeleteAsync(owner.Id, CreditorRules.Kind, creditor.Id, Ct));
        Assert.False(await Connect().Entities.PutAsync(renamed, Ct));
        Assert.Null(await Connect().Entities.GetAsync(owner.Id, CreditorRules.Kind, creditor.Id, Ct));
    }

    [Fact]
    public async Task A_collection_is_scoped_to_its_case_and_its_kind_and_lists_in_creation_order()
    {
        var owner = await CreateCaseAsync();
        var other = await CreateCaseAsync();
        var later = Creditor(owner.Id, "Later", "00000000-0000-4000-8000-0000000000e1", Opened.AddMinutes(1));
        var tieB = Creditor(owner.Id, "Tie B", "00000000-0000-4000-8000-0000000000eb", Opened);
        var tieA = Creditor(owner.Id, "Tie A", "00000000-0000-4000-8000-0000000000ea", Opened);
        foreach (var creditor in new[] { later, tieB, tieA, Creditor(other.Id, "Elsewhere") })
        {
            await Connect().Entities.CreateAsync(creditor, Ct);
        }

        // The same id under another kind is another record.
        var note = new CaseEntity<NoteBody>(NoteRules.Kind, tieA.Id, owner.Id, Opened, Opened, new NoteBody("Called the trustee.", null, Ids.Alice, "Alice Attorney"), ValueCollections.EmptyDictionary<string, ProvenanceEntry>());
        await Connect().Entities.CreateAsync(note, Ct);

        Assert.Equal([tieA, tieB, later], await Connect().Entities.ListTypedAsync(owner.Id, CreditorRules.Kind, Ct));
        Assert.Equal([note], await Connect().Entities.ListTypedAsync(owner.Id, NoteRules.Kind, Ct));
        Assert.Null(await Connect().Entities.GetAsync(other.Id, CreditorRules.Kind, tieA.Id, Ct));
    }

    // ── Documents ───────────────────────────────────────────────────

    [Fact]
    public async Task A_document_is_created_once_confirmed_only_while_it_exists_and_deleted_once()
    {
        var owner = await CreateCaseAsync();
        var document = Document(owner.Id);
        await Connect().Documents.CreateAsync(document, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Documents.CreateAsync(document, Ct));

        var stored = document with { Status = DocumentStatus.Stored, Etag = "\"etag-1\"" };
        Assert.Equal(stored, await Connect().Documents.UpdateAsync(stored, Ct));
        Assert.Equal(stored, await Connect().Documents.GetAsync(owner.Id, document.Id, Ct));
        Assert.True(await Connect().Documents.DeleteAsync(owner.Id, document.Id, Ct));
        Assert.False(await Connect().Documents.DeleteAsync(owner.Id, document.Id, Ct));
        Assert.Null(await Connect().Documents.UpdateAsync(stored, Ct));
        Assert.Null(await Connect().Documents.GetAsync(owner.Id, document.Id, Ct));
    }

    [Fact]
    public async Task Documents_list_newest_first_ties_by_id_descending()
    {
        var owner = await CreateCaseAsync();
        var older = Document(owner.Id, "00000000-0000-4000-8000-0000000000d9", Opened);
        var tieA = Document(owner.Id, "00000000-0000-4000-8000-0000000000da", Opened.AddMinutes(1)) with { Channel = DocumentChannel.Client, RequestId = "request-1" };
        var tieB = Document(owner.Id, "00000000-0000-4000-8000-0000000000db", Opened.AddMinutes(1));
        foreach (var document in new[] { older, tieA, tieB })
        {
            await Connect().Documents.CreateAsync(document, Ct);
        }

        Assert.Equal([tieB, tieA, older], await Connect().Documents.ListForCaseAsync(owner.Id, Ct));
    }

    // ── Candidates ──────────────────────────────────────────────────

    [Fact]
    public async Task A_candidate_round_trips_its_payloads_and_is_created_once()
    {
        var owner = await CreateCaseAsync();
        var candidate = Candidate(owner.Id) with
        {
            ExternalRef = new ExternalRef("harness", "ext-1", "https://example.test/1"),
            Note = "From the bank statement.",
            Confidence = 0.875,
            Locator = new JsonObject { ["page"] = 2 },
            DocumentId = "doc-1",
        };
        await Connect().Candidates.CreateAsync(candidate, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Candidates.CreateAsync(candidate, Ct));
        AssertSame(candidate, await Connect().Candidates.GetAsync(owner.Id, candidate.Id, Ct));
    }

    [Fact]
    public async Task A_review_is_a_compare_and_swap_on_the_status_it_read()
    {
        var owner = await CreateCaseAsync();
        var candidate = Candidate(owner.Id);
        await Connect().Candidates.CreateAsync(candidate, Ct);
        var accepted = candidate with
        {
            Status = CandidateStatus.Corrected,
            ConfirmedBy = Ids.Alice,
            ConfirmedAt = "2026-10-02T00:00:00.000000Z",
            CorrectedPayload = new JsonObject { ["name"] = "Example Bank N.A." },
            ResultingRecordId = "creditor-1",
        };

        Assert.Null(await Connect().Candidates.UpdateAsync(accepted, CandidateStatus.Withdrawn, Ct));
        AssertSame(accepted, await Connect().Candidates.UpdateAsync(accepted, CandidateStatus.Pending, Ct));
        Assert.Null(await Connect().Candidates.UpdateAsync(candidate with { Status = CandidateStatus.Withdrawn }, CandidateStatus.Pending, Ct));
        Assert.Null(await Connect().Candidates.UpdateAsync(Candidate(owner.Id), CandidateStatus.Pending, Ct));
        AssertSame(accepted, await Connect().Candidates.GetAsync(owner.Id, candidate.Id, Ct));
    }

    [Fact]
    public async Task Of_a_review_and_a_withdrawal_racing_from_pending_exactly_one_lands()
    {
        var owner = await CreateCaseAsync();
        var candidate = Candidate(owner.Id);
        await Connect().Candidates.CreateAsync(candidate, Ct);
        var outcomes = await Task.WhenAll(
            Connect().Candidates.UpdateAsync(candidate with { Status = CandidateStatus.Accepted }, CandidateStatus.Pending, Ct),
            Connect().Candidates.UpdateAsync(candidate with { Status = CandidateStatus.Withdrawn }, CandidateStatus.Pending, Ct));
        var winner = Assert.Single(outcomes, outcome => outcome is not null);
        Assert.Equal(winner!.Status, (await Connect().Candidates.GetAsync(owner.Id, candidate.Id, Ct))!.Status);
    }

    [Fact]
    public async Task Candidates_list_in_creation_order()
    {
        var owner = await CreateCaseAsync();
        var second = Candidate(owner.Id, Opened.AddMinutes(1));
        var first = Candidate(owner.Id, Opened);
        await Connect().Candidates.CreateAsync(second, Ct);
        await Connect().Candidates.CreateAsync(first, Ct);
        Assert.Equal([first.Id, second.Id], (await Connect().Candidates.ListForCaseAsync(owner.Id, Ct)).Select(found => found.Id));
    }

    // ── Tasks and document requests ─────────────────────────────────

    [Fact]
    public async Task A_task_is_created_once_updated_only_while_it_exists_and_listed_in_creation_order()
    {
        var owner = await CreateCaseAsync();
        var later = MakeTask(owner.Id, Opened.AddMinutes(1));
        var task = MakeTask(owner.Id, Opened);
        await Connect().Tasks.CreateAsync(later, Ct);
        await Connect().Tasks.CreateAsync(task, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Tasks.CreateAsync(task, Ct));

        var done = task with { Done = true, CompletedAt = Opened.AddHours(2), UpdatedAt = Opened.AddHours(2) };
        Assert.Equal(done, await Connect().Tasks.UpdateAsync(done, Ct));
        Assert.Equal([done, later], await Connect().Tasks.ListForCaseAsync(owner.Id, Ct));
        Assert.True(await Connect().Tasks.DeleteAsync(owner.Id, task.Id, Ct));
        Assert.False(await Connect().Tasks.DeleteAsync(owner.Id, task.Id, Ct));
        Assert.Null(await Connect().Tasks.UpdateAsync(done, Ct));
    }

    [Fact]
    public async Task Tasks_across_cases_are_read_together_and_only_from_the_cases_named()
    {
        var first = await CreateCaseAsync();
        var second = await CreateCaseAsync();
        var unnamed = await CreateCaseAsync();
        var a = MakeTask(first.Id, Opened);
        var b = MakeTask(second.Id, Opened.AddMinutes(1));
        await Connect().Tasks.CreateAsync(a, Ct);
        await Connect().Tasks.CreateAsync(b, Ct);
        await Connect().Tasks.CreateAsync(MakeTask(unnamed.Id, Opened), Ct);

        var listed = await Connect().Tasks.ListForCasesAsync([first.Id, second.Id], Ct);

        Assert.Equal([a, b], listed.OrderBy(task => task.CreatedAt));
        Assert.Empty(await Connect().Tasks.ListForCasesAsync([], Ct));
    }

    [Fact]
    public async Task A_document_request_keeps_its_answers_in_order()
    {
        var owner = await CreateCaseAsync();
        var request = Request(owner.Id, Opened);
        await Connect().Requests.CreateAsync(request, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Requests.CreateAsync(request, Ct));

        var received = request with { Status = DocumentRequestStatus.Received, DocumentIds = ["doc-b", "doc-a"], ReceivedAt = Opened.AddDays(1), UpdatedAt = Opened.AddDays(1) };
        Assert.Equal(received, await Connect().Requests.UpdateAsync(received, Ct));
        var earlier = Request(owner.Id, Opened.AddMinutes(-1));
        await Connect().Requests.CreateAsync(earlier, Ct);
        Assert.Equal([earlier, received], await Connect().Requests.ListForCaseAsync(owner.Id, Ct));
        Assert.True(await Connect().Requests.DeleteAsync(owner.Id, request.Id, Ct));
        Assert.False(await Connect().Requests.DeleteAsync(owner.Id, request.Id, Ct));
        Assert.Null(await Connect().Requests.UpdateAsync(received, Ct));
    }

    // ── Sealed tax ids ──────────────────────────────────────────────

    [Fact]
    public async Task A_sealed_tax_id_round_trips_its_envelope_is_replaced_by_ref_and_is_found_only_where_it_lives()
    {
        var owner = await CreateCaseAsync();
        var sealedId = Sealed("ref-1", "4321");
        await Connect().TaxIds.PutAsync(owner.Id, sealedId, Ct);
        Assert.Equal(sealedId, await Connect().TaxIds.GetAsync(owner.Id, "ref-1", Ct));

        var corrected = Sealed("ref-1", "8765") with { UpdatedAt = Opened.AddDays(1) };
        await Connect().TaxIds.PutAsync(owner.Id, corrected, Ct);
        Assert.Equal(corrected, await Connect().TaxIds.GetAsync(owner.Id, "ref-1", Ct));
        Assert.Null(await Connect().TaxIds.GetAsync((await CreateCaseAsync()).Id, "ref-1", Ct));
        Assert.Null(await Connect().TaxIds.GetAsync(owner.Id, "ref-2", Ct));
    }

    // ── Builders ────────────────────────────────────────────────────

    private static CaseEntity<CreditorBody> Creditor(string caseId, string name, string? id = null, DateTimeOffset? at = null) =>
        new(
            CreditorRules.Kind,
            id ?? NewId(),
            caseId,
            at ?? Opened,
            at ?? Opened,
            new CreditorBody(name, new Address("1 Bank Plaza", City: "Tampa", State: "FL", PostalCode: "33602")),
            new Dictionary<string, ProvenanceEntry>
            {
                ["name"] = new(ProvenanceSource.StaffTyped),
                ["address.line1"] = new(ProvenanceSource.StaffTyped),
                ["address.city"] = new(ProvenanceSource.StaffTyped),
                ["address.state"] = new(ProvenanceSource.StaffTyped),
                ["address.postal_code"] = new(ProvenanceSource.StaffTyped),
            }.ToValueDictionary());

    private static Document Document(string caseId, string? id = null, DateTimeOffset? at = null)
    {
        var documentId = id ?? NewId();
        return new(documentId, caseId, DocumentKind.BankStatement, "statement.pdf", DocumentContentType.Pdf, 12_345, $"cases/{caseId}/documents/{documentId}", Ids.Alice, at ?? Opened);
    }

    private static Candidate Candidate(string caseId, DateTimeOffset? at = null) =>
        new(
            NewId(),
            caseId,
            "creditor",
            new JsonObject { ["name"] = "Example Bank", ["address"] = new JsonObject { ["city"] = "Tampa", ["line1"] = "1 Bank Plaza" } },
            CandidateStatus.Pending,
            new CandidateOrigin(OriginChannel.Mcp, "harness-client", Ids.Alice),
            at ?? Opened,
            at ?? Opened);

    private static CaseTask MakeTask(string caseId, DateTimeOffset at) =>
        new(NewId(), caseId, "Collect pay stubs", "The last 60 days.", new DateOnly(2026, 11, 1), Ids.Kim, "B106I", false, null, at, at, Ids.Alice);

    private static DocumentRequest Request(string caseId, DateTimeOffset at) =>
        new(NewId(), caseId, "Pay stubs", DocumentKind.PayStub, "The last 60 days.", DocumentRequestStatus.Requested, [], at, Ids.Alice, at);

    private static SealedTaxId Sealed(string reference, string lastFour) =>
        new(
            reference,
            Ids.Firm,
            TaxIdKind.Ssn,
            lastFour,
            new Envelope(TaxIdRules.SealedScheme, Convert.ToBase64String("not a real ciphertext"u8.ToArray()), Convert.ToBase64String(new byte[12]), Convert.ToBase64String(new byte[] { 1, 2, 3, 255 })),
            Opened,
            Opened);

    // A candidate holds JsonObjects, which compare by reference; compared
    // as JSON trees instead (jsonb keeps no key order — neither did DynamoDB).
    private static void AssertSame(Candidate expected, Candidate? actual)
    {
        Assert.NotNull(actual);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General);
        Assert.True(
            JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected, options), JsonSerializer.SerializeToNode(actual, options)),
            $"{JsonSerializer.Serialize(expected, options)}\n!=\n{JsonSerializer.Serialize(actual, options)}");
    }
}

/// <summary>The oracles: the memory child stores.</summary>
public sealed class MemoryCaseChildStoreConformance : CaseChildStoreConformance
{
    private readonly MemoryCaseWorld _world = new();

    private protected override CaseStores Connect() => _world.Connect();
}

/// <summary>The Postgres child stores on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresCaseChildStoreConformance(PostgresFixture postgres) : CaseChildStoreConformance
{
    private readonly PostgresCaseWorld _world = new(postgres);

    public override async ValueTask InitializeAsync()
    {
        await _world.InitializeAsync();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _world.DisposeAsync();
        await base.DisposeAsync();
    }

    private protected override CaseStores Connect() => _world.Connect();
}
