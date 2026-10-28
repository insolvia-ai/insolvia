using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Core.Courts;
using Insolvia.Core.Debtors;
using Insolvia.Core.Expenses;
using Insolvia.Core.Petitions;
using Insolvia.Engines;
using Insolvia.Engines.CreditorMatrix;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Projections.Forms;
using Insolvia.Engines.Regulatory;

namespace Insolvia.Forms;

/// <summary>
/// One reason the case cannot yield a compliant packet: the source
/// collection (or "case"/"debtors", or a form series id), the record to fix
/// when one owns it, the body path, the message.
/// </summary>
public sealed record PacketProblem(string Source, string? ItemId, string Field, string Message);

/// <summary>
/// A clean assembly: the parts in filing order (zip entry name to exact
/// bytes), the pins the case and the packet record store, and the projected
/// values each form was rendered from (the review worker reads these).
/// </summary>
public sealed record AssembledPacket(
    IReadOnlyList<(string Name, byte[] Content)> Parts,
    IReadOnlyDictionary<string, string> FormRevisions,
    string ConstantsSetId,
    int CreditorCount,
    IReadOnlyDictionary<string, FieldValues> Projections);

/// <summary>
/// Packet assembly's pure half (from <c>core/packet_assembly.py</c>): the
/// completeness gate, which forms a case files, the amendment rules, the
/// deterministic render and the deterministic zip. The worker's store
/// calls, the logged tax-id read and the job envelope are 18.17's.
/// </summary>
public static class PacketAssembly
{
    /// <summary>The job kind the accept endpoint validates.</summary>
    public const string PacketAssemblyKind = "packet_assembly";

    /// <summary>The individual Chapter 7 set, in filing order.</summary>
    public static readonly IReadOnlyList<string> PacketFormSeries =
    [
        "form/b101", "form/b121", "form/b106sum", "form/b106ab", "form/b106c", "form/b106d", "form/b106ef",
        "form/b106g", "form/b106h", "form/b106i", "form/b106j", "form/b106j2", "form/b106dec", "form/b107",
        "form/b108", "form/b2010", "form/b2030", "form/b122a1", "form/b122a2",
    ];

    /// <summary>The individual Chapter 13 set, in filing order: the B122C pair, no B108, B113 last.</summary>
    public static readonly IReadOnlyList<string> Chapter13FormSeries =
    [
        "form/b101", "form/b121", "form/b106sum", "form/b106ab", "form/b106c", "form/b106d", "form/b106ef",
        "form/b106g", "form/b106h", "form/b106i", "form/b106j", "form/b106j2", "form/b106dec", "form/b107",
        "form/b2010", "form/b2030", "form/b122c1", "form/b122c2", "form/b113",
    ];

    /// <summary>Every series a case may pin: the Chapter 7 set, then the Chapter 13 additions.</summary>
    public static readonly IReadOnlyList<string> AllFormSeries =
        [.. PacketFormSeries, .. Chapter13FormSeries.Where(series => !PacketFormSeries.Contains(series))];

    /// <summary>The schedules an amendment narrows to their amended items.</summary>
    public static readonly IReadOnlyList<string> AmendableSeries =
    [
        "form/b106ab", "form/b106c", "form/b106d", "form/b106ef", "form/b106g", "form/b106h", "form/b106i",
        "form/b106j", "form/b106j2", "form/b107",
    ];

    private static readonly string[] AlwaysWithAmendment = ["form/b106sum", "form/b106dec"];

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    })];

    /// <summary>The unconditional set a chapter files.</summary>
    public static IReadOnlyList<string> ChapterFormSeries(int chapter) => chapter == 13 ? Chapter13FormSeries : PacketFormSeries;

    /// <summary>A form's name inside the zip: its 1-based position in the case's own set, and its short key.</summary>
    public static string PartFileName(int position, string form) => $"{position:00}-{form}.pdf";

    /// <summary>The wire shape of a problem — absent, never null.</summary>
    public static JsonObject ProblemJson(PacketProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var body = new JsonObject { ["source"] = problem.Source, ["message"] = problem.Message };
        if (problem.ItemId is { } itemId)
        {
            body["itemId"] = itemId;
        }

        if (problem.Field.Length > 0)
        {
            body["field"] = problem.Field;
        }

        return body;
    }

    // ── the completeness gate ────────────────────────────────────────────────

    private static List<PacketProblem> ReferenceProblems(CaseData data)
    {
        var problems = new List<PacketProblem>();
        var debtorIds = data.Debtors.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var creditorIds = data.Creditors.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var assetIds = data.Assets.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var householdIds = data.Households.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var claimIds = data.Claims.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var contractIds = data.ContractLeases.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        void Dangle(string source, string itemId, string field, string target) => problems.Add(new PacketProblem(
            source,
            itemId,
            field,
            $"References a {target} record that does not exist — fix the reference or re-enter the record it pointed at."));

        foreach (var claim in data.Claims)
        {
            if (claim.Body.CreditorId is { } creditor && !creditorIds.Contains(creditor))
            {
                Dangle("claims", claim.Id, "creditor_id", "creditor");
            }

            if (claim.Body.AssetId is { } asset && !assetIds.Contains(asset))
            {
                Dangle("claims", claim.Id, "asset_id", "property (asset)");
            }
        }

        foreach (var exemption in data.Exemptions)
        {
            if (exemption.Body.AssetId is { } asset && !assetIds.Contains(asset))
            {
                Dangle("exemptions", exemption.Id, "asset_id", "property (asset)");
            }
        }

        foreach (var employment in data.Employments)
        {
            if (employment.Body.DebtorId is { } debtor && !debtorIds.Contains(debtor))
            {
                Dangle("employments", employment.Id, "debtor_id", "debtor");
            }
        }

        var employmentIds = data.Employments.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var record in data.PayPeriodRecords)
        {
            if (record.Body.EmploymentId is { } employment && !employmentIds.Contains(employment))
            {
                Dangle("pay_period_records", record.Id, "employment_id", "employment");
            }
        }

        foreach (var receipt in data.OtherIncomeRecords)
        {
            if (receipt.Body.DebtorId is { } debtor && !debtorIds.Contains(debtor))
            {
                Dangle("other_income_records", receipt.Id, "debtor_id", "debtor");
            }
        }

        foreach (var summary in data.IncomeSummaries)
        {
            if (summary.Body.DebtorId is { } debtor && !debtorIds.Contains(debtor))
            {
                Dangle("income_summaries", summary.Id, "debtor_id", "debtor");
            }
        }

        foreach (var expense in data.Expenses)
        {
            if (expense.Body.HouseholdId is { } household && !householdIds.Contains(household))
            {
                Dangle("expenses", expense.Id, "household_id", "household");
            }
        }

        foreach (var dependent in data.Dependents)
        {
            if (dependent.Body.HouseholdId is { } household && !householdIds.Contains(household))
            {
                Dangle("dependents", dependent.Id, "household_id", "household");
            }
        }

        foreach (var codebtor in data.Codebtors)
        {
            foreach (var claim in codebtor.Body.ClaimIds)
            {
                if (!claimIds.Contains(claim))
                {
                    Dangle("codebtors", codebtor.Id, "claim_ids", "claim");
                }
            }

            foreach (var contract in codebtor.Body.ContractLeaseIds)
            {
                if (!contractIds.Contains(contract))
                {
                    Dangle("codebtors", codebtor.Id, "contract_lease_ids", "contract/lease");
                }
            }
        }

        return problems;
    }

    private static List<PacketProblem> CardinalityProblems(CaseData data)
    {
        var problems = new List<PacketProblem>();
        if (data.Petitions.Count == 0)
        {
            problems.Add(new("petitions", null, string.Empty, "The petition's case-level answers (B101 Parts 2-6) have not been entered yet."));
        }

        foreach (var extra in data.Petitions.Skip(1))
        {
            problems.Add(new("petitions", extra.Id, string.Empty, "A case has exactly one petition record — delete the duplicates before assembling."));
        }

        if (data.Case.Chapter == 13)
        {
            if (data.Plans.Count == 0)
            {
                problems.Add(new("plans", null, string.Empty, "The Chapter 13 plan has not been entered yet — Official Form 113 prints from it."));
            }

            foreach (var extra in data.Plans.Skip(1))
            {
                problems.Add(new("plans", extra.Id, string.Empty, "A case has exactly one plan record — delete the duplicates before assembling."));
            }
        }

        foreach (var extra in data.MeansTestInputs.Skip(1))
        {
            problems.Add(new("means_test_inputs", extra.Id, string.Empty, "A case has exactly one means-test input record — delete the duplicates before assembling."));
        }

        var seenHouseholds = new HashSet<WhichHousehold>();
        foreach (var household in data.Households)
        {
            if (household.Body.WhichHousehold is not { } which)
            {
                problems.Add(new(
                    "households",
                    household.Id,
                    "which_household",
                    "Say which schedule this household belongs to (main, or Debtor 2's separate household) — without it the row cannot print on 106J or 106J-2."));
            }
            else if (!seenHouseholds.Add(which))
            {
                problems.Add(new(
                    "households",
                    household.Id,
                    "which_household",
                    "Two household records claim the same schedule — a case has at most one main household and one separate household for Debtor 2."));
            }
        }

        var summariesByDebtor = new HashSet<string>(StringComparer.Ordinal);
        foreach (var summary in data.IncomeSummaries)
        {
            if (summary.Body.DebtorId is not { } debtor)
            {
                continue;
            }

            if (!summariesByDebtor.Add(debtor))
            {
                problems.Add(new("income_summaries", summary.Id, "debtor_id", "Two income summaries claim the same debtor column — 106I prints one column per debtor."));
            }
        }

        return problems;
    }

    private static List<PacketProblem> StatementProblems(CaseData data)
    {
        var problems = new List<PacketProblem>();
        if (data.Case.Chapter == 13)
        {
            foreach (var lease in data.ContractLeases)
            {
                if (lease.Body.Intention is null)
                {
                    problems.Add(new(
                        "contract_leases",
                        lease.Id,
                        "intention",
                        "Say whether this contract or lease will be assumed or rejected — the Chapter 13 plan (B113 Part 6) lists the assumed ones and rejects every other."));
                }
            }

            problems.AddRange(AttorneyDisclosureProblems(data));
            return problems;
        }

        foreach (var claim in data.Claims)
        {
            if (claim.Body.ClaimClass == ClaimClass.Secured && claim.Body.Intention is null)
            {
                problems.Add(new(
                    "claims",
                    claim.Id,
                    "intention",
                    "Say what the debtor intends to do with this collateral (surrender, redeem, reaffirm, or other) — the Statement of Intention (B108) prints one answer per secured claim."));
            }
        }

        foreach (var lease in data.ContractLeases)
        {
            if (lease.Body.ListOnStatementOfIntention == true && lease.Body.Intention is null)
            {
                problems.Add(new(
                    "contract_leases",
                    lease.Id,
                    "intention",
                    "Say whether this lease will be assumed or rejected — it is flagged for the Statement of Intention (B108), which prints one answer per listed lease."));
            }
        }

        problems.AddRange(AttorneyDisclosureProblems(data));
        return problems;
    }

    private static List<PacketProblem> AttorneyDisclosureProblems(CaseData data)
    {
        var problems = new List<PacketProblem>();
        foreach (var professional in data.FilingProfessionals)
        {
            var body = professional.Body;
            if (body.Role != FilingProfessionalRole.Attorney)
            {
                continue;
            }

            (string Field, string Label, bool Missing)[] answers =
            [
                ("compensation_agreed", "the fee agreed for legal services", body.CompensationAgreed is null),
                ("compensation_received", "the amount received before filing", body.CompensationReceived is null),
                ("compensation_source_paid", "who paid the compensation", body.CompensationSourcePaid is null),
                ("compensation_source_to_be_paid", "who will pay the balance", body.CompensationSourceToBePaid is null),
                ("compensation_shared", "whether the fee is shared outside the firm", body.CompensationShared is null),
            ];
            foreach (var (field, label, missing) in answers)
            {
                if (missing)
                {
                    problems.Add(new("filing_professionals", professional.Id, field, $"The attorney's compensation disclosure (B2030) needs {label}."));
                }
            }
        }

        return problems;
    }

    private static PacketProblem? LocalPlanFormProblem(Case @case, CourtRegistry courts)
    {
        if (@case.Court is not { } court || courts.District(court) is not { } district)
        {
            return null;
        }

        var fact = district.Chapter13Plan;
        if (!fact.Verified || fact.Value is null || fact.Value.Form != Chapter13PlanFormKind.Local)
        {
            return null;
        }

        var title = fact.Value.Title is { Length: > 0 } named ? named : "its own local plan form";
        return new PacketProblem(
            "case",
            null,
            "court",
            $"The {district.Name} requires {title} in place of Official Form 113 (Bankruptcy Rule 3015.1), and that form is not prepared here yet — the Chapter 13 plan for this court has to be prepared outside Insolvia.");
    }

    /// <summary>
    /// Every structural reason the case cannot assemble, before any
    /// projection runs, in a fixed order. <paramref name="options"/> is read
    /// for one thing: <see cref="OutputOptions.AmendedOnly"/> lifts the
    /// filed-case refusal.
    /// </summary>
    public static IReadOnlyList<PacketProblem> CompletenessProblems(CaseData data, OutputOptions? options = null, CourtRegistry? courts = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= FormOverlay.DefaultOutputOptions;
        courts ??= CourtRegistry.Shipped;
        var problems = new List<PacketProblem>();
        if (data.Case.Chapter is not (7 or 13))
        {
            problems.Add(new(
                "case",
                null,
                "chapter",
                $"This is a Chapter {data.Case.Chapter} case — only the individual Chapter 7 and Chapter 13 packets can be assembled today."));
        }

        if (data.Case.Chapter == 13 && LocalPlanFormProblem(data.Case, courts) is { } local)
        {
            problems.Add(local);
        }

        var filed = CaseRules.IsFiled(data.Case.Status);
        if (filed && !options.AmendedOnly)
        {
            problems.Add(new(
                "case",
                null,
                "status",
                "This case is filed. A filed case's packet is pinned to the data that produced it and is never re-assembled. To change a filed schedule, mark the changed items amended and assemble with amendedOnly instead."));
        }

        if (options.AmendedOnly && !filed)
        {
            problems.Add(new("case", null, "status", "amendedOnly renders an amendment to a FILED case — this case has not been filed yet."));
        }

        if (!data.Debtors.Any(debtor => debtor.FilingRole == FilingRole.Debtor1))
        {
            problems.Add(new("debtors", null, string.Empty, "The case has no Debtor 1 record — every form in the packet prints the debtor's name."));
        }

        problems.AddRange(CardinalityProblems(data));
        problems.AddRange(ReferenceProblems(data));
        problems.AddRange(StatementProblems(data));
        return problems;
    }

    /// <summary>
    /// Which of the chapter's forms THIS case files: J-2 only with Debtor 2's
    /// separate household, B122A-2/B122C-2 only when not determinately below
    /// the median, B108 only with a row to print, B2030 only with an attorney.
    /// </summary>
    public static IReadOnlyList<string> PacketFormSeriesFor(CaseData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var baseSet = ChapterFormSeries(data.Case.Chapter);
        var caseFile = data.ToCaseFile();
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        if (!data.Households.Any(e => e.Body.WhichHousehold == WhichHousehold.Debtor2Separate))
        {
            skipped.Add("form/b106j2");
        }

        if (baseSet.Contains("form/b122a2") && !B122A2.Files(caseFile))
        {
            skipped.Add("form/b122a2");
        }

        if (baseSet.Contains("form/b122c2") && !B122C2.Files(caseFile))
        {
            skipped.Add("form/b122c2");
        }

        var hasStatementRow = data.Claims.Any(e => e.Body.ClaimClass == ClaimClass.Secured)
            || data.ContractLeases.Any(e => e.Body.ListOnStatementOfIntention == true);
        if (!hasStatementRow)
        {
            skipped.Add("form/b108");
        }

        if (!data.FilingProfessionals.Any(e => e.Body.Role == FilingProfessionalRole.Attorney))
        {
            skipped.Add("form/b2030");
        }

        return [.. baseSet.Where(series => !skipped.Contains(series))];
    }

    // ── amendments ───────────────────────────────────────────────────────────

    private static HashSet<string> HouseholdIds(CaseData data, WhichHousehold which) =>
        data.Households.Where(e => e.Body.WhichHousehold == which).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static bool IsUnsecured(ClaimBody claim) => claim.ClaimClass is ClaimClass.PriorityUnsecured or ClaimClass.NonpriorityUnsecured;

    private static bool SeriesHasAmendedItems(CaseData data, string seriesId) => seriesId switch
    {
        "form/b106ab" => data.Assets.Any(e => e.Amended),
        "form/b106c" => data.Exemptions.Any(e => e.Amended),
        "form/b106d" => data.Claims.Any(e => e.Amended && e.Body.ClaimClass == ClaimClass.Secured),
        "form/b106ef" => data.Claims.Any(e => e.Amended && IsUnsecured(e.Body)),
        "form/b106g" => data.ContractLeases.Any(e => e.Amended),
        "form/b106h" => data.Codebtors.Any(e => e.Amended),
        "form/b106i" => data.IncomeSummaries.Any(e => e.Amended),
        "form/b106j" or "form/b106j2" => AmendedExpenses(data, seriesId).Any(),
        "form/b107" => data.SofaEntries.Any(e => e.Amended),
        _ => false,
    };

    private static IEnumerable<CaseEntity<ExpenseBody>> AmendedExpenses(CaseData data, string seriesId)
    {
        var households = HouseholdIds(data, seriesId == "form/b106j" ? WhichHousehold.Main : WhichHousehold.Debtor2Separate);
        return data.Expenses.Where(e => e.Amended && e.Body.HouseholdId is { } id && households.Contains(id));
    }

    /// <summary>The series both amendable and carrying an amended item, in filing order.</summary>
    public static IReadOnlyList<string> AmendedSeriesIds(CaseData data, IReadOnlyList<string> seriesIds)
    {
        ArgumentNullException.ThrowIfNull(seriesIds);
        return [.. seriesIds.Where(series => AmendableSeries.Contains(series) && SeriesHasAmendedItems(data, series))];
    }

    /// <summary>Every series an amendedOnly render prints: the amended schedules plus B106Sum/B106Dec — or nothing.</summary>
    public static IReadOnlyList<string> AmendmentRenderSeries(CaseData data, IReadOnlyList<string> seriesIds)
    {
        ArgumentNullException.ThrowIfNull(seriesIds);
        var amended = AmendedSeriesIds(data, seriesIds);
        if (amended.Count == 0)
        {
            return [];
        }

        return [.. seriesIds.Where(series => amended.Contains(series) || AlwaysWithAmendment.Contains(series))];
    }

    /// <summary>
    /// The render-only view of the data for one series of an amendment: only
    /// THIS series' own printed rows narrow to the amended ones; every other
    /// collection (a reference target among them) stays whole.
    /// </summary>
    public static CaseData FilteredForRender(CaseData data, string seriesId)
    {
        ArgumentNullException.ThrowIfNull(data);
        return seriesId switch
        {
            "form/b106ab" => data with { Assets = [.. data.Assets.Where(e => e.Amended)] },
            "form/b106c" => data with { Exemptions = [.. data.Exemptions.Where(e => e.Amended)] },
            "form/b106d" => data with { Claims = [.. data.Claims.Where(e => e.Amended && e.Body.ClaimClass == ClaimClass.Secured)] },
            "form/b106ef" => data with { Claims = [.. data.Claims.Where(e => e.Amended && IsUnsecured(e.Body))] },
            "form/b106g" => data with { ContractLeases = [.. data.ContractLeases.Where(e => e.Amended)] },
            "form/b106h" => data with { Codebtors = [.. data.Codebtors.Where(e => e.Amended)] },
            "form/b106j" or "form/b106j2" => data with { Expenses = [.. AmendedExpenses(data, seriesId)] },
            "form/b107" => data with { SofaEntries = [.. data.SofaEntries.Where(e => e.Amended)] },
            _ => data,
        };
    }

    // ── assembly ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole gate-then-render, pure over its inputs: the assembled packet,
    /// or EVERY problem found — never both, never a partial packet.
    /// <paramref name="printedAt"/> is the stamps' and the cover sheet's
    /// clock reading (the caller's; required, so the output is deterministic).
    /// </summary>
    public static (AssembledPacket? Packet, IReadOnlyList<PacketProblem> Problems) Assemble(
        CaseData data,
        DateOnly asOf,
        DateTimeOffset printedAt,
        OutputOptions? options = null,
        CourtRegistry? courts = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= FormOverlay.DefaultOutputOptions;
        courts ??= CourtRegistry.Shipped;
        var problems = new List<PacketProblem>(CompletenessProblems(data, options, courts));

        var matrix = CreditorMatrices.Generate([.. data.Creditors.Select(e => (e.Id, e.Body))], CreditorMatrices.FormatForCourt(data.Case.Court, courts));
        problems.AddRange(matrix.Problems.Select(p => new PacketProblem("creditors", p.CreditorId, p.Field, p.Message)));

        var caseFile = data.ToCaseFile();
        var seriesIds = PacketFormSeriesFor(data);
        var amendment = options.AmendedOnly ? AmendmentRenderSeries(data, seriesIds) : [];

        var releases = new OrderedDictionary<string, FormRelease>(StringComparer.Ordinal);
        foreach (var seriesId in seriesIds)
        {
            try
            {
                releases[seriesId] = FormTemplates.Resolve(seriesId, asOf);
            }
            catch (EngineLookupException error)
            {
                problems.Add(new(seriesId, null, string.Empty, Engines.Py.Str(error)));
            }
        }

        DollarAmountsRelease? constants = null;
        try
        {
            constants = DollarAmounts.Resolve(asOf);
        }
        catch (EngineLookupException error)
        {
            problems.Add(new(DollarAmounts.SeriesId, null, string.Empty, Engines.Py.Str(error)));
        }

        var projected = new OrderedDictionary<string, FieldValues>(StringComparer.Ordinal);
        foreach (var (seriesId, release) in releases)
        {
            var seriesCaseFile = options.AmendedOnly ? FilteredForRender(data, seriesId).ToCaseFile() : caseFile;
            try
            {
                projected[seriesId] = FormProjections.Project(release, seriesCaseFile);
            }
            catch (FormProjectionException error)
            {
                problems.AddRange(error.Problems.Select(message => new PacketProblem(seriesId, null, string.Empty, message)));
            }
        }

        if (problems.Count > 0)
        {
            return (null, problems);
        }

        IReadOnlyList<string> renderSeries = seriesIds;
        if (options.Forms is { } forms)
        {
            var requested = forms.Select(key => $"form/{key}").ToHashSet(StringComparer.Ordinal);
            problems.AddRange(requested.Except(seriesIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(series => new PacketProblem(
                series,
                null,
                string.Empty,
                "This case does not currently file this form, so it cannot be included in a selected print.")));
            if (problems.Count > 0)
            {
                return (null, problems);
            }

            renderSeries = [.. seriesIds.Where(requested.Contains)];
        }
        else if (options.AmendedOnly)
        {
            if (amendment.Count == 0)
            {
                problems.Add(new("case", null, string.Empty, "No schedule items are marked amended — mark at least one item amended before assembling an amendedOnly packet."));
                return (null, problems);
            }

            renderSeries = amendment;
        }

        var parts = new List<(string Name, byte[] Content)>();
        if (options.AmendedOnly)
        {
            parts.Add(("00-amendment-cover.pdf", AmendmentCoverSheet.Render(caseFile, [.. amendment.Select(series => releases[series])], printedAt)));
        }

        for (var index = 0; index < seriesIds.Count; index++)
        {
            var seriesId = seriesIds[index];
            if (!renderSeries.Contains(seriesId))
            {
                continue;
            }

            var release = releases[seriesId];
            var values = FormOverlay.ApplySignatureOptions(release, projected[seriesId], caseFile, options, asOf);
            values = FormOverlay.ApplyAmendmentOptions(release, values, options.AmendedOnly && renderSeries.Contains(seriesId));
            byte[] rendered;
            try
            {
                rendered = FormFill.Fill(release, values);
            }
            catch (FormFillException error)
            {
                problems.AddRange(error.Problems.Select(message => new PacketProblem(seriesId, null, string.Empty, message)));
                continue;
            }

            if (FormOverlay.ApplyOutputStamps(rendered, release, options, printedAt) is { } stamped)
            {
                parts.Add((PartFileName(index + 1, release.Form), stamped));
            }
        }

        if (problems.Count > 0)
        {
            return (null, problems);
        }

        parts.Add((CreditorMatrices.MatrixFileName, Encoding.ASCII.GetBytes(matrix.Content!)));
        var pins = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var series in AllFormSeries)
        {
            pins[series] = FormTemplates.Resolve(series, asOf).Pin;
        }

        var projections = new OrderedDictionary<string, FieldValues>(StringComparer.Ordinal);
        foreach (var series in seriesIds)
        {
            projections[series] = projected[series];
        }

        return (new AssembledPacket(parts, pins, constants!.ReleaseId, matrix.CreditorCount, projections), []);
    }

    /// <summary>
    /// One zip, deterministic to the byte (Python's <c>packet_zip</c>): the
    /// caller's entry order, the DOS-epoch timestamp (1980-01-01), STORED,
    /// rw-r--r--. Written by hand rather than through ZipArchive, whose
    /// "made by" byte follows the host OS: this writes exactly the records
    /// CPython's <c>zipfile</c> writes on a Unix host, so the same parts give
    /// the same zip on any machine — and the same zip Python gives.
    /// </summary>
    public static byte[] PacketZip(IReadOnlyList<(string Name, byte[] Content)> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        const ushort Version = 20;
        const ushort DosDate = (0 << 9) | (1 << 5) | 1; // 1980-01-01
        const ushort DosTime = 0;
        const uint ExternalAttributes = 0x81A4u << 16; // 0o100644, a regular rw-r--r-- file
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        var central = new List<(byte[] Name, ushort Flags, uint Crc, uint Size, uint Offset)>();
        foreach (var (name, content) in parts)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var flags = (ushort)(name.All(c => c < 128) ? 0 : 0x800);
            var crc = Crc32(content);
            var offset = (uint)output.Position;
            writer.Write(0x04034b50u);
            writer.Write(Version);
            writer.Write(flags);
            writer.Write((ushort)0); // stored
            writer.Write(DosTime);
            writer.Write(DosDate);
            writer.Write(crc);
            writer.Write((uint)content.Length);
            writer.Write((uint)content.Length);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0);
            writer.Write(nameBytes);
            writer.Write(content);
            central.Add((nameBytes, flags, crc, (uint)content.Length, offset));
        }

        var centralStart = (uint)output.Position;
        foreach (var (nameBytes, flags, crc, size, offset) in central)
        {
            writer.Write(0x02014b50u);
            writer.Write((byte)Version); // create_version
            writer.Write((byte)3); // create_system: Unix
            writer.Write(Version); // extract_version
            writer.Write(flags);
            writer.Write((ushort)0);
            writer.Write(DosTime);
            writer.Write(DosDate);
            writer.Write(crc);
            writer.Write(size);
            writer.Write(size);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0); // extra
            writer.Write((ushort)0); // comment
            writer.Write((ushort)0); // disk start
            writer.Write((ushort)0); // internal attributes
            writer.Write(ExternalAttributes);
            writer.Write(offset);
            writer.Write(nameBytes);
        }

        var centralSize = (uint)output.Position - centralStart;
        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)central.Count);
        writer.Write((ushort)central.Count);
        writer.Write(centralSize);
        writer.Write(centralStart);
        writer.Write((ushort)0);
        writer.Flush();
        return output.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Whether this render puts B121 on paper — the only reason for the full tax-id read.</summary>
    public static bool PrintsB121(OutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !options.AmendedOnly && (options.Forms is null || options.Forms.Contains("b121"));
    }
}
