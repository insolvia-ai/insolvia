using Insolvia.Core.Codebtors;
using Insolvia.Core.Expenses;
using Insolvia.Core.Fields;
using Insolvia.Core.Petitions;
using Insolvia.Core.Sofa;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Tests.Support;
using static Insolvia.Engines.Tests.Support.Projected;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_form_projections.py — B121,
// B2010, B2030, Schedules H, J, J-2, the summary, the declaration and B107,
// plus the shared formatting helpers.
public sealed class ProjectionStatementTests
{
    private static CaseFile Reference => CaseFiles.ReferenceCh7;

    // --- B121 ---

    [Fact]
    public void B121_prints_names_dates_and_the_disclosed_numbers()
    {
        var values = Project("form/b121", Reference);
        Assert.Equal(Text("Ada"), values["line_1_debtor1_first_name"]);
        Assert.Equal(Text("Quinn"), values["line_1_debtor1_middle_name"]);
        Assert.Equal(Text("Lovelace"), values["line_1_debtor1_last_name"]);
        Assert.Equal(Text("Ben"), values["line_1_debtor2_first_name"]);
        Assert.False(values.ContainsKey("line_1_debtor2_middle_name"));
        Assert.Equal(Text("08/30/2026"), values["debtor1_signature_date"]);
        Assert.Equal(Text("08/30/2026"), values["debtor2_signature_date"]);
        Assert.Equal([new KeyValuePair<string, FieldFill>("Debtor1a.SSNum", Text("987-65-4321"))], Assert.IsType<RowFills>(values["line_2_debtor1_ssn"]));
        Assert.Equal([new KeyValuePair<string, FieldFill>("Debtor2a ITINNum", Text("87-65-4322"))], Assert.IsType<RowFills>(values["line_3_debtor2_itin"]));
        foreach (var fieldId in new[] { "line_3_debtor1_itin", "line_2_debtor2_ssn", "line_2_debtor1_no_ssn", "line_3_debtor1_no_itin", "line_2_debtor2_no_ssn", "line_3_debtor2_no_itin" })
        {
            Assert.False(values.ContainsKey(fieldId));
        }
    }

    [Fact]
    public void B121_without_the_logged_read_prints_the_numbers_blank()
    {
        var values = Project("form/b121", Reference with { TaxIds = new Dictionary<string, string>() });
        Assert.False(values.ContainsKey("line_2_debtor1_ssn"));
        Assert.False(values.ContainsKey("line_3_debtor2_itin"));
        Assert.Equal(Text("Ada"), values["line_1_debtor1_first_name"]);
    }

    [Fact]
    public void B101_prints_the_last_four_in_the_box_for_the_kind()
    {
        var values = Project("form/b101", Reference);
        Assert.Equal(Text("4321"), values["line_3_debtor1_ssn_last4"]);
        Assert.Equal(Text("4322"), values["line_3_debtor2_itin_last4"]);
        Assert.False(values.ContainsKey("line_3_debtor1_itin_last4"));
        Assert.False(values.ContainsKey("line_3_debtor2_ssn_last4"));
    }

    // --- B2010 / B2030 ---

    [Fact]
    public void B2010_projects_nothing() => Assert.Equal(0, Project("form/b2010", Reference).Count);

    [Fact]
    public void B2030_prints_the_fee_and_derives_the_balance()
    {
        var values = Project("form/b2030", Reference);
        Assert.Equal(Text("1,500.00"), values["line_1_fee_agreed"]);
        Assert.Equal(Text("1,000.00"), values["line_1_fee_received"]);
        Assert.Equal(Text("500.00"), values["line_1_balance_due"]);
        Assert.Equal(new CheckFill(), values["line_2_source_paid_debtor"]);
        Assert.False(values.ContainsKey("line_2_source_paid_other"));
        Assert.Equal(new CheckFill(), values["line_3_source_to_be_paid_other"]);
        Assert.Equal(Text("Margaret Lovelace, Debtor 1's mother"), values["line_3_source_to_be_paid_other_specify"]);
        Assert.Equal(new CheckFill(), values["line_4_not_shared"]);
        Assert.False(values.ContainsKey("line_4_shared"));
        Assert.Equal(Text("08/31/2026"), values["certification.date"]);
        Assert.Equal(Text("Counsel & Counsel PA"), values["certification.firm_name"]);
        Assert.Equal(Text("7"), values["caption.chapter"]);
    }

    [Fact]
    public void B2030_splits_the_district_around_the_printed_blanks()
    {
        var values = Project("form/b2030", Reference);
        Assert.Equal(Text("Middle"), Row(values, "form/b2030", "caption.district", 0));
        Assert.Equal(Text("Florida"), Row(values, "form/b2030", "caption.district", 1));
        Assert.Equal(Text("Ada Quinn Lovelace"), values["caption.debtor1_name"]);
        Assert.Equal(Text("Ben Lovelace Jr."), values["caption.debtor2_name"]);
    }

    [Fact]
    public void B2030_wraps_the_narratives_by_helvetica_width()
    {
        var excluded = Assert.IsType<RowFills>(Project("form/b2030", Reference)["line_6_excluded_services"]);
        Assert.Equal(3, excluded.Count);
        Assert.All(excluded, pair => Assert.True(Helvetica.Width(((TextFill)pair.Value).Value) <= 460));
        Assert.True(excluded.ContainsKey("services_excluded.1"));
    }

    [Fact]
    public void B2030_received_above_agreed_is_an_error()
    {
        var attorney = Reference.FilingProfessionals[0] with { CompensationReceived = 2000.00m };
        Assert.Contains("exceeds the amount agreed", Refused("form/b2030", Reference with { FilingProfessionals = [attorney] }).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B2030_without_an_attorney_prints_only_the_caption() =>
        Assert.Equal(
            new HashSet<string> { "caption.district", "caption.debtor1_name", "caption.debtor2_name", "caption.chapter" },
            Project("form/b2030", Reference with { FilingProfessionals = [] }).Keys.ToHashSet());

    [Theory]
    [InlineData("Middle District of Florida", "Middle", "Florida")]
    [InlineData("District of Columbia", null, "Columbia")]
    [InlineData("Somewhere", "Somewhere", null)]
    public void District_parts_split_around_district_of(string district, string? before, string? after) =>
        Assert.Equal((before, after), Projections.Forms.B2030.DistrictParts(district));

    // --- B106H ---

    [Fact]
    public void B106h_schedule_boxes_derive_from_what_the_links_resolve_to()
    {
        var values = Project("form/b106h", Reference);
        Assert.Equal(Option("yes"), values["line_1_any_codebtors"]);
        Assert.Equal(new CheckFill(), Row(values, "form/b106h", "line_3_schedule_d_applies", 0));
        Assert.False(HasRow(values, "form/b106h", "line_3_schedule_ef_applies", 0));
        Assert.Equal(new CheckFill(), Row(values, "form/b106h", "line_3_schedule_ef_applies", 1));
        Assert.Equal(new CheckFill(), Row(values, "form/b106h", "line_3_schedule_g_applies", 1));
        Assert.Equal(Option("no"), values["line_2_lived_in_community_state"]);
        Assert.False(values.ContainsKey("line_2_spouse_name"));
    }

    [Fact]
    public void B106h_prints_one_community_property_block()
    {
        var member = new CommunityHouseholdMemberBody("Dana Lovelace", new Address("4 Alamo Way", City: "San Antonio", State: "TX", PostalCode: "78205"), "TX", true);
        var values = Project("form/b106h", Reference with { CommunityHouseholdMembers = [member] });
        Assert.Equal(Option("yes"), values["line_2_lived_in_community_state"]);
        Assert.Equal(Text("TX"), values["line_2_community_state"]);
        Assert.Equal(Option("yes"), values["line_2_spouse_lived_with_you"]);
        Assert.Contains("one community-property block", Refused("form/b106h", Reference with { CommunityHouseholdMembers = [member, member] }).Message, StringComparison.Ordinal);
    }

    // --- B106J / B106J-2 ---

    [Fact]
    public void B106j_expense_rows_land_on_their_lines_and_lines_22_23_derive()
    {
        var values = Project("form/b106j", Reference);
        Assert.Equal(Option("yes"), values["line_1_joint_case"]);
        Assert.Equal(Option("no"), values["line_1_debtor2_separate_household"]);
        Assert.Equal(Text("1,480.00"), values["line_4_rent_or_home_ownership"]);
        Assert.Equal(Text("150.00"), values["line_16_taxes"]);
        Assert.Equal(Text("Self-employment tax estimate"), values["line_16_taxes_specify"]);
        Assert.Equal(Text("260.00"), values["line_17a_car_payment_vehicle1"]);
        Assert.Equal(Text("55.00"), values["line_17c_installment_other_1"]);
        Assert.Equal(Text("Dog food and veterinary care"), values["line_21_other_specify"]);
        Assert.Equal(Text("Daughter"), Row(values, "form/b106j", "line_2_dependent_relationship", 0));
        Assert.Equal(Text("9"), Row(values, "form/b106j", "line_2_dependent_age", 0));
        Assert.Equal(Option("yes"), Row(values, "form/b106j", "line_2_dependent_lives_with_you", 0));
        Assert.Equal(Text("5,060.00"), values["line_22a_total_expenses"]);
        Assert.False(values.ContainsKey("line_22b_debtor2_expenses"));
        Assert.Equal(Text("5,060.00"), values["line_22c_monthly_expenses"]);
        Assert.Equal(Text("8,149.50"), values["line_23a_combined_monthly_income"]);
        Assert.Equal(Text("3,089.50"), values["line_23c_net_income"]);
    }

    [Fact]
    public void B106j2_projects_only_its_gate_without_a_second_household()
    {
        var values = Project("form/b106j2", Reference);
        Assert.Equal(Option("no"), values["line_1_separate_households"]);
        Assert.False(values.ContainsKey("line_22_monthly_expenses"));
    }

    [Fact]
    public void B106j2_prints_the_second_household_and_carries_to_106j()
    {
        var second = ("hh-2", new HouseholdBody(WhichHousehold.Debtor2Separate, true, null, null, null));
        var caseFile = Reference with
        {
            Households = [.. Reference.Households, second],
            Expenses = [.. Reference.Expenses, new ExpenseBody("hh-2", ExpenseCategory.RentOrHomeOwnership, null, 900.00m), new ExpenseBody("hh-2", ExpenseCategory.FoodAndHousekeeping, null, 400.00m)],
        };
        var values = Project("form/b106j2", caseFile);
        Assert.Equal(Option("yes"), values["line_1_separate_households"]);
        Assert.Equal(Text("900.00"), values["line_4_rent_or_home_ownership"]);
        Assert.Equal(Text("1,300.00"), values["line_22_monthly_expenses"]);
        var j = Project("form/b106j", caseFile);
        Assert.Equal(Text("1,300.00"), j["line_22b_debtor2_expenses"]);
        Assert.Equal(Text("6,360.00"), j["line_22c_monthly_expenses"]);
    }

    // --- B106Sum and B106Dec ---

    [Fact]
    public void B106sum_copies_every_line_from_the_schedules()
    {
        var values = Project("form/b106sum", Reference);
        Assert.Equal(Text("240,000.00"), values["line_1a_total_real_estate"]);
        Assert.Equal(Text("72,550.00"), values["line_1b_total_personal_property"]);
        Assert.Equal(Text("312,550.00"), values["line_1c_total_property"]);
        Assert.Equal(Text("202,400.00"), values["line_2_secured_claims_total"]);
        Assert.Equal(Text("3,650.00"), values["line_3a_priority_unsecured_total"]);
        Assert.Equal(Text("22,800.00"), values["line_3b_nonpriority_unsecured_total"]);
        Assert.Equal(Text("228,850.00"), values["line_3_total_liabilities"]);
        Assert.Equal(Text("8,149.50"), values["line_4_combined_monthly_income"]);
        Assert.Equal(Text("5,060.00"), values["line_5_monthly_expenses"]);
        Assert.Equal(Option("yes"), values["line_6_filing_under_7_11_13"]);
        Assert.Equal(Option("consumer"), values["line_7_kind_of_debt"]);
        Assert.False(values.ContainsKey("line_8_current_monthly_income"));
        Assert.Equal(Text("3,650.00"), values["line_9b_taxes_government"]);
        Assert.Equal(Text("12,000.00"), values["line_9d_student_loans"]);
        Assert.Equal(Text("15,650.00"), values["line_9g_total"]);
    }

    [Fact]
    public void B106dec_answers_the_preparer_question_and_dates_the_signatures()
    {
        var values = Project("form/b106dec", Reference);
        Assert.Equal(Option("no"), values["paid_nonattorney_preparer"]);
        Assert.False(values.ContainsKey("preparer_name"));
        Assert.Equal(Text("08/30/2026"), values["debtor1_signature_date"]);
        Assert.Equal(Text("08/30/2026"), values["debtor2_signature_date"]);
        Assert.False(values.ContainsKey("debtor1_signature"));
    }

    // --- B107 ---

    [Fact]
    public void B107_q4_buckets_income_by_period_start_year()
    {
        var values = Project("form/b107", Reference);
        Assert.Equal(Text("41,600.00"), values["q4_debtor1_current_gross"]);
        Assert.Equal(Text("65,200.00"), values["q4_debtor1_last_year_gross"]);
        Assert.Equal(new CheckFill(), values["q4_debtor1_last_year_wages"]);
        Assert.Equal(new CheckFill(), values["q4_debtor1_last_year_business"]);
        Assert.Equal(Text("58,300.00"), values["q4_debtor1_year_before_gross"]);
        Assert.Equal(Text("24,000.00"), values["q4_debtor2_current_gross"]);
        Assert.Equal(Text("2025"), values["q4_last_year_yyyy"]);
        Assert.Equal(Text("2024"), values["q4_year_before_yyyy"]);
        Assert.Equal(Text("Unemployment compensation"), Row(values, "form/b107", "q5_debtor1_source", 0));
        Assert.False(values.ContainsKey("q5_gate"));
    }

    [Fact]
    public void B107_q4_refuses_a_period_outside_the_three_printed_years()
    {
        var stale = new SofaEntryBody(SofaEntryType.IncomeByPeriod, new IncomeByPeriod(SofaDebtor.Debtor1, IncomeKind.WagesAndCommissions, null, new DateOnly(2020, 1, 1), null, 100.00m));
        Assert.Contains("fits none", Refused("form/b107", Reference with { SofaEntries = [.. Reference.SofaEntries, stale] }).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B107_q6_answers_the_consumer_branch_from_the_entries()
    {
        var values = Project("form/b107", Reference);
        Assert.Equal(Option("yes"), values["q6_consumer_debts"]);
        Assert.Equal(Option("yes"), values["q6_paid_600"]);
        Assert.False(values.ContainsKey("q6_paid_8575"));
        Assert.Equal(Text("05/01/2026"), Row(values, "form/b107", "q6_payment_dates", 0));
        Assert.Equal(Text("07/01/2026"), Row(values, "form/b107", "q6_payment_dates", 2));
    }

    [Fact]
    public void B107_q6_refuses_a_payment_below_the_reporting_floor() =>
        Assert.Contains("below the 600.00", Refused("form/b107", WithPayment(Reference, Payment("Corner Store", 599.99m))).Message, StringComparison.Ordinal);

    [Fact]
    public void B107_q6_business_branch_compares_against_the_8575_floor()
    {
        var business = WithPayment(Reference, Payment("Supplier Co", 8574.99m));
        business = business with { Petition = business.Petition! with { DebtCharacter = DebtCharacter.Business } };
        Assert.Contains("below the 8575.00", Refused("form/b107", business).Message, StringComparison.Ordinal);
        var values = Project("form/b107", WithPayment(business, Payment("Supplier Co", 8575.00m)));
        Assert.Equal(Option("yes"), values["q6_paid_8575"]);
        Assert.False(values.ContainsKey("q6_paid_600"));
    }

    [Fact]
    public void B107_q6_a_payment_without_a_total_answers_by_presence() =>
        Assert.Equal(Option("yes"), Project("form/b107", WithPayment(Reference, Payment("Gulf Coast Home Loans", null)))["q6_paid_600"]);

    [Fact]
    public void B107_q2_both_debtors_share_a_row_via_the_same_as_boxes()
    {
        var shared = new SofaEntryBody(SofaEntryType.PriorAddress, new PriorAddress(SofaDebtor.Both, new Address("2 Shared Street", City: "Tampa", State: "FL", PostalCode: "33601"), new DateOnly(2022, 1, 1), new DateOnly(2022, 12, 31)));
        var values = Project("form/b107", Reference with { SofaEntries = [shared] });
        Assert.Equal(Text("2 Shared Street"), Row(values, "form/b107", "q2_debtor1_street", 0));
        Assert.Equal(new CheckFill(), Row(values, "form/b107", "q2_debtor2_same_as_debtor1", 0));
        Assert.Equal(new CheckFill(), Row(values, "form/b107", "q2_debtor2_same_dates", 0));
    }

    [Fact]
    public void B107_q26_merged_group_needs_the_widget_escape_hatch()
    {
        Assert.Equal(Option("no"), Project("form/b107", Reference)["q26_gate_and_status"]);
        var proceeding = new SofaEntryBody(SofaEntryType.EnvironmentalProceeding, new EnvironmentalProceeding("State of Florida v. Lovelace", "26-ENV-001", new Party("Second District Court of Appeal", new Address()), "Stormwater runoff citation appeal", LegalStatus.OnAppeal));
        var values = Project("form/b107", Reference with { SofaEntries = [.. Reference.SofaEntries, proceeding] });
        Assert.Equal(WidgetStatesFill.ByStates("yes", "on appeal"), values["q26_gate_and_status"]);
        Assert.Equal(Text("26-ENV-001"), values["q26_case_number"]);
    }

    [Fact]
    public void B107_shared_widget_boxes_refuse_a_value_they_cannot_hold()
    {
        var second = new SofaEntryBody(SofaEntryType.ClosedAccount, new ClosedAccount(new Party("Second Bank", new Address("9 Other Road", City: "Ocala", State: "FL", PostalCode: "34470")), "7788", AccountType.Savings, null, null));
        Assert.Contains("second widget of row one", Refused("form/b107", Reference with { SofaEntries = [.. Reference.SofaEntries, second] }).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B107_q27_strips_the_ein_and_checks_the_connection()
    {
        var values = Project("form/b107", Reference);
        var entry = values["q27_ein"];
        Assert.True(entry == Text("123456789") || (entry is RowFills rows && rows.Any(pair => pair.Value == Text("123456789"))));
        Assert.Equal(new CheckFill(), values["q27_connection_sole_proprietor"]);
        Assert.False(values.ContainsKey("q27_connection_partner"));
    }

    [Fact]
    public void B107_signature_block_prints_dates_and_the_preparer_answer()
    {
        var values = Project("form/b107", Reference);
        Assert.Equal(Text("08/30/2026"), values["sign.debtor1_date"]);
        Assert.Equal(Option("no"), values["sign.paid_preparer"]);
        Assert.False(values.ContainsKey("sign.attached_pages"));
    }

    // --- the shared helpers ---

    [Theory]
    [InlineData("1234.5", "1,234.50")]
    [InlineData("5200.00", "5,200.00")]
    [InlineData("1234567.89", "1,234,567.89")]
    public void Money_prints_grouped_with_two_decimals(string stored, string printed) =>
        Assert.Equal(printed, ProjectionHelpers.FormatMoney(Py.Dec(stored)));

    [Fact]
    public void Dates_print_as_the_forms_spell_them() =>
        Assert.Equal("03/04/2019", ProjectionHelpers.FormatDate(new DateOnly(2019, 3, 4)));

    [Fact]
    public void Wrapping_overflow_is_a_problem_not_a_truncation()
    {
        var problems = new List<string>();
        var lines = ProjectionHelpers.WrapLines("one two three four five six", 9, 2, "where", problems);
        Assert.Equal(["one two", "three"], lines);
        Assert.Equal(["where: text needs 4 lines; the form prints 2"], problems);
    }

    private static CreditorPayment Payment(string creditor, decimal? total) => new(new Party(creditor, new Address()), [], total, null, [], null);

    private static CaseFile WithPayment(CaseFile caseFile, CreditorPayment payment) =>
        caseFile with { SofaEntries = [.. caseFile.SofaEntries.Where(entry => entry.Payload is not CreditorPayment), new SofaEntryBody(SofaEntryType.CreditorPayment, payment)] };
}
