using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Core.Debtors;
using Insolvia.Core.Petitions;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B101 @ 2024-06-22 (revision 06/24) — the voluntary petition's mapping
/// (from <c>form_projections/b101.py</c>). Line 3 prints the tax
/// identifier's last four from the debtor record; the amended caption,
/// wet signatures, the court's case number and the pro se acknowledgments
/// stay blank.
/// </summary>
public static partial class B101
{
    private static readonly Dictionary<int, string> ChapterExports = new() { [7] = "Chapter 7", [11] = "Chapter 11", [12] = "Chapter 12", [13] = "Chapter 13" };

    private static readonly Dictionary<FeeHandling, string> FeeHandlingExports = new()
    {
        [FeeHandling.Full] = "Pay entirely",
        [FeeHandling.Installments] = "Pay in installments",
        [FeeHandling.Waiver] = "Request fees waived",
    };

    private static readonly Dictionary<VenueBasis, string> VenueExports = new()
    {
        [VenueBasis.LivedLongest180Days] = "Lived in this district",
        [VenueBasis.Other] = "Other",
    };

    // Line 15's exports, verified against the PDF's widget geometry at ingestion.
    private static readonly Dictionary<CounselingStatus, string> CounselingExports = new()
    {
        [CounselingStatus.CompletedWithCertificate] = "1",
        [CounselingStatus.CompletedCertificatePending] = "2",
        [CounselingStatus.ExigentCircumstancesWaiverRequested] = "On",
        [CounselingStatus.NotRequired] = "4",
    };

    private static readonly Dictionary<CounselingExemption, string> CounselingExemptionExports = new()
    {
        [CounselingExemption.Incapacity] = "Incapacity",
        [CounselingExemption.Disability] = "Disability",
        [CounselingExemption.ActiveDuty] = "Active duty",
    };

    /// <summary>The values for <c>form/b101@2024-06-22</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        var @case = caseFile.Case;
        var debtor1 = caseFile.Debtor(FilingRole.Debtor1);
        var debtor2 = caseFile.Debtor(FilingRole.Debtor2);

        values["caption.district"] = new TextFill(@case.District);
        values["caption.chapter"] = new OptionFill(ChapterExports[@case.Chapter]);
        if (FullName(debtor1) is { } header)
        {
            values["caption.header_debtor1_name"] = new TextFill(header);
        }

        // Part 1 — identity, per debtor column.
        foreach (var (debtor, col) in new[] { (debtor1, "debtor1"), (debtor2, "debtor2") })
        {
            if (debtor is null)
            {
                continue;
            }

            var body = debtor.Body;
            foreach (var (value, box) in new[] { (body.Name.Given, "first_name"), (body.Name.Middle, "middle_name"), (body.Name.Surname, "last_name"), (body.Name.Suffix, "suffix") })
            {
                if (!string.IsNullOrEmpty(value))
                {
                    values[$"line_1_{col}_{box}"] = new TextFill(value);
                }
            }

            // Line 2 — the 8-year alias rows.
            var aliases = body.OtherNamesUsed;
            Rows(release, values, $"line_2_{col}_other_given", aliases.Select(alias => alias.Given), problems);
            Rows(release, values, $"line_2_{col}_other_middle", aliases.Select(alias => alias.Middle), problems);
            Rows(release, values, $"line_2_{col}_other_surname", aliases.Select(alias => alias.Surname), problems);
            Rows(release, values, $"line_2_{col}_business_name", aliases.Select(alias => alias.BusinessName), problems);

            // Line 3 — the last four, in the box for the identifier's kind.
            if (debtor.TaxId is { } taxId)
            {
                values[$"line_3_{col}_{taxId.Kind.ToWire()}_last4"] = new TextFill(taxId.LastFour);
            }

            // Line 4 — EINs; the boxes are 9-digit combs, so the dash is stripped.
            Rows(release, values, $"line_4_{col}_ein", body.EmployerIds.Select(ein => NonDigits().Replace(ein, string.Empty) is { Length: > 0 } digits ? digits : null), problems);

            // Line 5 — where the debtor lives; mailing only when it differs.
            AddressFills(values, body.ResidenceAddress, $"line_5_{col}_street", $"line_5_{col}_street2", $"line_5_{col}_city", $"line_5_{col}_state", $"line_5_{col}_zip", $"line_5_{col}_county");
            if (body.MailingAddress != new Core.Fields.Address() && body.MailingAddress != body.ResidenceAddress)
            {
                AddressFills(values, body.MailingAddress, $"line_5_{col}_mailing_street", $"line_5_{col}_mailing_pobox", $"line_5_{col}_mailing_city", $"line_5_{col}_mailing_state", $"line_5_{col}_mailing_zip");
            }

            // Line 6 — venue.
            if (body.Venue.Basis is { } basis)
            {
                values[$"line_6_{col}_venue_basis"] = new OptionFill(VenueExports[basis]);
            }

            if (!string.IsNullOrEmpty(body.Venue.Explanation))
            {
                Rows(release, values, $"line_6_{col}_venue_explanation", WrapLines(body.Venue.Explanation, 85, 4, $"line_6_{col}_venue_explanation", problems), problems);
            }

            // Line 15 — credit counseling.
            var counseling = body.CreditCounseling;
            if (counseling.Status is { } status)
            {
                values[$"line_15_{col}_credit_counseling"] = new OptionFill(CounselingExports[status]);
            }

            if (counseling.Status == CounselingStatus.NotRequired && counseling.ExemptionReason is { } reason)
            {
                values[$"line_15_{col}_exemption_reason"] = new OptionFill(CounselingExemptionExports[reason]);
            }

            if (body.SignedAt is { } signed)
            {
                values[$"sign.{col}_executed_on"] = new TextFill(FormatDate(signed));
            }
        }

        // Line 9 — prior bankruptcies.
        values["line_9_prior_bankruptcy"] = YesNo(release, "line_9_prior_bankruptcy", caseFile.PriorCases.Count > 0);
        Rows(release, values, "line_9_prior_district", caseFile.PriorCases.Select(prior => prior.District), problems);
        Rows(release, values, "line_9_prior_when", caseFile.PriorCases.Select(prior => prior.FiledOn is { } on ? FormatDate(on) : null), problems);
        Rows(release, values, "line_9_prior_case_number", caseFile.PriorCases.Select(prior => prior.CaseNumber), problems);

        // Line 10 — related pending cases.
        var related = caseFile.RelatedCases;
        values["line_10_related_pending"] = YesNo(release, "line_10_related_pending", related.Count > 0);
        Rows(release, values, "line_10_related_debtor", related.Select(r => r.DebtorName), problems);
        Rows(release, values, "line_10_related_relationship", related.Select(r => r.Relationship), problems);
        Rows(release, values, "line_10_related_district", related.Select(r => r.District), problems);
        Rows(release, values, "line_10_related_when", related.Select(r => r.FiledOn is { } on ? FormatDate(on) : null), problems);
        Rows(release, values, "line_10_related_case_number", related.Select(r => r.CaseNumber), problems);

        // Line 12 — sole proprietorships (ONE printed block).
        values["line_12_sole_proprietor"] = YesNo(release, "line_12_sole_proprietor", caseFile.SoleProprietorships.Count > 0);
        if (caseFile.SoleProprietorships.Count > 1)
        {
            problems.Add($"line_12: the form prints one sole-proprietorship block; the case holds {caseFile.SoleProprietorships.Count}");
        }

        if (caseFile.SoleProprietorships.Count > 0)
        {
            var business = caseFile.SoleProprietorships[0];
            if (!string.IsNullOrEmpty(business.Name))
            {
                values["line_12_business_name"] = new TextFill(business.Name);
            }

            AddressFills(values, business.Address, "line_12_business_street", "line_12_business_street2", "line_12_business_city", "line_12_business_state", "line_12_business_zip");
            if (business.BusinessType is { } businessType)
            {
                values["line_12_business_type"] = ByOrder(release, "line_12_business_type", businessType);
            }
        }

        // Line 17 — the chapter answers it.
        values["line_17_filing_under_ch7"] = YesNo(release, "line_17_filing_under_ch7", @case.Chapter == 7);

        // Parts 2, 4, 6 — the petition's answers.
        if (caseFile.Petition is { } petition)
        {
            if (petition.FeeHandling is { } fees)
            {
                values["line_8_fee_handling"] = new OptionFill(FeeHandlingExports[fees]);
            }

            if (petition.RentsResidence is { } rents)
            {
                values["line_11_rents_residence"] = YesNo(release, "line_11_rents_residence", rents);
                if (rents && petition.EvictionJudgmentAgainstYou is { } eviction)
                {
                    values["line_11_eviction_judgment"] = YesNo(release, "line_11_eviction_judgment", eviction);
                }
            }

            if (petition.SmallBusinessStatus is { } small)
            {
                values["line_13_small_business_status"] = ByOrder(release, "line_13_small_business_status", small);
            }

            // Line 14 — hazardous property.
            var hazard = petition.HazardousProperty;
            var hasHazard = !string.IsNullOrEmpty(hazard.Description) || !string.IsNullOrEmpty(hazard.WhyImmediate) || hazard.Address != new Core.Fields.Address();
            values["line_14_hazardous_property"] = YesNo(release, "line_14_hazardous_property", hasHazard);
            if (!string.IsNullOrEmpty(hazard.Description))
            {
                Rows(release, values, "line_14_hazard_description", WrapLines(hazard.Description, 95, 2, "line_14_hazard_description", problems), problems);
            }

            if (!string.IsNullOrEmpty(hazard.WhyImmediate))
            {
                Rows(release, values, "line_14_why_immediate", WrapLines(hazard.WhyImmediate, 95, 2, "line_14_why_immediate", problems), problems);
            }

            AddressFills(values, hazard.Address, "line_14_property_street", "line_14_property_street2", "line_14_property_city", "line_14_property_state", "line_14_property_zip");

            // Line 16 — the debt-character three-way, printed as two gates.
            if (petition.DebtCharacter is { } character)
            {
                var consumer = character == DebtCharacter.Consumer;
                values["line_16a_consumer_debts"] = YesNo(release, "line_16a_consumer_debts", consumer);
                if (!consumer)
                {
                    values["line_16b_business_debts"] = YesNo(release, "line_16b_business_debts", character == DebtCharacter.Business);
                }

                if (character == DebtCharacter.Other && !string.IsNullOrEmpty(petition.DebtCharacterOther))
                {
                    values["line_16c_other_debts"] = new TextFill(petition.DebtCharacterOther);
                }
            }

            if (@case.Chapter == 7 && petition.Ch7FundsAvailableForCreditors is { } funds)
            {
                values["line_17_funds_available"] = YesNo(release, "line_17_funds_available", funds);
            }

            // Lines 18-20 — the estimate brackets map by ORDER.
            if (petition.EstimatedCreditors is { } creditors)
            {
                values["line_18_estimated_creditors"] = ByOrder(release, "line_18_estimated_creditors", creditors);
            }

            if (petition.EstimatedAssets is { } assets)
            {
                values["line_19_estimated_assets"] = ByOrder(release, "line_19_estimated_assets", assets);
            }

            if (petition.EstimatedLiabilities is { } liabilities)
            {
                values["line_20_estimated_liabilities"] = ByOrder(release, "line_20_estimated_liabilities", liabilities);
            }
        }

        // Part 7 — who signs.
        var attorney = caseFile.FilingProfessionals.FirstOrDefault(p => p.Role == FilingProfessionalRole.Attorney);
        var preparer = caseFile.FilingProfessionals.FirstOrDefault(p => p.Role == FilingProfessionalRole.BankruptcyPetitionPreparer);
        if (attorney is not null)
        {
            if (FullName(attorney.Name) is { } printed)
            {
                values["attorney.printed_name"] = new TextFill(printed);
            }

            if (!string.IsNullOrEmpty(attorney.FirmName))
            {
                values["attorney.firm_name"] = new TextFill(attorney.FirmName);
            }

            AddressFills(values, attorney.Address, "attorney.street", "attorney.street2", "attorney.city", "attorney.state", "attorney.zip");
            foreach (var (fieldId, value) in new[] { ("attorney.phone", attorney.Phone), ("attorney.email", attorney.Email), ("attorney.bar_number", attorney.BarNumber), ("attorney.bar_state", attorney.BarState) })
            {
                if (!string.IsNullOrEmpty(value))
                {
                    values[fieldId] = new TextFill(value);
                }
            }

            if (attorney.SignatureDate is { } signed)
            {
                values["attorney.date_signed"] = new TextFill(FormatDate(signed));
            }
        }
        else
        {
            values["prose.paid_preparer"] = YesNo(release, "prose.paid_preparer", preparer is not null);
            if (preparer is not null && FullName(preparer.Name) is { } preparerName)
            {
                values["prose.preparer_name"] = new TextFill(preparerName);
            }

            foreach (var (debtor, col) in new[] { (debtor1, "debtor1"), (debtor2, "debtor2") })
            {
                if (debtor is null)
                {
                    continue;
                }

                foreach (var (fieldId, value) in new[] { ($"prose.{col}_phone", debtor.Body.Phone), ($"prose.{col}_cell", debtor.Body.Mobile), ($"prose.{col}_email", debtor.Body.Email) })
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        values[fieldId] = new TextFill(value);
                    }
                }

                if (debtor.Body.SignedAt is { } signed)
                {
                    values[$"prose.{col}_date"] = new TextFill(FormatDate(signed));
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();
}
