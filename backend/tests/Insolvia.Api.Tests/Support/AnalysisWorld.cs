using System.Globalization;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Assets;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Core.Codebtors;
using Insolvia.Core.ContractLeases;
using Insolvia.Core.Creditors;
using Insolvia.Core.Debtors;
using Insolvia.Core.Exemptions;
using Insolvia.Core.Expenses;
using Insolvia.Core.Income;
using Insolvia.Core.MeansTest;
using Insolvia.Core.Petitions;
using Insolvia.Core.Plans;
using Insolvia.Core.Ports;
using Insolvia.Core.Sofa;
using Insolvia.Core.TaxIds;
using Insolvia.Engines;
using Insolvia.Engines.Projections;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insolvia.Api.Tests.Support;

/// <summary>
/// The analysis routes' fixture — test_means_test_route.py's <c>Harness</c>:
/// <see cref="CaseWorld"/>'s cast, plus a case file from the Engines parity
/// data (<c>Parity/*.case.json</c>, the Python oracle's own inputs) stored
/// straight into the stores the API composed, the way the Python harness
/// seeds <c>reference_case_data()</c>, then read back over HTTP.
/// </summary>
/// <remarks>
/// <c>pinned</c> fixes the server clock at the oracle's <c>TODAY</c>
/// (2026-10-01) — the exemption analysis and the plan resolve on "today" when
/// the petition names no expected filing date — and mints this fixture's
/// tokens at that instant, because the verifier reads the same clock.
/// </remarks>
internal sealed class AnalysisWorld : IDisposable
{
    /// <summary>The oracle's <c>TODAY</c> (the date Parity/*.expected.json was computed for), noon UTC.</summary>
    public static readonly DateTimeOffset ParityToday = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The four case files the oracle ran over.</summary>
    public static readonly string[] ParityCaseFiles = ["reference-ch7", "reference-ch13", "chapter-7-sample", "chapter-13-sample"];

    /// <summary>What test_means_test_route.py's <c>seed_reference_case</c> stores.</summary>
    public static readonly string[] MeansTestCollections =
        ["petitions", "employments", "pay_period_records", "other_income_records", "means_test_inputs", "assets", "creditors", "claims", "households", "dependents"];

    /// <summary>test_plan_route.py's <c>seed_chapter_13</c>: the means test's, and the records the plan reads beyond them.</summary>
    public static readonly string[] PlanCollections =
        [.. MeansTestCollections, "exemptions", "income_summaries", "expenses", "sofa_entries"];

    // Case-file member -> its kind, and the id prefix of a bare body (null:
    // the file carries [id, body] pairs) — test_packet_assembly.py's
    // `reference_case_data` ids, so `exemption-0` is the oracle's too.
    private static readonly (string Member, IEntityKind Kind, string? Prefix)[] Members =
    [
        ("prior_cases", PetitionRules.PriorCase, "prior"),
        ("related_cases", PetitionRules.RelatedCase, "related"),
        ("sole_proprietorships", PetitionRules.SoleProprietorship, "soleprop"),
        ("filing_professionals", PetitionRules.FilingProfessional, "prof"),
        ("employments", IncomeRules.Employment, null),
        ("income_summaries", IncomeRules.IncomeSummary, "income"),
        ("pay_period_records", IncomeRules.PayPeriodRecord, "payperiod"),
        ("other_income_records", IncomeRules.OtherIncomeRecord, "otherincome"),
        ("means_test_inputs", MeansTestInputRules.Kind, "meanstest"),
        ("assets", AssetRules.Kind, null),
        ("exemptions", ExemptionRules.Kind, "exemption"),
        ("creditors", CreditorRules.Kind, null),
        ("claims", ClaimRules.Kind, null),
        ("contract_leases", ContractLeaseRules.Kind, null),
        ("codebtors", CodebtorRules.Kind, "codebtor"),
        ("community_household_members", CodebtorRules.CommunityHouseholdMember, "member"),
        ("households", ExpenseRules.Household, null),
        ("expenses", ExpenseRules.Expense, "expense"),
        ("dependents", ExpenseRules.Dependent, "dependent"),
        ("sofa_entries", SofaRules.Kind, "sofa"),
        ("plans", PlanRules.Kind, "plan"),
    ];

    private readonly bool _pinned;

    public AnalysisWorld(bool pinned = false)
    {
        _pinned = pinned;
        World = new CaseWorld(pinned
            ? services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new PinnedClock(ParityToday));
            }
        : null);
    }

    public CaseWorld World { get; }

    public static string ParityDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Parity");

    public ICaseEntityStore Entities => World.Service<ICaseEntityStore>();

    public static JsonObject CaseFileJson(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ParityDirectory, $"{name}.case.json")))!.AsObject();

    /// <summary>The oracle's output for one case file (Parity/&lt;case&gt;.expected.json).</summary>
    public static JsonObject Expected(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ParityDirectory, $"{name}.expected.json")))!.AsObject();

    public async Task<AnalysisWorld> InitializeAsync()
    {
        await World.SeedAsync();
        return this;
    }

    /// <summary>
    /// The case file <paramref name="name"/> in the stores, owned by Firm A
    /// and created by Alice; every collection unless <paramref name="collections"/>
    /// names a subset. Returns the case id.
    /// </summary>
    public async Task<string> SeedAsync(string name, int? chapter = null, IReadOnlyCollection<string>? collections = null, DateTimeOffset? createdAt = null, string caseId = Ids.Case)
    {
        var data = CaseFileJson(name);
        var raw = data["case"]!.AsObject();
        var created = createdAt ?? Stamp(raw["created_at"]);
        var stored = new Case(
            caseId,
            Ids.FirmA,
            CaseWorld.Alice,
            chapter ?? (int)raw["chapter"]!.GetValue<long>(),
            Json.AsString(raw["district"])!,
            Wire.TryParse<CaseStatus>(Json.AsString(raw["status"]), out var status) ? status : CaseStatus.Intake,
            created,
            created)
        {
            Court = Json.AsString(raw["court"]),
            Division = Json.AsString(raw["division"]),
            ExemptionSet = Wire.TryParse<ExemptionSet>(Json.AsString(raw["exemption_set"]), out var set) ? set : null,
            FiledAt = Exact.ParseIso(Json.AsString(raw["filed_at"])),
            Meeting341At = Exact.ParseIso(Json.AsString(raw["meeting_341_at"])),
        };

        var debtors = new List<Debtor>();
        foreach (var node in data["debtors"]!.AsArray())
        {
            var item = node!.AsObject();
            var draft = DebtorRules.Parse(item["body"]!.DeepClone().AsObject(), enforceProvenance: false);
            Assert.True(Wire.TryParse<FilingRole>(Json.AsString(item["filing_role"]), out var role));
            TaxIdRef? taxId = null;
            if (item["tax_id"] is JsonObject tax)
            {
                Assert.True(Wire.TryParse<TaxIdKind>(Json.AsString(tax["kind"]), out var kind));
                taxId = new TaxIdRef(kind, Json.AsString(tax["last_four"])!, Json.AsString(tax["ref"])!);
            }

            debtors.Add(new Debtor(Json.AsString(item["id"])!, caseId, role, Stamp(item["created_at"]), Stamp(item["updated_at"]), draft.Body) { TaxId = taxId });
        }

        await World.Cases.CreateAsync(stored, new CaseAssignment(caseId, CaseWorld.Alice, created, created, CaseWorld.Alice), debtors, HttpAssert.Ct);

        var position = 0;
        bool Wanted(string member) => collections is null || collections.Contains(member);
        async Task StoreAsync(IEntityKind kind, string id, JsonNode body)
        {
            // Creation order is print order: one second per record keeps the
            // stores' (created_at, id) listing in the file's order.
            var at = created.AddSeconds(position++);
            var entity = kind.Restore(id, caseId, at, at, kind.ParseDraft((JsonObject)body.DeepClone(), enforceProvenance: false));
            await Entities.CreateAsync(entity, HttpAssert.Ct);
        }

        if (Wanted("petitions") && data["petition"] is JsonObject petition)
        {
            await StoreAsync(PetitionRules.Petition, "petition-0", petition);
        }

        foreach (var (member, kind, prefix) in Members)
        {
            if (!Wanted(member))
            {
                continue;
            }

            var items = data[member] as JsonArray ?? [];
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index]!;
                if (prefix is null)
                {
                    await StoreAsync(kind, Json.AsString(item[0])!, item[1]!);
                }
                else
                {
                    await StoreAsync(kind, $"{prefix}-{index}", item);
                }
            }
        }

        return caseId;
    }

    /// <summary>The case file as the stores now hold it — test_means_test_route.py's <c>harness.case_file</c>.</summary>
    public async Task<CaseFile> CaseFileAsync(string caseId)
    {
        var stored = (await World.StoredAsync(caseId))!;
        var debtors = await World.Service<IDebtorStore>().ListForCaseAsync(caseId, HttpAssert.Ct);
        async Task<List<TBody>> Bodies<TBody>(EntityKind<TBody> kind)
            where TBody : class => [.. (await Entities.ListTypedAsync(caseId, kind, HttpAssert.Ct)).Select(entity => entity.Body)];
        async Task<List<(string Id, TBody Body)>> Pairs<TBody>(EntityKind<TBody> kind)
            where TBody : class => [.. (await Entities.ListTypedAsync(caseId, kind, HttpAssert.Ct)).Select(entity => (entity.Id, entity.Body))];
        var petitions = await Bodies(PetitionRules.Petition);
        return new CaseFile(stored)
        {
            Debtors = debtors,
            Petition = petitions.Count > 0 ? petitions[0] : null,
            PriorCases = await Bodies(PetitionRules.PriorCase),
            RelatedCases = await Bodies(PetitionRules.RelatedCase),
            SoleProprietorships = await Bodies(PetitionRules.SoleProprietorship),
            FilingProfessionals = await Bodies(PetitionRules.FilingProfessional),
            Employments = await Pairs(IncomeRules.Employment),
            IncomeSummaries = await Bodies(IncomeRules.IncomeSummary),
            PayPeriodRecords = await Bodies(IncomeRules.PayPeriodRecord),
            OtherIncomeRecords = await Bodies(IncomeRules.OtherIncomeRecord),
            MeansTestInputs = await Bodies(MeansTestInputRules.Kind),
            Assets = await Pairs(AssetRules.Kind),
            Exemptions = await Bodies(ExemptionRules.Kind),
            Creditors = await Pairs(CreditorRules.Kind),
            Claims = await Pairs(ClaimRules.Kind),
            ContractLeases = await Pairs(ContractLeaseRules.Kind),
            Codebtors = await Bodies(CodebtorRules.Kind),
            CommunityHouseholdMembers = await Bodies(CodebtorRules.CommunityHouseholdMember),
            Households = await Pairs(ExpenseRules.Household),
            Expenses = await Bodies(ExpenseRules.Expense),
            Dependents = await Bodies(ExpenseRules.Dependent),
            SofaEntries = await Bodies(SofaRules.Kind),
            Plans = await Bodies(PlanRules.Kind),
        };
    }

    /// <summary>A record stored directly — the Python tests' <c>entity_store.create(create_entity(...))</c>.</summary>
    public async Task<string> StoreAsync(IEntityKind kind, string caseId, string json, string? id = null)
    {
        var now = _pinned ? ParityToday : DateTimeOffset.UtcNow;
        var entity = kind.Restore(id ?? Guid.NewGuid().ToString(), caseId, now, now, kind.ParseDraft(JsonNode.Parse(json)!.AsObject()));
        await Entities.CreateAsync(entity, HttpAssert.Ct);
        return entity.Id;
    }

    /// <summary>
    /// The route suites' <c>{**body, "provenance": dict.fromkeys(body, TYPED)}</c>:
    /// <paramref name="json"/> with every top-level member marked staff-typed.
    /// </summary>
    public static string Typed(string json)
    {
        var body = JsonNode.Parse(json)!.AsObject();
        var provenance = new JsonObject();
        foreach (var (key, _) in body)
        {
            provenance[key] = new JsonObject { ["source"] = "staff_typed" };
        }

        body["provenance"] = provenance;
        return body.ToJsonString();
    }

    /// <summary>test_*_route.py's <c>open_case</c>: a case opened over HTTP (an unpinned world only), its id.</summary>
    public async Task<string> OpenCaseAsync(string subject = CaseWorld.Alice, string court = "flmb", string division = "tampa") =>
        (await World.OpenCaseAsync(subject, court: court, division: division)).Str("id")!;

    /// <summary>test_*_route.py's <c>add</c>: a record through the generic collection route, its id.</summary>
    public async Task<string> AddAsync(string caseId, string collection, string json)
    {
        var answer = await Post($"/v1/cases/{caseId}/{collection}", Typed(json));
        Assert.True(answer.Status == 201, answer.Body);
        return answer.Str("id")!;
    }

    public Task<Answer> Get(string path, string? subject = CaseWorld.Alice) => Send(HttpMethod.Get, path, subject, null);

    public Task<Answer> Post(string path, string? json, string? subject = CaseWorld.Alice) => Send(HttpMethod.Post, path, subject, json);

    public Task<Answer> Put(string path, string? json, string? subject = CaseWorld.Alice) => Send(HttpMethod.Put, path, subject, json);

    public Task<Answer> Patch(string path, string? json, string? subject = CaseWorld.Alice) => Send(HttpMethod.Patch, path, subject, json);

    public void Dispose() => World.Dispose();

    private static DateTimeOffset Stamp(JsonNode? node) => DateTimeOffset.Parse(Json.AsString(node)!, CultureInfo.InvariantCulture);

    private Task<Answer> Send(HttpMethod method, string path, string? subject, string? json)
    {
        if (!_pinned || subject is null)
        {
            return World.World.SendAsync(method, path, subject, json);
        }

        var at = ParityToday.ToUnixTimeSeconds();
        var token = Tokens.Make(subject, extra: new JsonObject { ["iat"] = at, ["auth_time"] = at, ["exp"] = at + 3600 });
        return World.World.SendAsync(method, path, json: json, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + token });
    }

    private sealed class PinnedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
