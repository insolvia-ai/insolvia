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
using Insolvia.Core.Sofa;
using Insolvia.Core.TaxIds;
using Insolvia.Engines;
using Insolvia.Engines.Projections;

namespace Insolvia.Forms.Tests.Support;

/// <summary>
/// Case files as Parity/*.case.json holds them — every body in the stored
/// shape, parsed here through Core's own parsers, the same trip the Python
/// generator makes through insolvia_core's. The reference cases are
/// test_form_projections.py's, so the translated targeted tests start from
/// exactly the case the Python tests do.
/// </summary>
internal static class CaseFiles
{
    private static readonly Dictionary<string, CaseFile> Cache = new(StringComparer.Ordinal);

    public static string ParityDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "CaseFiles");

    /// <summary>The Chapter 7 reference case (test_form_projections.reference_case_file).</summary>
    public static CaseFile ReferenceCh7 => Named("reference-ch7");

    /// <summary>The Chapter 13 reference case (reference_case_file_chapter_13).</summary>
    public static CaseFile ReferenceCh13 => Named("reference-ch13");

    public static CaseFile Named(string name)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(name, out var caseFile))
            {
                caseFile = Load(JsonNode.Parse(File.ReadAllText(Path.Combine(ParityDirectory, $"{name}.case.json")))!.AsObject());
                Cache[name] = caseFile;
            }

            return caseFile;
        }
    }

    public static CaseFile Load(JsonObject data)
    {
        var raw = data["case"]!.AsObject();
        var @case = new Case(
            Str(raw["id"])!,
            Str(raw["firm_id"])!,
            Str(raw["created_by"])!,
            (int)raw["chapter"]!.GetValue<long>(),
            Str(raw["district"])!,
            Wire.TryParse<CaseStatus>(Str(raw["status"]), out var status) ? status : CaseStatus.Intake,
            DateTimeOffset.Parse(Str(raw["created_at"])!, CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(Str(raw["updated_at"])!, CultureInfo.InvariantCulture))
        {
            Court = Str(raw["court"]),
            Division = Str(raw["division"]),
            ExemptionSet = Wire.TryParse<ExemptionSet>(Str(raw["exemption_set"]), out var set) ? set : null,
            FiledAt = Date(raw["filed_at"]),
            Meeting341At = Date(raw["meeting_341_at"]),
        };

        var debtors = new List<Debtor>();
        foreach (var node in data["debtors"]!.AsArray())
        {
            var item = node!.AsObject();
            var draft = DebtorRules.Parse(item["body"]!.DeepClone().AsObject(), enforceProvenance: false);
            var role = Wire.TryParse<FilingRole>(Str(item["filing_role"]), out var parsedRole) ? parsedRole : throw new InvalidDataException("filing_role");
            TaxIdRef? taxId = null;
            if (item["tax_id"] is JsonObject tax)
            {
                var kind = Wire.TryParse<TaxIdKind>(Str(tax["kind"]), out var parsedKind) ? parsedKind : throw new InvalidDataException("tax_id.kind");
                taxId = new TaxIdRef(kind, Str(tax["last_four"])!, Str(tax["ref"])!);
            }

            debtors.Add(new Debtor(
                Str(item["id"])!,
                @case.Id,
                role,
                DateTimeOffset.Parse(Str(item["created_at"])!, CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(Str(item["updated_at"])!, CultureInfo.InvariantCulture),
                draft.Body)
            {
                TaxId = taxId,
            });
        }

        var taxIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (role, digits) in data["tax_ids"]!.AsObject())
        {
            taxIds[role] = Str(digits)!;
        }

        return new CaseFile(@case)
        {
            Debtors = debtors,
            TaxIds = taxIds,
            Petition = data["petition"] is JsonObject petition ? Parse(PetitionRules.Petition, petition) : null,
            PriorCases = Bodies(data, "prior_cases", PetitionRules.PriorCase),
            RelatedCases = Bodies(data, "related_cases", PetitionRules.RelatedCase),
            SoleProprietorships = Bodies(data, "sole_proprietorships", PetitionRules.SoleProprietorship),
            FilingProfessionals = Bodies(data, "filing_professionals", PetitionRules.FilingProfessional),
            Employments = Pairs(data, "employments", IncomeRules.Employment),
            IncomeSummaries = Bodies(data, "income_summaries", IncomeRules.IncomeSummary),
            PayPeriodRecords = Bodies(data, "pay_period_records", IncomeRules.PayPeriodRecord),
            OtherIncomeRecords = Bodies(data, "other_income_records", IncomeRules.OtherIncomeRecord),
            MeansTestInputs = Bodies(data, "means_test_inputs", MeansTestInputRules.Kind),
            Assets = Pairs(data, "assets", AssetRules.Kind),
            Exemptions = Bodies(data, "exemptions", ExemptionRules.Kind),
            Creditors = Pairs(data, "creditors", CreditorRules.Kind),
            Claims = Pairs(data, "claims", ClaimRules.Kind),
            ContractLeases = Pairs(data, "contract_leases", ContractLeaseRules.Kind),
            Codebtors = Bodies(data, "codebtors", CodebtorRules.Kind),
            CommunityHouseholdMembers = Bodies(data, "community_household_members", CodebtorRules.CommunityHouseholdMember),
            Households = Pairs(data, "households", ExpenseRules.Household),
            Expenses = Bodies(data, "expenses", ExpenseRules.Expense),
            Dependents = Bodies(data, "dependents", ExpenseRules.Dependent),
            SofaEntries = Bodies(data, "sofa_entries", SofaRules.Kind),
            Plans = Bodies(data, "plans", PlanRules.Kind),
        };
    }

    public static TBody Parse<TBody>(EntityKind<TBody> kind, JsonObject body)
        where TBody : class =>
        CaseEntityRules.ParseEntity(kind, (JsonObject)body.DeepClone(), enforceProvenance: false).Body;

    /// <summary>A body from a JSON literal, through its kind's parser — the C# spelling of the Python tests' dataclass literals.</summary>
    public static TBody Body<TBody>(EntityKind<TBody> kind, string json)
        where TBody : class => Parse(kind, JsonNode.Parse(json)!.AsObject());

    private static string? Str(JsonNode? node) => Json.AsString(node);

    private static DateOnly? Date(JsonNode? node) => Py.ParseIso(Str(node));

    private static List<TBody> Bodies<TBody>(JsonObject data, string member, EntityKind<TBody> kind)
        where TBody : class =>
        [.. (data[member] as JsonArray ?? []).Select(node => Parse(kind, node!.AsObject()))];

    private static List<(string Id, TBody Body)> Pairs<TBody>(JsonObject data, string member, EntityKind<TBody> kind)
        where TBody : class =>
        [.. (data[member] as JsonArray ?? []).Select(node => (Str(node![0])!, Parse(kind, node[1]!.AsObject())))];
}
