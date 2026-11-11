using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Plans;

// From plans.py: the Chapter 13 plan's PROPOSAL (issue 16.2 / #366), one per
// case. Its figures are the calculator's, never stored.

/// <summary>From <see cref="StartMonth"/> on (1-based, inclusive) the monthly payment is <see cref="MonthlyPayment"/>.</summary>
public sealed record StepPayment(
    string Id,
    [property: Count(PlanRules.MaxTermMonths)] int? StartMonth = null,
    [property: Money] decimal? MonthlyPayment = null) : IRow, IValidatableObject
{
    /// <summary>The month is one of the plan's.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        PlanRules.CheckMonth(StartMonth, nameof(StartMonth));
}

/// <summary>A one-time payment into the plan in <see cref="Month"/>.</summary>
public sealed record LumpSum(
    string Id,
    [property: Count(PlanRules.MaxTermMonths)] int? Month = null,
    [property: Money] decimal? Amount = null,
    [property: Text] string? Description = null) : IRow, IValidatableObject
{
    /// <summary>The month is one of the plan's.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        PlanRules.CheckMonth(Month, nameof(Month));
}

/// <summary>
/// How the plan treats one secured claim: cure-and-maintain (arrearage cured,
/// optional conduit maintenance), cramdown (paid to the collateral's value)
/// or surrender (nothing through the plan). <see cref="MonthlyPayment"/>
/// fixes the equal monthly payment; absent, the calculator amortises.
/// </summary>
public sealed record SecuredTreatment(
    string Id,
    [property: Text(64)] string? ClaimId = null,
    SecuredTreatmentKind? Treatment = null,
    [property: Money] decimal? Arrearage = null,
    [property: Rate] decimal? ArrearageInterestRate = null,
    [property: Money] decimal? MaintenancePayment = null,
    [property: Money] decimal? CramdownValue = null,
    [property: Rate] decimal? InterestRate = null,
    [property: Money] decimal? MonthlyPayment = null) : IRow;

/// <summary>The plan's proposal.</summary>
public sealed record PlanBody(
    [property: Count(PlanRules.MaxTermMonths)] int? TermMonths = null,
    PaymentSource? PaymentSource = null,
    [property: Money] decimal? MonthlyPayment = null,
    ValueCollection<StepPayment>? StepPayments = null,
    ValueCollection<LumpSum>? LumpSums = null,
    [property: Rate(PlanRules.MaxTrusteePercentage)] decimal? TrusteePercentage = null,
    [property: Money] decimal? AttorneyFees = null,
    [property: Money] decimal? AttorneyFeeMonthly = null,
    ValueCollection<SecuredTreatment>? SecuredTreatments = null,
    [property: Rate] decimal? PriorityPercentage = null,
    [property: Rate] decimal? PriorityInterestRate = null,
    UnsecuredTreatment? UnsecuredTreatment = null,
    [property: Rate] decimal? UnsecuredPercentage = null,
    [property: Money] decimal? UnsecuredAmount = null,
    [property: Rate] decimal? UnsecuredInterestRate = null,
    [property: Money] decimal? Chapter7OtherCosts = null,
    [property: Text] string? Chapter7OtherCostsDescription = null,
    [property: Rate] decimal? PresentValueRate = null) : IValidatableObject
{
    /// <summary>The step schedule.</summary>
    [Rows]
    public ValueCollection<StepPayment> StepPayments { get; init; } = StepPayments ?? [];

    /// <summary>The one-time payments.</summary>
    [Rows]
    public ValueCollection<LumpSum> LumpSums { get; init; } = LumpSums ?? [];

    /// <summary>The secured claims' treatments.</summary>
    [Rows]
    public ValueCollection<SecuredTreatment> SecuredTreatments { get; init; } = SecuredTreatments ?? [];

    /// <summary>A plan runs at least a month; steps start in distinct months; a claim has one treatment.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TermMonths == 0)
        {
            yield return new ValidationResult($"A plan runs 1 to {PlanRules.MaxTermMonths} months — § 1322(d) caps it at five years.", [nameof(TermMonths)]);
        }

        var starts = new HashSet<int>();
        for (var index = 0; index < StepPayments.Count; index++)
        {
            if (StepPayments[index].StartMonth is { } start && !starts.Add(start))
            {
                yield return new ValidationResult("Another step already starts in this month.", [$"stepPayments[{index}].startMonth"]);
            }
        }

        var treated = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < SecuredTreatments.Count; index++)
        {
            if (SecuredTreatments[index].ClaimId is { } claimId && !treated.Add(claimId))
            {
                yield return new ValidationResult("This claim already has a treatment.", [$"securedTreatments[{index}].claimId"]);
            }
        }
    }
}

/// <summary>The plan collection.</summary>
public static class PlanRules
{
    /// <summary>§ 1322(d) caps a plan at five years.</summary>
    public const int MaxTermMonths = 60;

    /// <summary>The trustee's percentage fee is capped at ten per cent.</summary>
    public const int MaxTrusteePercentage = 10;

    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<PlanBody> Kind = new("plan", "plans", "PLAN");

    /// <summary>A month of the plan is 1-based: 0 is refused.</summary>
    /// <param name="month">The month.</param>
    /// <param name="member">The member that holds it.</param>
    /// <returns>The failures.</returns>
    internal static IEnumerable<ValidationResult> CheckMonth(int? month, string member)
    {
        if (month == 0)
        {
            yield return new ValidationResult($"Must be a month of the plan, 1 to {MaxTermMonths}.", [member]);
        }
    }
}

/// <summary>Where a Chapter 13 plan's payment figure comes from (Python's <c>PAYMENT_SOURCES</c>).</summary>
public enum PaymentSource
{
    /// <summary>The wire value <c>fixed</c>.</summary>
    [JsonStringEnumMemberName("fixed")]
    Fixed,

    /// <summary>The wire value <c>schedule_j_excess</c>.</summary>
    [JsonStringEnumMemberName("schedule_j_excess")]
    ScheduleJExcess,

    /// <summary>The wire value <c>disposable_income</c>.</summary>
    [JsonStringEnumMemberName("disposable_income")]
    DisposableIncome,
}

/// <summary>How a plan treats a secured claim (Python's <c>SECURED_TREATMENTS</c>).</summary>
public enum SecuredTreatmentKind
{
    /// <summary>The wire value <c>cure_and_maintain</c>.</summary>
    [JsonStringEnumMemberName("cure_and_maintain")]
    CureAndMaintain,

    /// <summary>The wire value <c>cramdown</c>.</summary>
    [JsonStringEnumMemberName("cramdown")]
    Cramdown,

    /// <summary>The wire value <c>surrender</c>.</summary>
    [JsonStringEnumMemberName("surrender")]
    Surrender,
}

/// <summary>How a plan states what general unsecured creditors receive (Python's <c>UNSECURED_TREATMENTS</c>).</summary>
public enum UnsecuredTreatment
{
    /// <summary>The wire value <c>pot</c>.</summary>
    [JsonStringEnumMemberName("pot")]
    Pot,

    /// <summary>The wire value <c>percentage</c>.</summary>
    [JsonStringEnumMemberName("percentage")]
    Percentage,

    /// <summary>The wire value <c>amount</c>.</summary>
    [JsonStringEnumMemberName("amount")]
    Amount,
}
