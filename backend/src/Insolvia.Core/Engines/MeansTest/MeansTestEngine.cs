using Insolvia.Core;
using Insolvia.Core.MeansTest;
using Insolvia.Engines.Regulatory;

namespace Insolvia.Engines.MeansTest;

// The means-test engine — rule-based, effective-dated, pure (from
// core/means_test.py, issues #101, #365): § 707(b) for a Chapter 7 case
// (B122A-1's comparison, then B122A-2), § 1325(b) for a Chapter 13 case
// (B122C-1's commitment period, then B122C-2). Every figure traces to a rule,
// an input, or a dated dataset; an absent entered figure is a zero; an
// entered figure past a statutory cap is an error naming the cap.
//
// Rounding: every line quantizes to the cent ROUND_HALF_UP (`_money`, the
// ÷ 60 averages, the ÷ 12 education cap, line 36's product, line 41b).

/// <summary>The calculation cannot run as asked; carries every problem found.</summary>
public sealed class MeansTestException : Exception
{
    /// <summary>Creates the exception from the problems found.</summary>
    public MeansTestException(IReadOnlyList<string> problems)
        : base(string.Join("; ", problems ?? []))
    {
        Problems = [.. problems ?? []];
    }

    /// <summary>Creates the exception with no problems.</summary>
    public MeansTestException()
        : this([])
    {
    }

    /// <summary>Creates the exception with one problem.</summary>
    public MeansTestException(string message)
        : this([message])
    {
    }

    /// <summary>Creates the exception with one problem and its cause.</summary>
    public MeansTestException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [message];
    }

    /// <summary>Every problem, in the order found.</summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>The effective-dated datasets, resolved once for one as-of date.</summary>
public sealed record MeansTestData(
    DateOnly AsOf,
    UstRelease MediansRelease,
    MedianIncomeTable Medians,
    UstRelease NationalRelease,
    NationalStandards National,
    UstRelease LocalRelease,
    LocalStandards Local,
    UstRelease Ch13Release,
    Ch13AdminMultipliers Ch13,
    DollarAmountsRelease AmountsRelease)
{
    /// <summary>Series id to release id, the pins the result records — in the wire order.</summary>
    public IReadOnlyDictionary<string, string> ReleaseIds
    {
        get
        {
            var ids = new OrderedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var release in new[] { MediansRelease, NationalRelease, LocalRelease, Ch13Release })
            {
                ids[release.SeriesId] = release.ReleaseId;
            }

            ids[AmountsRelease.SeriesId] = AmountsRelease.ReleaseId;
            return ids;
        }
    }
}

/// <summary>B122A-1 lines 12-14 / B122C-1 lines 15-17: annualized CMI against the applicable median.</summary>
public sealed record MedianComparison(string State, int HouseholdSize, string MonthlyCmi, string AnnualizedCmi, string AnnualMedian, bool AboveMedian, string Source);

/// <summary>One line of the trace.</summary>
public sealed record MeansTestLine(string Line, string Label, string Amount, string Source, string Form);

/// <summary>B122C-1 Part 3: the applicable commitment period and the comparison that set it.</summary>
public sealed record CommitmentPeriod(int Months, string MaritalAdjustment, string MonthlyIncome, string AnnualizedIncome, string AnnualMedian, string Source);

/// <summary>Everything one run of the test reads, assembled by the caller from case records.</summary>
public sealed record MeansTestCase(
    string State,
    string County,
    string District,
    CmiResult Cmi,
    MeansTestInputBody Inputs,
    string PriorityDebtTotal,
    string NonpriorityUnsecuredTotal,
    int ChildrenUnder18,
    int Chapter = 7);

/// <summary>The whole determination.</summary>
public sealed record MeansTestResult(
    DateOnly AsOf,
    IReadOnlyDictionary<string, string> ReleaseIds,
    MedianComparison? Comparison,
    string Outcome,
    string DeterminedBy,
    IReadOnlyList<MeansTestLine> Lines,
    int Chapter = 7,
    CommitmentPeriod? Commitment = null,
    string? DisposableIncome = null);

/// <summary>The means-test engine (Python's <c>core/means_test.py</c>).</summary>
public static class MeansTestEngine
{
    /// <summary>B122A-2.</summary>
    public const string Form122A2 = "122A-2";

    /// <summary>B122C-1.</summary>
    public const string Form122C1 = "122C-1";

    /// <summary>B122C-2.</summary>
    public const string Form122C2 = "122C-2";

    /// <summary>§ 1325(b)(4)'s period below the median, in months.</summary>
    public const int CommitmentPeriodBelowMedian = 36;

    /// <summary>§ 1325(b)(4)'s period at or above the median, in months.</summary>
    public const int CommitmentPeriodAboveMedian = 60;

    private const string AgeBandSource = "entered (means_test_input.people_under_65 + people_65_or_older)";

    /// <summary>The three Form 122A-1Supp exemptions, in the order the form asks them, each with its rule.</summary>
    public static readonly IReadOnlyList<(string Flag, string Rule)> PresumptionExemptions =
    [
        ("non_consumer_debts", "debts are not primarily consumer debts — 11 U.S.C. § 707(b)(1); Form 122A-1Supp Part 1"),
        ("disabled_veteran", "disabled veteran whose debts were incurred primarily during active duty or homeland-defense activity — 11 U.S.C. § 707(b)(2)(D)(i); Form 122A-1Supp Part 2"),
        ("reservist_national_guard", "reservist or National Guard member called to active duty after September 11, 2001 — 11 U.S.C. § 707(b)(2)(D)(ii); Form 122A-1Supp Part 2"),
    ];

    /// <summary>The first exemption the entered inputs claim, or null when the presumption can arise.</summary>
    public static (string Flag, string Rule)? PresumptionExemption(MeansTestInputBody inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        foreach (var (flag, rule) in PresumptionExemptions)
        {
            var claimed = flag switch
            {
                "non_consumer_debts" => inputs.NonConsumerDebts,
                "disabled_veteran" => inputs.DisabledVeteran,
                _ => inputs.ReservistNationalGuard,
            };
            if (claimed == true)
            {
                return (flag, rule);
            }
        }

        return null;
    }

    /// <summary>B122A-1 line 13's (B122C-1 line 16b's) household size and its source.</summary>
    public static (int Size, string Source)? MedianHouseholdSize(MeansTestInputBody inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.MedianHouseholdSize is { } entered)
        {
            return (entered, "entered (means_test_input.median_household_size)");
        }

        return EnteredHousehold(inputs) is { } sum ? (sum, AgeBandSource) : null;
    }

    /// <summary>Line 5's "number of people used in determining deductions".</summary>
    public static (int Size, string Source)? IrsFamilySize(MeansTestInputBody inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.IrsFamilySize is { } entered)
        {
            return (entered, "entered (means_test_input.irs_family_size)");
        }

        return EnteredHousehold(inputs) is { } sum ? (sum, AgeBandSource) : null;
    }

    /// <summary>The family size the housing standard is read at (lines 8 and 9a).</summary>
    public static (int Size, string Source)? IrsHousingFamilySize(MeansTestInputBody inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.IrsHousingFamilySize is { } entered)
        {
            return (entered, "entered (means_test_input.irs_housing_family_size)");
        }

        return EnteredHousehold(inputs) is { } sum ? (sum, AgeBandSource) : null;
    }

    /// <summary>Every series the test reads, resolved as of one date; <see cref="EngineLookupException"/> when any has no release.</summary>
    public static MeansTestData ResolveData(DateOnly asOf)
    {
        var (mediansRelease, medians) = UstData.MedianIncomeTable(asOf);
        var (nationalRelease, national) = UstData.NationalStandards(asOf);
        var (localRelease, local) = UstData.LocalStandards(asOf);
        var (ch13Release, ch13) = UstData.Ch13AdminMultipliers(asOf);
        return new MeansTestData(asOf, mediansRelease, medians, nationalRelease, national, localRelease, local, ch13Release, ch13, DollarAmounts.Resolve(asOf));
    }

    /// <summary>12x the monthly figure against the state median for the household size.</summary>
    public static MedianComparison Compare(string monthlyCmi, string state, int householdSize, MeansTestData data)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(data);
        var monthly = Exact.Dec(monthlyCmi);
        var annualized = monthly * 12;
        decimal median;
        try
        {
            median = data.Medians.AnnualMedian(state, householdSize);
        }
        catch (Exception error) when (error is EngineKeyException or EngineValueException)
        {
            throw new MeansTestException([Exact.Str(error)]);
        }

        var upper = state.ToUpperInvariant();
        return new MedianComparison(
            upper,
            householdSize,
            Money(monthly),
            Money(annualized),
            Money(median),
            annualized > median,
            $"Census median family income, {upper} household of {householdSize} — {data.MediansRelease.ReleaseId}");
    }

    /// <summary>The whole determination for one case, selected by its chapter.</summary>
    public static MeansTestResult Run(MeansTestCase @case, MeansTestData data)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(data);
        return @case.Chapter switch
        {
            13 => RunChapter13(@case, data),
            7 => RunChapter7(@case, data),
            _ => throw new MeansTestException([$"Chapter {@case.Chapter} has no means-test form — Forms 122A and 122C exist for Chapters 7 and 13 only"]),
        };
    }

    // `_money`: ROUND_HALF_UP.
    internal static string Money(decimal value) => Exact.Money(value, Rounding.HalfUp);

    private static int? EnteredHousehold(MeansTestInputBody inputs) =>
        inputs.PeopleUnder65 is { } under && inputs.People65OrOlder is { } over ? under + over : null;

    private static decimal Entered(decimal? value) => value ?? 0m;

    private static decimal SixtyMonthAverage(decimal total) => Exact.Quantize(total / 60, Rounding.HalfUp);

    private static string RowName(OtherSecuredPayment payment) =>
        $"{(string.IsNullOrEmpty(payment.CreditorName) ? payment.Id : payment.CreditorName)} ({(string.IsNullOrEmpty(payment.PropertyDescription) ? "property" : payment.PropertyDescription)})";

    private static (decimal Amount, string Source) SecuredPayment(MeansTestInputBody inputs, SecuredPaymentBucket bucket, decimal? typed, string fieldName)
    {
        if (typed is not null)
        {
            return (Entered(typed), $"entered (means_test_input.{fieldName})");
        }

        var rows = inputs.OtherSecuredPayments.Where(payment => payment.Bucket == bucket).ToList();
        if (rows.Count == 0)
        {
            return (0m, $"entered (means_test_input.{fieldName})");
        }

        var total = rows.Aggregate(0m, (sum, payment) => sum + Entered(payment.MonthlyPayment));
        return (total, $"entered per claim (means_test_input.other_secured_payments, bucket {bucket.ToWire()}: " + string.Join("; ", rows.Select(RowName)) + ")");
    }

    private static (decimal Amount, string Source) CureTotal(MeansTestInputBody inputs)
    {
        if (inputs.PriorityCureTotal is not null)
        {
            return (Entered(inputs.PriorityCureTotal), "entered (means_test_input.priority_cure_total)");
        }

        var rows = inputs.OtherSecuredPayments.Where(payment => payment.CureTotal is not null).ToList();
        if (rows.Count == 0)
        {
            return (0m, "entered (means_test_input.priority_cure_total)");
        }

        var total = rows.Aggregate(0m, (sum, payment) => sum + Entered(payment.CureTotal));
        return (total, "entered per claim (means_test_input.other_secured_payments[].cure_total: " + string.Join("; ", rows.Select(RowName)) + ")");
    }

    private static (decimal Amount, string Source) MaritalAdjustment(MeansTestCase @case, string form, string line, List<string> problems)
    {
        var inputs = @case.Inputs;
        var hasColumnB = @case.Cmi.Columns.Any(column => column.Column == "B");
        var adjustments = inputs.MaritalAdjustments;
        if (adjustments.Count > 0 && !hasColumnB)
        {
            problems.Add($"marital adjustments are entered but Form {form} has no Column B — line {line} applies only when a spouse's income is included");
        }

        var total = 0m;
        foreach (var item in adjustments)
        {
            total += Entered(item.Amount);
        }

        var source = "entered (means_test_input.marital_adjustments"
            + (adjustments.Count > 0 ? ": " + string.Join("; ", adjustments.Select(item => string.IsNullOrEmpty(item.Description) ? item.Id : item.Description)) : string.Empty);
        return (total, source + ")");
    }

    private static (int Under65, int Over65, (int Size, string Source) Family, (int Size, string Source) HousingFamily) HouseholdForDeductions(MeansTestInputBody inputs)
    {
        if (inputs.PeopleUnder65 is not { } under || inputs.People65OrOlder is not { } over)
        {
            throw new MeansTestException(["the household's age bands (people under 65 / 65 and older) have not been entered — line 7's health care allowance needs them"]);
        }

        var family = IrsFamilySize(inputs)!.Value;
        var housingFamily = IrsHousingFamilySize(inputs)!.Value;
        if (family.Size < 1)
        {
            throw new MeansTestException(["the household cannot be empty"]);
        }

        return (under, over, family, housingFamily);
    }

    private static decimal Deductions(MeansTestCase @case, MeansTestData data, Trace trace, string form, List<string> problems)
    {
        var inputs = @case.Inputs;
        var (under65, over65, family, housingFamily) = HouseholdForDeductions(inputs);
        var (household, householdSource) = family;

        decimal Put(string line, string label, decimal amount, string source) => trace.Put(form, line, label, amount, source);

        // ── IRS allowances (lines 5-24) ──
        Put("5", "Number of people used in determining deductions", household, householdSource);
        var nationalSource = $"IRS National Standards, household of {household} — {data.NationalRelease.ReleaseId}";
        var line6 = Put("6", "Food, clothing, and other items", data.National.Allowance(household), nationalSource);
        var oopUnder = Exact.Dec(data.National.OopHealthcareUnder65);
        var oopOver = Exact.Dec(data.National.OopHealthcare65AndOlder);
        Put("7a", "Out-of-pocket health care allowance per person (under 65)", oopUnder, nationalSource);
        Put("7b", "Number of people who are under 65", under65, "entered");
        var line7c = Put("7c", "Subtotal (under 65)", oopUnder * under65, "line 7a x line 7b");
        Put("7d", "Out-of-pocket health care allowance per person (65 or older)", oopOver, nationalSource);
        Put("7e", "Number of people who are 65 or older", over65, "entered");
        var line7f = Put("7f", "Subtotal (65 or older)", oopOver * over65, "line 7d x line 7e");
        var line7 = Put("7", "Out-of-pocket health care allowance", line7c + line7f, "line 7c plus line 7f");

        CountyHousing countyRow;
        try
        {
            countyRow = data.Local.HousingFor(@case.State, @case.County);
        }
        catch (EngineKeyException error)
        {
            throw new MeansTestException([.. problems, Exact.Str(error)]);
        }

        var (housingSize, housingSizeSource) = housingFamily;
        var housingSource = $"IRS Local Standards, {countyRow.County}, {@case.State.ToUpperInvariant()}, household of {housingSize} — {data.LocalRelease.ReleaseId}";
        if (housingSize != household)
        {
            housingSource += $" ({housingSizeSource})";
        }

        var line8 = Put("8", "Housing and utilities — insurance and operating expenses", countyRow.NonMortgageFor(housingSize), housingSource);
        var line9a = Put("9a", "Housing and utilities — mortgage or rent expense (IRS Local Standard)", countyRow.MortgageRentFor(housingSize), housingSource);
        var (homeSecured, homeSource) = SecuredPayment(inputs, SecuredPaymentBucket.Home, inputs.HomeSecuredMonthlyTotal, "home_secured_monthly_total");
        var line9b = Put("9b", "Average monthly payment for all debts secured by your home", homeSecured, homeSource);
        var line9c = Put("9c", "Net mortgage or rent expense", Exact.Max(0m, line9a - line9b), "line 9a minus line 9b, not below zero");
        var line10 = Put(
            "10",
            "Claimed adjustment to the housing standard",
            Entered(inputs.HousingAdjustmentAmount),
            "entered (means_test_input.housing_adjustment_amount"
                + (string.IsNullOrEmpty(inputs.HousingAdjustmentExplanation) ? string.Empty : $": {inputs.HousingAdjustmentExplanation}")
                + ")");
        if (inputs.HousingAdjustmentAmount is not null && string.IsNullOrEmpty(inputs.HousingAdjustmentExplanation))
        {
            problems.Add("line 10's housing adjustment needs its explanation — the form requires why the UST's division is incorrect");
        }

        var vehicles = inputs.VehicleCount ?? 0;
        Put("11", "Number of vehicles claimed for ownership or operating expenses", vehicles, "entered (means_test_input.vehicle_count)");
        var transportation = data.Local.Transportation;
        var transportSource = $"IRS Local Standards transportation — {data.LocalRelease.ReleaseId}";
        decimal line12;
        if (vehicles > 0)
        {
            var operating = transportation.OperatingCostsFor(@case.State, @case.County);
            line12 = Put("12", "Vehicle operation expense", operating.ForVehicles(vehicles), $"{transportSource}, operating costs, {@case.County}");
        }
        else
        {
            line12 = Put("12", "Vehicle operation expense", 0m, "no vehicles claimed");
        }

        var line13 = 0m;
        var line13b = 0m;
        var line13e = 0m;
        if (vehicles >= 1)
        {
            var ownershipEach = transportation.OwnershipCosts.ForVehicles(1);
            var line13a = Put("13a", "Vehicle 1 ownership costs (IRS Local Standard)", ownershipEach, transportSource);
            var (vehicle1, vehicle1Source) = SecuredPayment(inputs, SecuredPaymentBucket.Vehicle1, inputs.Vehicle1LoanMonthly, "vehicle_1_loan_monthly");
            line13b = Put("13b", "Average monthly payment for debts secured by Vehicle 1", vehicle1, vehicle1Source);
            line13 += Put("13c", "Net Vehicle 1 ownership or lease expense", Exact.Max(0m, line13a - line13b), "line 13a minus line 13b, not below zero");
        }

        if (vehicles >= 2)
        {
            var ownershipEach = transportation.OwnershipCosts.ForVehicles(1);
            var line13d = Put("13d", "Vehicle 2 ownership costs (IRS Local Standard)", ownershipEach, transportSource);
            var (vehicle2, vehicle2Source) = SecuredPayment(inputs, SecuredPaymentBucket.Vehicle2, inputs.Vehicle2LoanMonthly, "vehicle_2_loan_monthly");
            line13e = Put("13e", "Average monthly payment for debts secured by Vehicle 2", vehicle2, vehicle2Source);
            line13 += Put("13f", "Net Vehicle 2 ownership or lease expense", Exact.Max(0m, line13d - line13e), "line 13d minus line 13e, not below zero");
        }

        Put("13", "Vehicle ownership or lease expense", line13, "net vehicle expenses added");

        var publicTransit = Exact.Dec(transportation.PublicTransportationNational);
        var line14 = vehicles == 0
            ? Put("14", "Public transportation expense", publicTransit, transportSource)
            : Put("14", "Public transportation expense", 0m, "vehicles claimed on line 11");
        var line15 = Entered(inputs.AdditionalPublicTransportation);
        if (vehicles == 0 && line15 > 0)
        {
            problems.Add("line 15's additional public transportation applies only when one or more vehicles are claimed on line 11");
        }

        if (line15 > publicTransit)
        {
            problems.Add($"line 15 may not exceed the IRS public transportation standard ({Money(publicTransit)})");
        }

        Put("15", "Additional public transportation expense", line15, "entered (means_test_input.additional_public_transportation), capped at the IRS public transportation standard");

        (string Number, string Label, string Field, decimal? Value)[] otherNecessary =
        [
            ("16", "Taxes", "taxes", inputs.Taxes),
            ("17", "Involuntary deductions", "involuntary_deductions", inputs.InvoluntaryDeductions),
            ("18", "Term life insurance", "term_life_insurance", inputs.TermLifeInsurance),
            ("19", "Court-ordered payments", "court_ordered_payments", inputs.CourtOrderedPayments),
            ("20", "Education required for employment or for a disabled child", "education_for_employment_or_disability", inputs.EducationForEmploymentOrDisability),
            ("21", "Childcare", "childcare", inputs.Childcare),
            ("22", "Additional health care expenses, excluding insurance costs", "healthcare_above_allowance", inputs.HealthcareAboveAllowance),
            ("23", "Optional telephones and telephone services", "optional_telecom", inputs.OptionalTelecom),
        ];
        var otherNecessaryTotal = 0m;
        foreach (var (number, label, fieldName, value) in otherNecessary)
        {
            otherNecessaryTotal += Put(number, label, Entered(value), $"entered (means_test_input.{fieldName})");
        }

        var line24 = Put(
            "24",
            "All expenses allowed under the IRS expense allowances",
            line6 + line7 + line8 + line9c + line10 + line12 + line13 + line14 + line15 + otherNecessaryTotal,
            "lines 6 + 7 + 8 + 9c + 10 + 12 + 13 + 14 + 15 + 16 through 23");

        // ── additional expense deductions (lines 25-32) ──
        var line25 = Put(
            "25",
            "Health insurance, disability insurance, and HSA expenses",
            Entered(inputs.HealthInsurance) + Entered(inputs.DisabilityInsurance) + Entered(inputs.HealthSavingsAccount),
            "entered (means_test_input.health_insurance + disability_insurance + health_savings_account)");
        var line26 = Put("26", "Continuing contributions to the care of household or family members", Entered(inputs.FamilyCareContributions), "entered (means_test_input.family_care_contributions) — 11 U.S.C. § 707(b)(2)(A)(ii)(II)");
        var line27 = Put("27", "Protection against family violence", Entered(inputs.FamilyViolenceProtection), "entered (means_test_input.family_violence_protection)");
        var line28 = Put("28", "Additional home energy costs", Entered(inputs.HomeEnergyExcess), "entered (means_test_input.home_energy_excess)");
        var educationCapAmount = data.AmountsRelease.Amount("means-test-education-annual-cap-per-child");
        var monthlyCapPerChild = Exact.Quantize(educationCapAmount.Value / 12, Rounding.HalfUp);
        var educationCap = monthlyCapPerChild * @case.ChildrenUnder18;
        var line29 = Entered(inputs.EducationUnder18);
        if (line29 > educationCap)
        {
            problems.Add($"line 29 exceeds the {educationCapAmount.Citation} cap of {Money(monthlyCapPerChild)} per month per child under 18 ({@case.ChildrenUnder18} children — cap {Money(educationCap)})");
        }

        Put("29", "Education expenses for dependent children younger than 18", line29, $"entered (means_test_input.education_under_18), capped per child by {educationCapAmount.Citation} — {data.AmountsRelease.ReleaseId}");
        var foodClothingCap = data.National.AdditionalFoodClothingCapFor(household);
        var line30 = Entered(inputs.AdditionalFoodClothing);
        if (line30 > foodClothingCap)
        {
            problems.Add($"line 30 exceeds the published 5% food-and-clothing cap of {Money(foodClothingCap)} for a household of {household}");
        }

        Put("30", "Additional food and clothing expense", line30, $"entered (means_test_input.additional_food_clothing), capped at 5% of the food and clothing allowances — {data.NationalRelease.ReleaseId}");
        var line31 = Put("31", "Continuing charitable contributions", Entered(inputs.CharitableContributions), "entered (means_test_input.charitable_contributions)");
        var line32 = Put("32", "All additional expense deductions", line25 + line26 + line27 + line28 + line29 + line30 + line31, "lines 25 through 31");

        // ── deductions for debt payment (lines 33-37) ──
        Put("33a", "Copy of line 9b", line9b, "line 9b");
        Put("33b", "Copy of line 13b", line13b, "line 13b");
        Put("33c", "Copy of line 13e", line13e, "line 13e");
        var otherRows = inputs.OtherSecuredPayments.Where(payment => payment.Bucket is null or SecuredPaymentBucket.Other).ToList();
        var line33d = 0m;
        foreach (var payment in otherRows)
        {
            line33d += Entered(payment.MonthlyPayment);
        }

        Put(
            "33d",
            "Other debts secured by your property",
            line33d,
            "entered (means_test_input.other_secured_payments" + (otherRows.Count > 0 ? ": " + string.Join("; ", otherRows.Select(RowName)) : string.Empty) + ")");
        var line33e = Put("33e", "Total average monthly payment on secured debts", line9b + line13b + line13e + line33d, "lines 33a through 33d");
        var (cureTotal, cureSource) = CureTotal(inputs);
        var line34 = Put("34", "Past-due amounts on secured debts necessary for support (÷ 60)", SixtyMonthAverage(cureTotal), $"{cureSource} divided by 60 — 11 U.S.C. § 707(b)(2)(A)(iii)(II)");
        var line35 = Put("35", "Priority claims (÷ 60)", SixtyMonthAverage(Exact.Dec(@case.PriorityDebtTotal)), "the claims' priority portions divided by 60 — 11 U.S.C. § 707(b)(2)(A)(iv)");
        decimal line36;
        if (form == Form122C2 || inputs.Ch13Eligible == true)
        {
            var planPayment = Entered(inputs.Ch13ProjectedPlanPayment);
            decimal multiplier;
            try
            {
                multiplier = data.Ch13.MultiplierFor(@case.District);
            }
            catch (EngineKeyException error)
            {
                throw new MeansTestException([.. problems, Exact.Str(error)]);
            }

            line36 = Put(
                "36",
                "Chapter 13 administrative expenses",
                Exact.Quantize(planPayment * multiplier, Rounding.HalfUp),
                $"projected plan payment {Money(planPayment)} x the {@case.District} multiplier {Exact.F(multiplier)} — {data.Ch13Release.ReleaseId}; 11 U.S.C. § 707(b)(2)(A)(ii)(III)"
                    + (form == Form122C2 ? " applied by § 1325(b)(3)" : string.Empty));
        }
        else
        {
            line36 = Put("36", "Chapter 13 administrative expenses", 0m, "not eligible to file under Chapter 13 (11 U.S.C. § 109(e))");
        }

        var line37 = Put("37", "All deductions for debt payment", line33e + line34 + line35 + line36, "lines 33e + 34 + 35 + 36");
        return Put("38", "Total deductions from income", line24 + line32 + line37, "lines 24 + 32 + 37");
    }

    private static int MedianSizeOrRefuse(MeansTestInputBody inputs, string neededBy)
    {
        var medianSize = MedianHouseholdSize(inputs)
            ?? throw new MeansTestException([$"the household composition (people under 65 / 65 and older) has not been entered — {neededBy}"]);
        if (medianSize.Size < 1)
        {
            throw new MeansTestException(["the household cannot be empty"]);
        }

        return medianSize.Size;
    }

    private static MeansTestResult RunChapter7(MeansTestCase @case, MeansTestData data)
    {
        var problems = new List<string>();
        var inputs = @case.Inputs;
        var medianSize = MedianHouseholdSize(inputs);

        if (PresumptionExemption(inputs) is { } exemption)
        {
            MedianComparison? preview = null;
            if (medianSize is { Size: >= 1 } size)
            {
                try
                {
                    preview = Compare(@case.Cmi.CombinedMonthlyTotal, @case.State, size.Size, data);
                }
                catch (MeansTestException)
                {
                    preview = null;
                }
            }

            return new MeansTestResult(data.AsOf, data.ReleaseIds, preview, "exempt", exemption.Flag, [], 7);
        }

        var householdSize = MedianSizeOrRefuse(inputs, "line 5's deductions and the median comparison both need it");
        var comparison = Compare(@case.Cmi.CombinedMonthlyTotal, @case.State, householdSize, data);
        if (!comparison.AboveMedian)
        {
            return new MeansTestResult(data.AsOf, data.ReleaseIds, comparison, "below_median", "median", [], 7);
        }

        var trace = new Trace();
        decimal Put(string line, string label, decimal amount, string source) => trace.Put(Form122A2, line, label, amount, source);

        // ── Part 1: adjusted current monthly income ──
        var line1 = Put("1", "Total current monthly income", Exact.Dec(@case.Cmi.CombinedMonthlyTotal), "Form 122A-1 line 11 — the § 101(10A) derivation (core/cmi.py)");
        var (marital, maritalSource) = MaritalAdjustment(@case, "122A-1", "3", problems);
        var line3 = Put("3", "Marital adjustment", marital, maritalSource);
        var line4 = Put("4", "Adjusted current monthly income", line1 - line3, "line 1 minus line 3");

        // ── Part 2: the deductions (lines 5-38) ──
        var line38 = Deductions(@case, data, trace, Form122A2, problems);

        // ── Part 3: the determination ──
        Put("39a", "Copy of line 4, adjusted current monthly income", line4, "line 4");
        Put("39b", "Copy of line 38, total deductions", line38, "line 38");
        var line39c = Put("39c", "Monthly disposable income (11 U.S.C. § 707(b)(2))", line4 - line38, "line 39a minus line 39b");
        var line39d = Put("39d", "Total over 60 months", line39c * 60, "line 39c x 60");

        var floor = data.AmountsRelease.Amount("means-test-presumption-floor-60mo");
        var ceiling = data.AmountsRelease.Amount("means-test-presumption-ceiling-60mo");
        var thresholdSource = $"{floor.Citation} / {ceiling.Citation} — {data.AmountsRelease.ReleaseId}";
        string outcome;
        string determinedBy;
        if (line39d < floor.Value)
        {
            (outcome, determinedBy) = ("no_presumption", "threshold_floor");
            Put("40", "Presumption determination", line39d, $"line 39d is less than {floor.Amount}: no presumption of abuse ({thresholdSource})");
        }
        else if (line39d > ceiling.Value)
        {
            (outcome, determinedBy) = ("presumption_of_abuse", "threshold_ceiling");
            Put("40", "Presumption determination", line39d, $"line 39d is more than {ceiling.Amount}: the presumption of abuse arises ({thresholdSource})");
        }
        else
        {
            var nonpriority = Exact.Dec(@case.NonpriorityUnsecuredTotal);
            Put("41a", "Total nonpriority unsecured debt", nonpriority, "the claims' nonpriority unsecured portions");
            var line41b = Put("41b", "25% of total nonpriority unsecured debt", Exact.Quantize(nonpriority * 0.25m, Rounding.HalfUp), $"line 41a x 0.25 — {floor.Citation}");
            if (line39d >= line41b)
            {
                (outcome, determinedBy) = ("presumption_of_abuse", "unsecured_ratio");
                Put("42", "Presumption determination", line39d, $"line 39d is at least line 41b: the presumption of abuse arises ({thresholdSource})");
            }
            else
            {
                (outcome, determinedBy) = ("no_presumption", "unsecured_ratio");
                Put("42", "Presumption determination", line39d, $"line 39d is less than line 41b: no presumption of abuse ({thresholdSource})");
            }
        }

        if (problems.Count > 0)
        {
            throw new MeansTestException(problems);
        }

        return new MeansTestResult(data.AsOf, data.ReleaseIds, comparison, outcome, determinedBy, trace.Lines, 7);
    }

    private static MeansTestResult RunChapter13(MeansTestCase @case, MeansTestData data)
    {
        var problems = new List<string>();
        var inputs = @case.Inputs;
        var trace = new Trace();
        decimal Put(string line, string label, decimal amount, string source) => trace.Put(Form122C1, line, label, amount, source);

        var householdSize = MedianSizeOrRefuse(inputs, "line 16b's household size and line 5's deductions both need it");

        // ── B122C-1 Part 2 ──
        var line11 = Put("11", "Total average monthly income", Exact.Dec(@case.Cmi.CombinedMonthlyTotal), "Form 122C-1 line 11 — the § 101(10A) derivation (core/cmi.py)");
        var line12 = Put("12", "Copy of line 11", line11, "line 11");
        var (marital, maritalSource) = MaritalAdjustment(@case, "122C-1", "13", problems);
        var line13 = Put("13", "Marital adjustment", marital, maritalSource);
        var line14 = Put("14", "Current monthly income", line12 - line13, "line 12 minus line 13");
        Put("15a", "Copy of line 14", line14, "line 14");
        var comparison = Compare(Money(line14), @case.State, householdSize, data);
        var median = Exact.Dec(comparison.AnnualMedian);
        Put("15b", "Current monthly income for the year", line14 * 12, "line 15a x 12 — 11 U.S.C. § 1325(b)(3)");
        Put("16c", "Median family income for your state and size of household", median, comparison.Source);
        Put(
            "17",
            "Whether disposable income is determined under 11 U.S.C. § 1325(b)(3)",
            comparison.AboveMedian ? 1 : 0,
            comparison.AboveMedian
                ? "line 15b is more than line 16c: disposable income is determined under § 1325(b)(3) — fill out Form 122C-2 (line 17b)"
                : "line 15b is less than or equal to line 16c: disposable income is not determined under § 1325(b)(3) — Form 122C-2 is not filed (line 17a)");

        // ── B122C-1 Part 3: the commitment period ──
        var line18 = Put("18", "Copy of line 11", line11, "line 11");
        var line19a = inputs.CommitmentPeriodMaritalAdjustment == true
            ? Put("19a", "Marital adjustment deducted for the commitment period", line13, "line 13, contended to apply under 11 U.S.C. § 1325(b)(4) (means_test_input.commitment_period_marital_adjustment)")
            : Put("19a", "Marital adjustment deducted for the commitment period", 0m, "the marital adjustment is not contended for the commitment period (means_test_input.commitment_period_marital_adjustment)");
        var line19b = Put("19b", "Line 18 minus line 19a", line18 - line19a, "line 18 minus line 19a");
        Put("20a", "Copy of line 19b", line19b, "line 19b");
        var line20b = Put("20b", "Current monthly income for the year (commitment period)", line19b * 12, "line 20a x 12 — 11 U.S.C. § 1325(b)(4)");
        Put("20c", "Copy of line 16c", median, "line 16c");
        int months;
        string commitmentSource;
        if (line20b < median)
        {
            months = CommitmentPeriodBelowMedian;
            commitmentSource = "line 20b is less than line 20c: the commitment period is 3 years — 11 U.S.C. § 1325(b)(4)(A)(i)";
        }
        else
        {
            months = CommitmentPeriodAboveMedian;
            commitmentSource = "line 20b is more than or equal to line 20c: the commitment period is 5 years — 11 U.S.C. § 1325(b)(4)(A)(ii)";
        }

        Put("21", "Applicable commitment period (months)", months, commitmentSource);
        var commitment = new CommitmentPeriod(months, Money(line19a), Money(line19b), Money(line20b), Money(median), commitmentSource);

        if (!comparison.AboveMedian)
        {
            if (problems.Count > 0)
            {
                throw new MeansTestException(problems);
            }

            return new MeansTestResult(data.AsOf, data.ReleaseIds, comparison, "below_median", "median", trace.Lines, 13, commitment);
        }

        // ── B122C-2 Part 1: the deductions (lines 5-38) ──
        var line38 = Deductions(@case, data, trace, Form122C2, problems);

        // ── B122C-2 Part 2: disposable income under § 1325(b)(2) ──
        decimal PutC2(string line, string label, decimal amount, string source) => trace.Put(Form122C2, line, label, amount, source);

        var line39 = PutC2("39", "Current monthly income (Form 122C-1 line 14)", line14, "Form 122C-1 line 14");
        var line40 = PutC2("40", "Support income for dependent children", Entered(inputs.ChildSupportForDependents), "entered (means_test_input.child_support_for_dependents) — 11 U.S.C. § 1325(b)(2)");
        var line41 = PutC2("41", "Qualified retirement deductions", Entered(inputs.QualifiedRetirementDeductions), "entered (means_test_input.qualified_retirement_deductions) — 11 U.S.C. §§ 541(b)(7), 362(b)(19)");
        var line42 = PutC2("42", "Total of all deductions allowed under 11 U.S.C. § 707(b)(2)(A)", line38, "line 38");
        var circumstances = inputs.SpecialCircumstances;
        var line43 = 0m;
        foreach (var item in circumstances)
        {
            line43 += Entered(item.Amount);
        }

        PutC2(
            "43",
            "Deduction for special circumstances",
            line43,
            "entered (means_test_input.special_circumstances"
                + (circumstances.Count > 0 ? ": " + string.Join("; ", circumstances.Select(item => string.IsNullOrEmpty(item.Description) ? item.Id : item.Description)) : string.Empty)
                + ") — 11 U.S.C. § 707(b)(2)(B)");
        var line44 = PutC2("44", "Total adjustments", line40 + line41 + line42 + line43, "lines 40 + 41 + 42 + 43");
        var line45 = PutC2("45", "Monthly disposable income under 11 U.S.C. § 1325(b)(2)", line39 - line44, "line 39 minus line 44");

        if (problems.Count > 0)
        {
            throw new MeansTestException(problems);
        }

        return new MeansTestResult(data.AsOf, data.ReleaseIds, comparison, "above_median", "median", trace.Lines, 13, commitment, Money(line45));
    }

    /// <summary>The lines one run accumulates, in printed order.</summary>
    private sealed class Trace
    {
        public List<MeansTestLine> Lines { get; } = [];

        public decimal Put(string form, string line, string label, decimal amount, string source)
        {
            Lines.Add(new MeansTestLine(line, label, Money(amount), source, form));
            return amount;
        }
    }
}
