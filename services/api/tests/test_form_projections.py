"""Projection goldens (issue #93): a reference case, rendered end to end.

test_form_fill.py proves the ENGINE with synthetic full-coverage values; this
file proves the MAPPING: a semantically coherent reference case — a joint
Chapter 7 in the Middle District of Florida with aliases, a prior case, a
related case, a sole proprietorship, hazardous property, an attorney, and
two full income columns — projected through core/form_projections.py, filled
through the engine, and pinned to goldens the same three-layer way (field
read-back, official pages untouched via the engine's own suite, sha256).
Regeneration is the same deliberate act: UPDATE_FORM_GOLDENS=1.

The targeted tests below the goldens are the ones a reviewer should read
first — they state the mappings in assertable form: line 15's verified
'1/2/On/4' exports, line 16's three-way-to-two-gates translation, line 19
selecting the PDF's misprinted band export BY POSITION, and the derived
arithmetic the model refuses to store.
"""

import hashlib
import json
import os
from pathlib import Path

import pytest
from insolvia_api.core.assets import AssetBody
from insolvia_api.core.cases import Case
from insolvia_api.core.claims import ClaimBody, NoticeParty
from insolvia_api.core.codebtors import CodebtorBody, CommunityHouseholdMemberBody
from insolvia_api.core.contract_leases import ContractLeaseBody
from insolvia_api.core.creditors import CreditorBody
from insolvia_api.core.debtors import CreditCounseling, Debtor, OtherName, Venue
from insolvia_api.core.exemption_claims import ExemptionBody
from insolvia_api.core.expenses import DependentBody, ExpenseBody, HouseholdBody
from insolvia_api.core.fields import Address, PersonName
from insolvia_api.core.form_fill import Check, Option, Text, WidgetStates, fill_form
from insolvia_api.core.form_projections import (
    CaseFile,
    FormProjectionError,
    format_date,
    format_money,
    project,
)
from insolvia_api.core.form_templates import get_form, latest_form
from insolvia_api.core.income import EmploymentBody, IncomeSummaryBody
from insolvia_api.core.petitions import (
    FilingProfessionalBody,
    HazardousProperty,
    PetitionBody,
    PriorCaseBody,
    RelatedCaseBody,
    SoleProprietorshipBody,
)
from insolvia_api.core.sofa import (
    BusinessConnection,
    CharitableContribution,
    ClosedAccount,
    ConsultantPayment,
    CreditorPayment,
    EnvironmentalProceeding,
    FinancialStatementIssued,
    Gift,
    HeldForAnother,
    IncomeByPeriod,
    InsiderPayment,
    Lawsuit,
    Loss,
    MaritalStatus,
    Party,
    PriorAddress,
    PropertyTransfer,
    SofaEntryBody,
    StorageUnit,
)

from tests.test_form_fill import read_form

GOLDEN_DIR = Path(__file__).resolve().parent / "goldens"

REFERENCE_CASE = Case(
    id="case-reference-0001",
    firm_id="firm-0001",
    created_by="subject-0001",
    chapter=7,
    district="Middle District of Florida",
    status="intake",
    created_at="2026-08-01T12:00:00Z",
    updated_at="2026-09-01T12:00:00Z",
)


def _debtor_1() -> Debtor:
    return Debtor(
        id="debtor-0001",
        case_id=REFERENCE_CASE.id,
        filing_role="debtor_1",
        created_at="2026-08-01T12:00:00Z",
        updated_at="2026-08-01T12:00:00Z",
        name=PersonName(given="Ada", middle="Quinn", surname="Lovelace"),
        other_names_used=(
            OtherName(id="alias-1", given="Ada", surname="Byron"),
            OtherName(id="alias-2", business_name="Ada's Analytical Engines"),
        ),
        employer_ids=("12-3456789",),
        residence_address=Address(
            line1="12 Byron Court",
            line2="Apt 4",
            city="Tampa",
            state="FL",
            postal_code="33601",
            county="Hillsborough",
        ),
        mailing_address=Address(
            line1="4501 Postal Way",
            line2="PO Box 99",
            city="Tampa",
            state="FL",
            postal_code="33602",
        ),
        phone="(813) 555-0101",
        mobile="(813) 555-0102",
        email="ada@example.com",
        venue=Venue(basis="lived_longest_180_days"),
        credit_counseling=CreditCounseling(status="completed_with_certificate"),
        signed_at="2026-08-30",
    )


def _debtor_2() -> Debtor:
    return Debtor(
        id="debtor-0002",
        case_id=REFERENCE_CASE.id,
        filing_role="debtor_2",
        created_at="2026-08-01T12:05:00Z",
        updated_at="2026-08-01T12:05:00Z",
        name=PersonName(given="Ben", surname="Lovelace", suffix="Jr."),
        residence_address=Address(
            line1="12 Byron Court",
            line2="Apt 4",
            city="Tampa",
            state="FL",
            postal_code="33601",
            county="Hillsborough",
        ),
        venue=Venue(
            basis="other",
            explanation=(
                "Moved from Georgia in July; the greater part of the last 180 "
                "days was spent in this district."
            ),
        ),
        credit_counseling=CreditCounseling(
            status="not_required", exemption_reason="disability"
        ),
        signed_at="2026-08-30",
    )


def _assets() -> tuple[tuple[str, AssetBody], ...]:
    """The reference estate, in creation (= printed row) order: the
    homestead, two vehicles, Part 3-4 personal property, and the sole
    proprietorship's business property."""
    return (
        (
            "asset-house",
            AssetBody(
                category="real_property",
                property_types=("single_family_home",),
                description="12 Byron Court, Tampa, FL 33601",
                county="Hillsborough",
                value_entire="265000.00",
                value_portion_owned="240000.00",
                ownership_interest="both",
                ownership_interest_description="Fee simple, tenancy by the entireties",
                community_property=False,
                detail="Two-bedroom bungalow; the family homestead",
            ),
        ),
        (
            "asset-civic",
            AssetBody(
                category="vehicle",
                description="2016 Honda Civic LX",
                detail="Approx. 92,000 miles",
                value_entire="9000.00",
                value_portion_owned="9000.00",
                ownership_interest="debtor_1",
            ),
        ),
        (
            "asset-pontoon",
            AssetBody(
                category="watercraft_aircraft_or_recreational_vehicle",
                description="2005 Sun Tracker pontoon boat",
                detail="Trailer included; engine needs work",
                value_entire="3500.00",
                value_portion_owned="3500.00",
                ownership_interest="both",
            ),
        ),
        (
            "asset-household",
            AssetBody(
                category="household_goods",
                description="Used furniture and kitchen appliances",
                value_entire="2200.00",
                value_portion_owned="2200.00",
                ownership_interest="both",
            ),
        ),
        (
            "asset-electronics",
            AssetBody(
                category="electronics",
                description="Two laptops, one television",
                value_entire="900.00",
                value_portion_owned="900.00",
                ownership_interest="both",
            ),
        ),
        (
            "asset-clothes",
            AssetBody(
                category="clothes",
                description="Everyday clothing",
                value_entire="400.00",
                value_portion_owned="400.00",
                ownership_interest="both",
            ),
        ),
        (
            "asset-rings",
            AssetBody(
                category="jewelry",
                description="Wedding rings",
                value_entire="1800.00",
                value_portion_owned="1800.00",
                ownership_interest="both",
            ),
        ),
        (
            "asset-cash",
            AssetBody(
                category="cash",
                description="Cash on hand",
                value_portion_owned="150.00",
                ownership_interest="both",
            ),
        ),
        (
            "asset-checking",
            AssetBody(
                category="deposits_of_money",
                detail="Checking account, Suncoast Credit Union",
                value_portion_owned="1250.00",
                ownership_interest="both",
            ),
        ),
        (
            "asset-biz-checking",
            AssetBody(
                category="deposits_of_money",
                detail="Business checking account, Wells Fargo",
                value_portion_owned="2100.00",
                ownership_interest="debtor_1",
            ),
        ),
        (
            "asset-401k",
            AssetBody(
                category="retirement_accounts",
                description="401(k) retirement account",
                detail="401(k), Fidelity — Menabrea Machines Inc",
                value_portion_owned="48000.00",
                ownership_interest="debtor_1",
            ),
        ),
        (
            "asset-utility-deposit",
            AssetBody(
                category="security_deposits_and_prepayments",
                detail="Utility deposit, Tampa Electric",
                value_portion_owned="200.00",
                ownership_interest="both",
            ),
        ),
        (
            "asset-tax-refund",
            AssetBody(
                category="money_owed_to_you",
                description="2025 federal income tax refund, return filed",
                detail="Federal",
                value_portion_owned="1100.00",
                ownership_interest="both",
            ),
        ),
        (
            "asset-term-life",
            AssetBody(
                category="insurance_policy_interests",
                description="Term life policy, Prudential",
                detail="Beneficiary: Ben Lovelace",
                value_portion_owned="0.00",
                ownership_interest="debtor_1",
            ),
        ),
        (
            "asset-workbench",
            AssetBody(
                category="office_equipment",
                description="Workbench, test rigs, and hand tools",
                value_portion_owned="750.00",
                ownership_interest="debtor_1",
            ),
        ),
        (
            "asset-parts",
            AssetBody(
                category="inventory",
                description="Replacement engine parts inventory",
                value_portion_owned="1200.00",
                ownership_interest="debtor_1",
            ),
        ),
    )


def _exemptions() -> tuple[ExemptionBody, ...]:
    """Florida claims (the reference case's state, opted out of § 522(d)):
    the unlimited homestead as a full-FMV election over the § 522(q) cap,
    two dollar-amount claims, and the exempt 401(k)."""
    return (
        ExemptionBody(
            asset_id="asset-house",
            statute_citation="Fla. Const. art. X, § 4(a)(1)",
            claims_full_fmv=True,
            acquired_within_1215_days=False,
        ),
        ExemptionBody(
            asset_id="asset-civic",
            statute_citation="Fla. Stat. § 222.25(1)",
            amount="5000.00",
            claims_full_fmv=False,
        ),
        ExemptionBody(
            asset_id="asset-household",
            statute_citation="Fla. Const. art. X, § 4(a)(2)",
            amount="1000.00",
            claims_full_fmv=False,
        ),
        ExemptionBody(
            asset_id="asset-401k",
            statute_citation="Fla. Stat. § 222.21(2)",
            claims_full_fmv=True,
        ),
    )


def _creditors() -> tuple[tuple[str, CreditorBody], ...]:
    def creditor(id_: str, name: str, line1: str, city: str, state: str, postal: str):
        return (
            id_,
            CreditorBody(
                name=name,
                address=Address(
                    line1=line1, city=city, state=state, postal_code=postal
                ),
            ),
        )

    return (
        creditor(
            "cred-mortgage",
            "Gulf Coast Home Loans",
            "100 Lender Way",
            "Tampa",
            "FL",
            "33602",
        ),
        creditor(
            "cred-auto",
            "Drive Away Financial LLC",
            "88 Motor Row",
            "Orlando",
            "FL",
            "32801",
        ),
        creditor(
            "cred-irs",
            "Internal Revenue Service",
            "PO Box 7346",
            "Philadelphia",
            "PA",
            "19101",
        ),
        creditor(
            "cred-visa",
            "Meridian Bank Card Services",
            "1 Meridian Plaza",
            "Wilmington",
            "DE",
            "19801",
        ),
        creditor(
            "cred-hospital",
            "Bayside General Hospital",
            "2 Care Circle",
            "Tampa",
            "FL",
            "33606",
        ),
        creditor(
            "cred-student",
            "Great Plains Student Servicing",
            "500 Loan Loop",
            "Lincoln",
            "NE",
            "68501",
        ),
    )


def _claims() -> tuple[tuple[str, ClaimBody], ...]:
    """Two secured, one priority, three nonpriority — every class, every
    printed flag column somewhere, and one notice party per notify part."""
    return (
        (
            "claim-mortgage",
            ClaimBody(
                creditor_id="cred-mortgage",
                claim_class="secured",
                account_last4="3321",
                date_incurred="2019-06-01",
                amount="195000.00",
                contingent=False,
                unliquidated=False,
                disputed=False,
                who_incurred="both",
                collateral_description="12 Byron Court, Tampa, FL 33601",
                collateral_value="240000.00",
                lien_nature=("agreement",),
                notice_parties=(
                    NoticeParty(
                        id="np-mortgage-servicer",
                        name="Gulf Coast Loan Servicing",
                        address=Address(
                            line1="101 Lender Way",
                            line2="Suite 4",
                            city="Tampa",
                            state="FL",
                            postal_code="33602",
                        ),
                        account_last4="3321",
                    ),
                ),
            ),
        ),
        (
            "claim-auto",
            ClaimBody(
                creditor_id="cred-auto",
                claim_class="secured",
                account_last4="8890",
                date_incurred="2023-04-15",
                amount="7400.00",
                who_incurred="debtor_1",
                collateral_description="2016 Honda Civic LX",
                collateral_value="9000.00",
                lien_nature=("agreement",),
            ),
        ),
        (
            "claim-irs",
            ClaimBody(
                creditor_id="cred-irs",
                claim_class="priority_unsecured",
                date_incurred="2025-04-15",
                priority_amount="3200.00",
                nonpriority_amount="450.00",
                priority_type="tax_and_government",
                who_incurred="both",
                subject_to_offset=False,
            ),
        ),
        (
            "claim-visa",
            ClaimBody(
                creditor_id="cred-visa",
                claim_class="nonpriority_unsecured",
                account_last4="4412",
                date_incurred="2020-11-01",
                amount="8200.00",
                nonpriority_type="other",
                nonpriority_type_other="Credit card purchases",
                who_incurred="both",
                subject_to_offset=False,
                notice_parties=(
                    NoticeParty(
                        id="np-visa-collector",
                        name="Meridian Recovery Services",
                        address=Address(
                            line1="9 Collection Court",
                            city="Wilmington",
                            state="DE",
                            postal_code="19801",
                        ),
                        account_last4="4412",
                    ),
                ),
            ),
        ),
        (
            "claim-hospital",
            ClaimBody(
                creditor_id="cred-hospital",
                claim_class="nonpriority_unsecured",
                account_last4="0072",
                date_incurred="2026-02-10",
                amount="2600.00",
                nonpriority_type="other",
                nonpriority_type_other="Medical services",
                who_incurred="debtor_2",
                disputed=True,
                subject_to_offset=True,
            ),
        ),
        (
            "claim-student",
            ClaimBody(
                creditor_id="cred-student",
                claim_class="nonpriority_unsecured",
                account_last4="5510",
                date_incurred="2012-09-01",
                amount="12000.00",
                nonpriority_type="student_loan",
                who_incurred="debtor_1",
                subject_to_offset=False,
            ),
        ),
    )


def _contract_leases() -> tuple[tuple[str, ContractLeaseBody], ...]:
    return (
        (
            "cl-storage",
            ContractLeaseBody(
                counterparty_name="StorSafe Tampa LLC",
                counterparty_address=Address(
                    line1="77 Keeper Street",
                    city="Tampa",
                    state="FL",
                    postal_code="33605",
                ),
                description=(
                    "Month-to-month storage unit lease, unit 214; debtor's "
                    "interest: lessee"
                ),
            ),
        ),
        (
            "cl-wireless",
            ContractLeaseBody(
                counterparty_name="Gulf Wireless LLC",
                counterparty_address=Address(
                    line1="300 Signal Drive",
                    city="Tampa",
                    state="FL",
                    postal_code="33607",
                ),
                description="Two-year wireless service agreement, 14 months remaining",
            ),
        ),
    )


def _codebtors() -> tuple[CodebtorBody, ...]:
    return (
        CodebtorBody(
            name="Margaret Lovelace",
            address=Address(
                line1="9 Garden Lane", city="Tampa", state="FL", postal_code="33603"
            ),
            claim_ids=("claim-auto",),
        ),
        CodebtorBody(
            name="Charles Menabrea",
            address=Address(
                line1="41 Engine Row", city="Tampa", state="FL", postal_code="33605"
            ),
            claim_ids=("claim-visa",),
            contract_lease_ids=("cl-storage",),
        ),
    )


def _households() -> tuple[tuple[str, HouseholdBody], ...]:
    return (
        (
            "hh-main",
            HouseholdBody(
                which_household="main",
                separate_household=False,
                expenses_include_others=True,
                change_expected=True,
                change_explanation="Health insurance premiums increase in January.",
            ),
        ),
    )


def _expenses() -> tuple[ExpenseBody, ...]:
    """The main household's monthly expense rows — one per 106J line the
    family actually has, totalling 5,060.00."""

    def expense(category: str, monthly: str, specify: str | None = None) -> ExpenseBody:
        return ExpenseBody(
            household_id="hh-main",
            category=category,
            amount=monthly,
            specify_text=specify,
        )

    return (
        expense("rent_or_home_ownership", "1480.00"),
        expense("home_maintenance", "120.00"),
        expense("electricity_heat_gas", "210.00"),
        expense("water_sewer_garbage", "95.00"),
        expense("telephone_and_internet", "140.00"),
        expense("food_and_housekeeping", "850.00"),
        expense("childcare_and_education", "300.00"),
        expense("clothing_and_laundry", "120.00"),
        expense("personal_care", "60.00"),
        expense("medical_and_dental", "180.00"),
        expense("transportation", "240.00"),
        expense("entertainment_and_recreation", "90.00"),
        expense("charitable_contributions", "50.00"),
        expense("life_insurance", "45.00"),
        expense("health_insurance", "380.00"),
        expense("vehicle_insurance", "165.00"),
        expense("taxes", "150.00", "Self-employment tax estimate"),
        expense("vehicle_installment_payments", "260.00"),
        expense("other_installment_payments", "55.00", "Financed laptop"),
        expense("other", "70.00", "Dog food and veterinary care"),
    )


def _dependents() -> tuple[DependentBody, ...]:
    return (
        DependentBody(
            household_id="hh-main",
            relationship="Daughter",
            age=9,
            lives_with_debtor=True,
        ),
    )


def _sofa_entries() -> tuple[SofaEntryBody, ...]:
    """The reference SOFA, coherent with the rest of the case: the mortgage
    payments name the mortgage creditor, the insider is the car loan's
    cosigner, the storage unit is Schedule G's lease, and the consultant is
    the attorney."""

    def entry(entry_type: str, payload: object) -> SofaEntryBody:
        return SofaEntryBody(entry_type=entry_type, payload=payload)  # type: ignore[arg-type]

    return (
        entry("marital_status", MaritalStatus(status="married")),
        entry(
            "prior_address",
            PriorAddress(
                which_debtor="debtor_1",
                address=Address(
                    line1="3 Difference Court",
                    city="Tampa",
                    state="FL",
                    postal_code="33604",
                ),
                from_date="2023-01-01",
                to_date="2024-06-30",
            ),
        ),
        entry(
            "prior_address",
            PriorAddress(
                which_debtor="debtor_2",
                address=Address(
                    line1="8 Peachtree Lane",
                    city="Decatur",
                    state="GA",
                    postal_code="30030",
                ),
                from_date="2024-11-01",
                to_date="2026-07-01",
            ),
        ),
        entry(
            "income_by_period",
            IncomeByPeriod(
                which_debtor="debtor_1",
                kind="wages_and_commissions",
                period_start="2026-01-01",
                period_end="2026-08-01",
                gross_amount="41600.00",
            ),
        ),
        entry(
            "income_by_period",
            IncomeByPeriod(
                which_debtor="debtor_1",
                kind="wages_and_commissions",
                period_start="2025-01-01",
                period_end="2025-12-31",
                gross_amount="61000.00",
            ),
        ),
        entry(
            "income_by_period",
            IncomeByPeriod(
                which_debtor="debtor_1",
                kind="operating_a_business",
                description="Ada's Analytical Engines",
                period_start="2025-01-01",
                period_end="2025-12-31",
                gross_amount="4200.00",
            ),
        ),
        entry(
            "income_by_period",
            IncomeByPeriod(
                which_debtor="debtor_1",
                kind="wages_and_commissions",
                period_start="2024-01-01",
                period_end="2024-12-31",
                gross_amount="58300.00",
            ),
        ),
        entry(
            "income_by_period",
            IncomeByPeriod(
                which_debtor="debtor_2",
                kind="wages_and_commissions",
                period_start="2026-01-01",
                period_end="2026-08-01",
                gross_amount="24000.00",
            ),
        ),
        entry(
            "income_by_period",
            IncomeByPeriod(
                which_debtor="debtor_2",
                kind="wages_and_commissions",
                period_start="2025-01-01",
                period_end="2025-12-31",
                gross_amount="35000.00",
            ),
        ),
        entry(
            "income_by_period",
            IncomeByPeriod(
                which_debtor="debtor_1",
                kind="other",
                description="Unemployment compensation",
                period_start="2024-03-01",
                period_end="2024-08-31",
                gross_amount="3100.00",
            ),
        ),
        entry(
            "creditor_payment",
            CreditorPayment(
                creditor=Party(
                    name="Gulf Coast Home Loans",
                    address=Address(
                        line1="100 Lender Way",
                        city="Tampa",
                        state="FL",
                        postal_code="33602",
                    ),
                ),
                dates=("2026-05-01", "2026-06-01", "2026-07-01"),
                total_paid="4440.00",
                amount_still_owed="195000.00",
                payment_for=("mortgage",),
            ),
        ),
        entry(
            "insider_payment",
            InsiderPayment(
                insider=Party(
                    name="Margaret Lovelace",
                    address=Address(
                        line1="9 Garden Lane",
                        city="Tampa",
                        state="FL",
                        postal_code="33603",
                    ),
                ),
                relationship="Mother of Debtor 2",
                dates=("2025-12-20",),
                total_paid="1200.00",
                amount_still_owed="0.00",
                reason="Repayment of a family loan",
            ),
        ),
        entry(
            "lawsuit",
            Lawsuit(
                case_title="Meridian Bank Card Services v. Lovelace",
                case_number="26-CC-1184",
                nature_of_case="Credit card collection",
                court=Party(
                    name="Hillsborough County Court",
                    address=Address(
                        line1="800 E Twiggs Street",
                        city="Tampa",
                        state="FL",
                        postal_code="33602",
                    ),
                ),
                status="pending",
            ),
        ),
        entry(
            "gift",
            Gift(
                recipient=Party(
                    name="Clara Byron",
                    address=Address(
                        line1="15 Loom Lane",
                        city="Atlanta",
                        state="GA",
                        postal_code="30301",
                    ),
                ),
                relationship="Niece",
                description="Wedding gift",
                dates=("2025-10-12",),
                value="650.00",
            ),
        ),
        entry(
            "charitable_contribution",
            CharitableContribution(
                organization=Party(
                    name="Tampa Bay Food Bank",
                    address=Address(
                        line1="5 Harvest Road",
                        city="Tampa",
                        state="FL",
                        postal_code="33610",
                    ),
                ),
                description="Cash donations",
                dates=("2025-11-30",),
                value="700.00",
            ),
        ),
        entry(
            "loss",
            Loss(
                description="Hurricane damage to the back fence",
                insurance_coverage="Not covered; below the deductible",
                date="2025-09-28",
                value="1400.00",
            ),
        ),
        entry(
            "consultant_payment",
            ConsultantPayment(
                person=Party(
                    name="Counsel & Counsel PA",
                    address=Address(
                        line1="1 Example Way",
                        city="Tampa",
                        state="FL",
                        postal_code="33604",
                    ),
                ),
                email_or_website="alex@example.com",
                description="Attorney fees for this bankruptcy case",
                date="2026-07-15",
                amount="1500.00",
            ),
        ),
        entry(
            "property_transfer",
            PropertyTransfer(
                transferee=Party(
                    name="Charles Menabrea",
                    address=Address(
                        line1="41 Engine Row",
                        city="Tampa",
                        state="FL",
                        postal_code="33605",
                    ),
                ),
                relationship="Friend",
                description="Sold a spare engine lathe valued at about $800",
                value_received="$500 cash",
                date="2025-03-15",
            ),
        ),
        entry(
            "closed_account",
            ClosedAccount(
                institution=Party(
                    name="First Gulf Bank",
                    address=Address(
                        line1="12 Bay Street",
                        city="Tampa",
                        state="FL",
                        postal_code="33601",
                    ),
                ),
                account_last4="2210",
                account_type="checking",
                date_closed="2026-01-15",
                last_balance="25.00",
            ),
        ),
        entry(
            "storage_unit",
            StorageUnit(
                facility=Party(
                    name="StorSafe Tampa LLC",
                    address=Address(
                        line1="77 Keeper Street",
                        city="Tampa",
                        state="FL",
                        postal_code="33605",
                    ),
                ),
                who_has_access=("Ada Lovelace",),
                description="Business records and spare parts",
                still_have=True,
            ),
        ),
        entry(
            "held_for_another",
            HeldForAnother(
                owner=Party(
                    name="Menabrea Machines Inc",
                    address=Address(
                        line1="200 Engine Row",
                        city="Tampa",
                        state="FL",
                        postal_code="33605",
                    ),
                ),
                location="Workshop shelf at 12 Byron Court",
                description="Loaned test equipment",
                value="900.00",
            ),
        ),
        entry(
            "business_connection",
            BusinessConnection(
                business=Party(
                    name="Ada's Analytical Engines",
                    address=Address(
                        line1="88 Difference Drive",
                        city="Tampa",
                        state="FL",
                        postal_code="33603",
                    ),
                ),
                nature_of_business="Repair and resale of analytical engines",
                ein="12-3456789",
                from_date="2019-01-01",
                connection=("sole_proprietor",),
            ),
        ),
        entry(
            "financial_statement_issued",
            FinancialStatementIssued(
                recipient=Party(
                    name="Suncoast Credit Union",
                    address=Address(
                        line1="6801 Croom Road",
                        city="Tampa",
                        state="FL",
                        postal_code="33607",
                    ),
                ),
                date_issued="2025-06-01",
            ),
        ),
    )


def reference_case_file() -> CaseFile:
    return CaseFile(
        case=REFERENCE_CASE,
        debtors=(_debtor_1(), _debtor_2()),
        petition=PetitionBody(
            fee_handling="installments",
            # The family owns its homestead (Schedule A/B row one), so B101
            # line 11 answers No and the eviction follow-up never prints.
            rents_residence=False,
            eviction_judgment_against_you=False,
            small_business_status="not_filing_under_chapter_11",
            hazardous_property=HazardousProperty(
                description="Two corroding propane tanks behind the shed",
                why_immediate="A slow leak was found in August",
                address=Address(
                    line1="12 Byron Court",
                    city="Tampa",
                    state="FL",
                    postal_code="33601",
                ),
            ),
            debt_character="consumer",
            ch7_funds_available_for_creditors=False,
            estimated_creditors="1_49",
            # Deliberately the bracket whose printed export on line 19 is
            # missing a digit — the projection must pick it by position.
            estimated_assets="100000001_500000000",
            estimated_liabilities="50001_100000",
        ),
        prior_cases=(
            PriorCaseBody(
                district="Northern District of Georgia",
                filed_on="2019-03-04",
                case_number="19-01234",
            ),
        ),
        related_cases=(
            RelatedCaseBody(
                debtor_name="Analytical Engines LLC",
                relationship="Affiliate",
                district="Middle District of Florida",
                filed_on="2026-06-15",
                case_number="26-00042",
            ),
        ),
        sole_proprietorships=(
            SoleProprietorshipBody(
                name="Ada's Analytical Engines",
                address=Address(
                    line1="88 Difference Drive",
                    city="Tampa",
                    state="FL",
                    postal_code="33603",
                ),
                business_type="none_of_the_above",
            ),
        ),
        filing_professionals=(
            FilingProfessionalBody(
                role="attorney",
                name=PersonName(given="Alex", surname="Counsel"),
                firm_name="Counsel & Counsel PA",
                address=Address(
                    line1="1 Example Way",
                    city="Tampa",
                    state="FL",
                    postal_code="33604",
                ),
                phone="(813) 555-0100",
                email="alex@example.com",
                bar_number="112233",
                bar_state="FL",
                signature_date="2026-08-31",
            ),
        ),
        employments=(
            EmploymentBody(
                debtor_id="debtor-0001",
                status="employed",
                occupation="Systems analyst",
                employer_name="Menabrea Machines Inc",
                employer_address=Address(
                    line1="200 Engine Row",
                    city="Tampa",
                    state="FL",
                    postal_code="33605",
                ),
                employed_since="2019-02-14",
            ),
            EmploymentBody(
                debtor_id="debtor-0002",
                status="not_employed",
            ),
        ),
        assets=_assets(),
        exemptions=_exemptions(),
        creditors=_creditors(),
        claims=_claims(),
        contract_leases=_contract_leases(),
        codebtors=_codebtors(),
        households=_households(),
        expenses=_expenses(),
        dependents=_dependents(),
        sofa_entries=_sofa_entries(),
        income_summaries=(
            IncomeSummaryBody(
                debtor_id="debtor-0001",
                wages="5200.00",
                overtime="250.00",
                deduction_tax="830.00",
                deduction_insurance="120.50",
                family_support="400.00",
                household_contributions="250.00",
                household_contributions_specify="Adult son shares rent",
                change_expected=True,
                change_explanation="Overtime ends in November.",
            ),
            IncomeSummaryBody(
                debtor_id="debtor-0002",
                wages="3000.00",
            ),
        ),
    )


# --- the projected goldens ----------------------------------------------------


@pytest.mark.parametrize(
    "series",
    [
        "form/b101",
        "form/b106ab",
        "form/b106c",
        "form/b106d",
        "form/b106ef",
        "form/b106dec",
        "form/b106g",
        "form/b106h",
        "form/b106i",
        "form/b106j",
        "form/b106j2",
        "form/b106sum",
        "form/b107",
    ],
)
def test_reference_case_renders_to_its_golden(series: str) -> None:
    release = latest_form(series)
    data = fill_form(release, project(release, reference_case_file()))
    observed = {
        "release": release.release_id,
        "sha256": hashlib.sha256(data).hexdigest(),
        "fields": read_form(release, data),
    }
    path = GOLDEN_DIR / f"{release.form}_case.json"
    if os.environ.get("UPDATE_FORM_GOLDENS") == "1":  # pragma: no cover
        path.write_text(
            json.dumps(observed, indent=1, sort_keys=True) + "\n", encoding="utf-8"
        )
    golden = json.loads(path.read_text(encoding="utf-8"))
    assert golden["release"] == release.release_id
    assert observed["fields"] == golden["fields"]
    assert observed["sha256"] == golden["sha256"]


# --- the mappings, stated as assertions ---------------------------------------


def b101_values() -> dict[str, object]:
    release = latest_form("form/b101")
    return dict(project(release, reference_case_file()))


def test_line_15_uses_the_verified_exports() -> None:
    values = b101_values()
    assert values["line_15_debtor1_credit_counseling"] == Option("1")
    assert values["line_15_debtor2_credit_counseling"] == Option("4")
    assert values["line_15_debtor2_exemption_reason"] == Option("Disability")


def test_line_16_translates_the_three_way_into_two_gates() -> None:
    values = b101_values()
    assert values["line_16a_consumer_debts"] == Option("Yes")
    # A consumer answer never reaches the business gate or the 16c text.
    assert "line_16b_business_debts" not in values
    assert "line_16c_other_debts" not in values


def test_line_19_picks_the_misprinted_band_by_position() -> None:
    values = b101_values()
    # The stored band is 100000001_500000000; line 19's printed export for
    # that bracket is missing the +1 digit ('100000000-500000000', sic) and
    # line 20's is not — position, not spelling, is the identity.
    assert values["line_19_estimated_assets"] == Option("100000000-500000000")
    assert values["line_20_estimated_liabilities"] == Option("50001-100000")


def test_line_17_answers_from_the_chapter() -> None:
    values = b101_values()
    assert values["line_17_filing_under_ch7"] == Option("Yes")
    assert values["line_17_funds_available"] == Option("No")


def test_the_attorney_block_wins_over_the_pro_se_block() -> None:
    values = b101_values()
    assert values["attorney.printed_name"] == Text("Alex Counsel")
    assert values["attorney.date_signed"] == Text("08/31/2026")
    assert not any(key.startswith("prose.") for key in values)


def test_without_an_attorney_the_pro_se_block_carries_contacts() -> None:
    release = latest_form("form/b101")
    case_file = reference_case_file()
    values = project(
        release,
        CaseFile(**{**case_file.__dict__, "filing_professionals": ()}),
    )
    assert values["prose.paid_preparer"] == Option("no")
    assert values["prose.debtor1_phone"] == Text("(813) 555-0101")
    assert values["prose.debtor1_date"] == Text("08/30/2026")
    assert not any(key.startswith("attorney.") for key in values)


def test_b106i_derives_the_arithmetic_lines() -> None:
    release = latest_form("form/b106i")
    values = project(release, reference_case_file())

    def column(field_id: str, digit: str) -> object:
        entry = values[field_id]
        assert isinstance(entry, dict)
        name = next(
            n for n in entry if f"Debtor {digit}" in n or f"debtor {digit}" in n
        )
        return entry[name]

    # Debtor 1: 5,200 + 250 gross; 830 + 120.50 deducted; 400 other income.
    assert column("line_4_gross_income", "1") == Text("5,450.00")
    assert column("line_6_total_deductions", "1") == Text("950.50")
    assert column("line_7_take_home_pay", "1") == Text("4,499.50")
    assert column("line_9_total_other_income", "1") == Text("400.00")
    assert column("line_10_monthly_income", "1") == Text("4,899.50")
    # Debtor 2: wages only.
    assert column("line_10_monthly_income", "2") == Text("3,000.00")
    # Combined; line 11 household contributions; line 12 = 10 + 11.
    assert values["line_10_combined"] == Text("7,899.50")
    assert values["line_11_household_contributions"] == Text("250.00")
    assert values["line_12_combined_monthly_income"] == Text("8,149.50")
    # The page-2 copy row repeats line 4 exactly.
    assert column("line_4_copy", "1") == Text("5,450.00")


def test_b106i_second_column_takes_a_non_filing_spouse() -> None:
    release = latest_form("form/b106i")
    case_file = reference_case_file()
    spouse = Debtor(**{**_debtor_2().__dict__, "filing_role": "non_filing_spouse"})
    values = project(
        release,
        CaseFile(**{**case_file.__dict__, "debtors": (_debtor_1(), spouse)}),
    )
    assert values["caption.debtor2_name"] == Text("Ben Lovelace Jr.")


def test_facts_that_do_not_fit_the_printed_rows_are_errors() -> None:
    release = latest_form("form/b101")
    case_file = reference_case_file()
    debtor = _debtor_1()
    crowded = Debtor(
        **{
            **debtor.__dict__,
            "other_names_used": (
                OtherName(id="a1", surname="Byron"),
                OtherName(id="a2", surname="King"),
                OtherName(id="a3", surname="Noel"),
            ),
        }
    )
    with pytest.raises(FormProjectionError, match="prints 2 rows"):
        project(release, CaseFile(**{**case_file.__dict__, "debtors": (crowded,)}))

    doubled = CaseFile(
        **{
            **case_file.__dict__,
            "sole_proprietorships": case_file.sole_proprietorships * 2,
        }
    )
    with pytest.raises(FormProjectionError, match="one sole-proprietorship block"):
        project(release, doubled)


def test_an_unmapped_revision_is_refused() -> None:
    release = get_form("form/b101", "form/b101@2024-06-22")
    import dataclasses

    unknown = dataclasses.replace(
        release, effective_date=release.effective_date.replace(year=2030)
    )
    with pytest.raises(KeyError, match="no projection is written"):
        project(unknown, reference_case_file())


# --- B106A/B ------------------------------------------------------------------


def row(values: dict[str, object], release: object, field_id: str, index: int):
    """One printed row's fill for a repeated field, by the spec's row order."""
    spec = release.field(field_id)  # type: ignore[attr-defined]
    entry = values[field_id]
    if len(spec.pdf_names) == 1:
        assert index == 0
        return entry
    assert isinstance(entry, dict)
    return entry[spec.pdf_names[index]]


def b106ab_values() -> dict[str, object]:
    release = latest_form("form/b106ab")
    return dict(project(release, reference_case_file()))


def test_b106ab_derives_the_part_totals_and_the_rollup() -> None:
    values = b106ab_values()
    assert values["line_2_part1_total"] == Text("240,000.00")
    assert values["line_5_part2_total"] == Text("12,500.00")
    assert values["line_15_part3_total"] == Text("5,300.00")
    assert values["line_36_part4_total"] == Text("52,800.00")
    assert values["line_45_part5_total"] == Text("1,950.00")
    assert values["line_52_part6_total"] == Text("0.00")
    # Part 8: 55 copies Part 1, 62 sums Parts 2-7, 63 = 55 + 62.
    assert values["line_55_total"] == Text("240,000.00")
    assert values["line_62_total_personal_property"] == Text("72,550.00")
    assert values["line_63_total_all_property"] == Text("312,550.00")


def test_b106ab_lands_the_homestead_on_row_one() -> None:
    release = latest_form("form/b106ab")
    values = dict(project(release, reference_case_file()))
    assert row(values, release, "real_estate.street", 0) == Text(
        "12 Byron Court, Tampa, FL 33601"
    )
    assert row(values, release, "real_estate.county", 0) == Text("Hillsborough")
    assert row(values, release, "real_estate.value_portion", 0) == Text("240,000.00")
    assert row(values, release, "real_estate.who_has_interest", 0) == Option(
        "Debtor 1 and 2"
    )


def test_b106ab_vehicle_free_text_lands_in_other_information() -> None:
    # The spec maps make/model/year/mileage all to the one free-text
    # `detail`, which cannot be split back apart — the whole text lands in
    # the row's Other information box and the four sub-boxes stay blank.
    release = latest_form("form/b106ab")
    values = dict(project(release, reference_case_file()))
    assert row(values, release, "vehicle.other_information", 0) == Text(
        "2016 Honda Civic LX; Approx. 92,000 miles"
    )
    assert "vehicle.make" not in values
    assert row(values, release, "vehicle.who_has_interest", 0) == Option("Debtor 1")


def test_b106ab_single_box_lines_aggregate_their_category() -> None:
    values = b106ab_values()
    assert values["line_6_gate"] == Option("yes")
    assert values["line_6_description"] == Text("Used furniture and kitchen appliances")
    assert values["line_6_amount"] == Text("2,200.00")
    # An empty category answers its gate No and prints nothing else.
    assert values["line_8_gate"] == Option("no")
    assert "line_8_amount" not in values


def test_b106ab_deposits_take_one_printed_row_each() -> None:
    release = latest_form("form/b106ab")
    values = dict(project(release, reference_case_file()))
    assert row(values, release, "line_17_institution", 0) == Text(
        "Checking account, Suncoast Credit Union"
    )
    assert row(values, release, "line_17_amount", 1) == Text("2,100.00")


def test_b106ab_routes_line_28_amounts_by_detail_keyword() -> None:
    values = b106ab_values()
    # The quirky gate: line 28's yes box exports 'On'.
    assert values["line_28_gate"] == Option("On")
    assert values["line_28_amount_federal"] == Text("1,100.00")
    assert "line_28_amount_state" not in values

    case_file = reference_case_file()
    unroutable = (
        (
            "asset-mystery",
            AssetBody(
                category="money_owed_to_you",
                description="A refund of some kind",
                detail="unspecified",
                value_portion_owned="10.00",
            ),
        ),
    )
    with pytest.raises(FormProjectionError, match="names none of them"):
        project(
            latest_form("form/b106ab"),
            CaseFile(**{**case_file.__dict__, "assets": case_file.assets + unroutable}),
        )


def test_b106ab_overflow_past_the_printed_rows_is_an_error() -> None:
    case_file = reference_case_file()
    extra = tuple(
        (
            f"asset-lot-{n}",
            AssetBody(
                category="real_property",
                description=f"Vacant lot {n}",
                value_portion_owned="1000.00",
            ),
        )
        for n in range(3)
    )
    with pytest.raises(FormProjectionError, match="prints 3 rows"):
        project(
            latest_form("form/b106ab"),
            CaseFile(**{**case_file.__dict__, "assets": case_file.assets + extra}),
        )


# --- B106C --------------------------------------------------------------------


def b106c_values() -> dict[str, object]:
    release = latest_form("form/b106c")
    return dict(project(release, reference_case_file()))


def test_b106c_line_1_is_forced_by_the_opt_out_rule() -> None:
    # Florida bars the § 522(d) election, so § 522(b)(3) is the only box
    # the law allows — derived from the exemptions registry, not stored.
    values = b106c_values()
    assert values["line_1_exemption_set"] == Option("state and federal")


def test_b106c_line_1_stays_blank_where_the_debtor_may_elect() -> None:
    # Texas allows the federal election; the choice is the debtor's own
    # fact (case.exemption_set), which code has not grown yet — blank.
    case_file = reference_case_file()
    debtor = _debtor_1()
    texan = Debtor(
        **{
            **debtor.__dict__,
            "residence_address": Address(
                line1="1 Alamo Plaza",
                city="San Antonio",
                state="TX",
                postal_code="78205",
            ),
        }
    )
    values = project(
        latest_form("form/b106c"),
        CaseFile(**{**case_file.__dict__, "debtors": (texan, _debtor_2())}),
    )
    assert "line_1_exemption_set" not in values


def test_b106c_rows_copy_the_asset_and_spell_the_election() -> None:
    release = latest_form("form/b106c")
    values = dict(project(release, reference_case_file()))
    # Row 1: the homestead, a 100%-of-FMV election — no dollar box.
    assert row(values, release, "line_2_property_description", 0) == Text(
        "12 Byron Court, Tampa, FL 33601"
    )
    assert row(values, release, "line_2_current_value", 0) == Text("240,000.00")
    assert row(values, release, "line_2_exemption_kind", 0) == Option("fair market")
    with pytest.raises(KeyError):
        row(values, release, "line_2_exemption_amount", 0)
    # Row 2: the vehicle, a specific dollar amount.
    assert row(values, release, "line_2_exemption_kind", 1) == Option("On")
    assert row(values, release, "line_2_exemption_amount", 1) == Text("5,000.00")
    assert row(values, release, "line_2_statute_citation", 1) == Text(
        "Fla. Stat. § 222.25(1)"
    )


def test_b106c_answers_the_homestead_cap_from_the_registry() -> None:
    # The full-FMV homestead counts at the asset's portion-owned value:
    # 240,000 > the registry's § 522(q) cap (214,000 on 04/25) -> Yes, and
    # the 1,215-day follow-up prints the exemption's own stored answer.
    values = b106c_values()
    assert values["line_3_homestead_over_cap"] == Option("yes")
    assert values["line_3_acquired_within_1215_days"] == Option("no")


def test_b106c_under_cap_answers_no_and_skips_the_follow_up() -> None:
    case_file = reference_case_file()
    modest = tuple(
        (id_, AssetBody(**{**body.__dict__, "value_portion_owned": "180000.00"}))
        if id_ == "asset-house"
        else (id_, body)
        for id_, body in case_file.assets
    )
    values = project(
        latest_form("form/b106c"),
        CaseFile(**{**case_file.__dict__, "assets": modest}),
    )
    assert values["line_3_homestead_over_cap"] == Option("no")
    assert "line_3_acquired_within_1215_days" not in values


def test_b106c_overflow_past_fifteen_rows_is_an_error() -> None:
    case_file = reference_case_file()
    crowded = case_file.exemptions + tuple(
        ExemptionBody(asset_id="asset-clothes", statute_citation="Fla. test")
        for _ in range(12)
    )
    with pytest.raises(FormProjectionError, match="prints 15 rows"):
        project(
            latest_form("form/b106c"),
            CaseFile(**{**case_file.__dict__, "exemptions": crowded}),
        )


# --- B106D --------------------------------------------------------------------


def test_b106d_rows_resolve_their_creditor_and_derive_the_unsecured_portion() -> None:
    release = latest_form("form/b106d")
    values = dict(project(release, reference_case_file()))
    assert row(values, release, "claim.creditor_name", 0) == Text(
        "Gulf Coast Home Loans"
    )
    assert row(values, release, "claim.amount", 0) == Text("195,000.00")
    assert row(values, release, "claim.collateral_value", 0) == Text("240,000.00")
    # 195,000 owed against 240,000 of collateral: nothing unsecured.
    assert row(values, release, "claim.unsecured_portion", 0) == Text("0.00")
    assert row(values, release, "claim.who_owes", 0) == Option("Debtor 1 and 2")
    # Column A: both rows print on page one; no page-two subtotal.
    assert row(values, release, "part1_page_subtotal", 0) == Text("202,400.00")
    entry = values["part1_page_subtotal"]
    assert isinstance(entry, dict)
    assert len(entry) == 1
    assert values["part1_total"] == Text("202,400.00")


def test_b106d_underwater_collateral_leaves_an_unsecured_portion() -> None:
    case_file = reference_case_file()
    resized = tuple(
        (id_, ClaimBody(**{**body.__dict__, "collateral_value": "180000.00"}))
        if id_ == "claim-mortgage"
        else (id_, body)
        for id_, body in case_file.claims
    )
    release = latest_form("form/b106d")
    values = dict(
        project(release, CaseFile(**{**case_file.__dict__, "claims": resized}))
    )
    assert row(values, release, "claim.unsecured_portion", 0) == Text("15,000.00")


def test_b106d_broken_who_owes_rows_use_the_widget_escape_hatches() -> None:
    # Rows 2.4 and 2.5 carry the official PDF's broken groups: 2.4 selects
    # by widget position, 2.5 by the option's own named checkbox.
    case_file = reference_case_file()
    filler = tuple(
        (
            f"claim-extra-{n}",
            ClaimBody(
                creditor_id="cred-auto",
                claim_class="secured",
                amount="100.00",
                who_incurred="at_least_one_plus_another" if n == 1 else "debtor_2",
            ),
        )
        for n in range(3)
    )
    release = latest_form("form/b106d")
    values = project(
        release,
        CaseFile(**{**case_file.__dict__, "claims": case_file.claims + filler}),
    )
    assert values["claim.who_owes_row_2_4"] == WidgetStates(indexes=(3,))
    row_5 = values["claim.who_owes_row_2_5"]
    assert isinstance(row_5, dict)
    assert set(row_5) == {"Debtor 2 only_5"}


def test_b106d_notice_row_four_can_only_mirror_claim_one() -> None:
    # The fourth notice row's account box is the PDF's second widget of
    # claim row 2.1's account field — a different last-four cannot land.
    case_file = reference_case_file()
    parties = tuple(
        NoticeParty(id=f"np-{n}", name=f"Notice Party {n}", account_last4=f"000{n}")
        for n in range(4)
    )
    crowded = tuple(
        (id_, ClaimBody(**{**body.__dict__, "notice_parties": parties}))
        if id_ == "claim-mortgage"
        else (id_, body)
        for id_, body in case_file.claims
    )
    with pytest.raises(FormProjectionError, match="second widget"):
        project(
            latest_form("form/b106d"),
            CaseFile(**{**case_file.__dict__, "claims": crowded}),
        )


# --- B106E/F ------------------------------------------------------------------


def test_b106ef_derives_each_priority_total_and_the_part_4_rollup() -> None:
    release = latest_form("form/b106ef")
    values = dict(project(release, reference_case_file()))
    # The IRS row: 3,200 priority + 450 nonpriority = 3,650 total claim.
    assert row(values, release, "priority.total_claim", 0) == Text("3,650.00")
    assert row(values, release, "priority.priority_amount", 0) == Text("3,200.00")
    assert row(values, release, "priority.nonpriority_amount", 0) == Text("450.00")
    # Part 4: 6b/6e carry the tax claim; 6f the student loan; 6i the two
    # 'other' rows (8,200 + 2,600); 6j all nonpriority claims.
    assert values["line_6b_total"] == Text("3,650.00")
    assert values["line_6e_total"] == Text("3,650.00")
    assert values["line_6f_total"] == Text("12,000.00")
    assert values["line_6i_total"] == Text("10,800.00")
    assert values["line_6j_total"] == Text("22,800.00")
    assert values["line_6a_total"] == Text("0.00")


def test_b106ef_types_and_offsets_land_per_row() -> None:
    release = latest_form("form/b106ef")
    values = dict(project(release, reference_case_file()))
    assert row(values, release, "priority.type_taxes", 0) == Check()
    # Nonpriority rows print in creation order: visa, hospital, student.
    assert row(values, release, "nonpriority.type_other_specify", 0) == Text(
        "Credit card purchases"
    )
    assert row(values, release, "nonpriority.subject_to_offset", 1) == Option("yes")
    assert row(values, release, "nonpriority.type_student_loans", 2) == Check()
    assert row(values, release, "nonpriority.amount", 2) == Text("12,000.00")


def test_b106ef_notice_rows_answer_which_part_from_the_claims_class() -> None:
    release = latest_form("form/b106ef")
    values = dict(project(release, reference_case_file()))
    # The one notice party rides the visa claim — Part 2's box ('yes').
    assert row(values, release, "notify.name", 0) == Text("Meridian Recovery Services")
    assert row(values, release, "notify.referenced_part", 0) == Option("yes")


# --- B106G and B106H ----------------------------------------------------------


def test_b106g_contracts_take_one_printed_row_each() -> None:
    release = latest_form("form/b106g")
    values = dict(project(release, reference_case_file()))
    assert values["line_1_any_contracts"] == Option("yes")
    assert row(values, release, "line_2_counterparty_name", 0) == Text(
        "StorSafe Tampa LLC"
    )
    assert row(values, release, "line_2_contract_description", 1) == Text(
        "Two-year wireless service agreement, 14 months remaining"
    )


def test_b106h_schedule_boxes_derive_from_what_the_links_resolve_to() -> None:
    release = latest_form("form/b106h")
    values = dict(project(release, reference_case_file()))
    assert values["line_1_any_codebtors"] == Option("yes")
    # Margaret co-signed the (secured) car loan: Schedule D only.
    assert row(values, release, "line_3_schedule_d_applies", 0) == Check()
    with pytest.raises(KeyError):
        row(values, release, "line_3_schedule_ef_applies", 0)
    # Charles is on the visa claim and the storage lease: E/F and G.
    assert row(values, release, "line_3_schedule_ef_applies", 1) == Check()
    assert row(values, release, "line_3_schedule_g_applies", 1) == Check()
    # Florida is not a community property state: line 2 answers No and the
    # spouse block stays empty.
    assert values["line_2_lived_in_community_state"] == Option("no")
    assert "line_2_spouse_name" not in values


def test_b106h_prints_one_community_property_block() -> None:
    case_file = reference_case_file()
    member = CommunityHouseholdMemberBody(
        name="Dana Lovelace",
        address=Address(
            line1="4 Alamo Way", city="San Antonio", state="TX", postal_code="78205"
        ),
        community_state="TX",
        lived_with_debtor=True,
    )
    release = latest_form("form/b106h")
    values = project(
        release,
        CaseFile(**{**case_file.__dict__, "community_household_members": (member,)}),
    )
    assert values["line_2_lived_in_community_state"] == Option("yes")
    assert values["line_2_community_state"] == Text("TX")
    assert values["line_2_spouse_lived_with_you"] == Option("yes")

    with pytest.raises(FormProjectionError, match="one community-property block"):
        project(
            release,
            CaseFile(
                **{**case_file.__dict__, "community_household_members": (member,) * 2}
            ),
        )


# --- B106J / B106J-2 ----------------------------------------------------------


def test_b106j_expense_rows_land_on_their_lines_and_lines_22_23_derive() -> None:
    release = latest_form("form/b106j")
    values = dict(project(release, reference_case_file()))
    assert values["line_1_joint_case"] == Option("yes")
    assert values["line_1_debtor2_separate_household"] == Option("no")
    assert values["line_4_rent_or_home_ownership"] == Text("1,480.00")
    assert values["line_16_taxes"] == Text("150.00")
    assert values["line_16_taxes_specify"] == Text("Self-employment tax estimate")
    assert values["line_17a_car_payment_vehicle1"] == Text("260.00")
    assert values["line_17c_installment_other_1"] == Text("55.00")
    assert values["line_21_other_specify"] == Text("Dog food and veterinary care")
    assert row(values, release, "line_2_dependent_relationship", 0) == Text("Daughter")
    assert row(values, release, "line_2_dependent_age", 0) == Text("9")
    assert row(values, release, "line_2_dependent_lives_with_you", 0) == Option("yes")
    # 22a sums lines 4-21; no second household, so 22b stays blank and
    # 22c = 22a; 23c = Schedule I line 12 less 22c.
    assert values["line_22a_total_expenses"] == Text("5,060.00")
    assert "line_22b_debtor2_expenses" not in values
    assert values["line_22c_monthly_expenses"] == Text("5,060.00")
    assert values["line_23a_combined_monthly_income"] == Text("8,149.50")
    assert values["line_23c_net_income"] == Text("3,089.50")


def test_b106j2_projects_only_its_gate_without_a_second_household() -> None:
    release = latest_form("form/b106j2")
    values = dict(project(release, reference_case_file()))
    assert values["line_1_separate_households"] == Option("no")
    assert "line_22_monthly_expenses" not in values


def _with_second_household(case_file: CaseFile) -> CaseFile:
    second = (
        "hh-2",
        HouseholdBody(which_household="debtor_2_separate", separate_household=True),
    )
    extra = (
        ExpenseBody(
            household_id="hh-2", category="rent_or_home_ownership", amount="900.00"
        ),
        ExpenseBody(
            household_id="hh-2", category="food_and_housekeeping", amount="400.00"
        ),
    )
    return CaseFile(
        **{
            **case_file.__dict__,
            "households": (*case_file.households, second),
            "expenses": case_file.expenses + extra,
        }
    )


def test_b106j2_prints_the_second_household_and_carries_to_106j() -> None:
    case_file = _with_second_household(reference_case_file())
    values = dict(project(latest_form("form/b106j2"), case_file))
    assert values["line_1_separate_households"] == Option("yes")
    assert values["line_4_rent_or_home_ownership"] == Text("900.00")
    assert values["line_22_monthly_expenses"] == Text("1,300.00")

    j_values = dict(project(latest_form("form/b106j"), case_file))
    assert j_values["line_22b_debtor2_expenses"] == Text("1,300.00")
    assert j_values["line_22c_monthly_expenses"] == Text("6,360.00")


# --- B106Sum and B106Dec ------------------------------------------------------


def test_b106sum_copies_every_line_from_the_schedules() -> None:
    release = latest_form("form/b106sum")
    values = dict(project(release, reference_case_file()))
    # Line 1: Schedule A/B's part totals.
    assert values["line_1a_total_real_estate"] == Text("240,000.00")
    assert values["line_1b_total_personal_property"] == Text("72,550.00")
    assert values["line_1c_total_property"] == Text("312,550.00")
    # Lines 2-3: D's Column A and E/F's 6e/6j.
    assert values["line_2_secured_claims_total"] == Text("202,400.00")
    assert values["line_3a_priority_unsecured_total"] == Text("3,650.00")
    assert values["line_3b_nonpriority_unsecured_total"] == Text("22,800.00")
    assert values["line_3_total_liabilities"] == Text("228,850.00")
    # Lines 4-5: I's line 12 and J's line 22c.
    assert values["line_4_combined_monthly_income"] == Text("8,149.50")
    assert values["line_5_monthly_expenses"] == Text("5,060.00")
    # Part 3: a chapter-7 consumer filing; line 8 stays blank for the
    # means-test milestone.
    assert values["line_6_filing_under_7_11_13"] == Option("yes")
    assert values["line_7_kind_of_debt"] == Option("consumer")
    assert "line_8_current_monthly_income" not in values
    assert values["line_9b_taxes_government"] == Text("3,650.00")
    assert values["line_9d_student_loans"] == Text("12,000.00")
    assert values["line_9g_total"] == Text("15,650.00")


def test_b106dec_answers_the_preparer_question_and_dates_the_signatures() -> None:
    release = latest_form("form/b106dec")
    values = dict(project(release, reference_case_file()))
    assert values["paid_nonattorney_preparer"] == Option("no")
    assert "preparer_name" not in values
    assert values["debtor1_signature_date"] == Text("08/30/2026")
    assert values["debtor2_signature_date"] == Text("08/30/2026")
    assert "debtor1_signature" not in values


# --- B107 ---------------------------------------------------------------------


def b107_values() -> dict[str, object]:
    release = latest_form("form/b107")
    return dict(project(release, reference_case_file()))


def test_b107_q4_buckets_income_by_period_start_year() -> None:
    # 2026 is the case's creation year (the filing-date stand-in): 2026
    # entries take the current row, 2025 the last-year row (Ada's wages and
    # business income summing there), 2024 the year before.
    values = b107_values()
    assert values["q4_debtor1_current_gross"] == Text("41,600.00")
    assert values["q4_debtor1_last_year_gross"] == Text("65,200.00")
    assert values["q4_debtor1_last_year_wages"] == Check()
    assert values["q4_debtor1_last_year_business"] == Check()
    assert values["q4_debtor1_year_before_gross"] == Text("58,300.00")
    assert values["q4_debtor2_current_gross"] == Text("24,000.00")
    assert values["q4_last_year_yyyy"] == Text("2025")
    assert values["q4_year_before_yyyy"] == Text("2024")
    # Q5: the other-income row; its gate is the PDF quirk where only the
    # No box has a widget, so a Yes leaves the field untouched.
    release = latest_form("form/b107")
    assert row(values, release, "q5_debtor1_source", 0) == Text(
        "Unemployment compensation"
    )
    assert "q5_gate" not in values


def test_b107_q4_refuses_a_period_outside_the_three_printed_years() -> None:
    case_file = reference_case_file()
    stale = SofaEntryBody(
        entry_type="income_by_period",
        payload=IncomeByPeriod(
            which_debtor="debtor_1",
            kind="wages_and_commissions",
            period_start="2020-01-01",
            gross_amount="100.00",
        ),
    )
    with pytest.raises(FormProjectionError, match="fits none"):
        project(
            latest_form("form/b107"),
            CaseFile(
                **{
                    **case_file.__dict__,
                    "sofa_entries": (*case_file.sofa_entries, stale),
                }
            ),
        )


def test_b107_q6_answers_the_consumer_branch_from_the_entries() -> None:
    values = b107_values()
    assert values["q6_consumer_debts"] == Option("yes")
    assert values["q6_paid_600"] == Option("yes")
    assert "q6_paid_8575" not in values
    release = latest_form("form/b107")
    assert row(values, release, "q6_payment_dates", 0) == Text("05/01/2026")
    assert row(values, release, "q6_payment_dates", 2) == Text("07/01/2026")


def test_b107_q2_both_debtors_share_a_row_via_the_same_as_boxes() -> None:
    case_file = reference_case_file()
    shared = SofaEntryBody(
        entry_type="prior_address",
        payload=PriorAddress(
            which_debtor="both",
            address=Address(
                line1="2 Shared Street", city="Tampa", state="FL", postal_code="33601"
            ),
            from_date="2022-01-01",
            to_date="2022-12-31",
        ),
    )
    release = latest_form("form/b107")
    values = dict(
        project(
            release,
            CaseFile(**{**case_file.__dict__, "sofa_entries": (shared,)}),
        )
    )
    assert row(values, release, "q2_debtor1_street", 0) == Text("2 Shared Street")
    assert row(values, release, "q2_debtor2_same_as_debtor1", 0) == Check()
    assert row(values, release, "q2_debtor2_same_dates", 0) == Check()


def test_b107_q26_merged_group_needs_the_widget_escape_hatch() -> None:
    values = b107_values()
    assert values["q26_gate_and_status"] == Option("no")

    case_file = reference_case_file()
    proceeding = SofaEntryBody(
        entry_type="environmental_proceeding",
        payload=EnvironmentalProceeding(
            case_title="State of Florida v. Lovelace",
            case_number="26-ENV-001",
            court=Party(name="Second District Court of Appeal"),
            nature_of_case="Stormwater runoff citation appeal",
            status="on_appeal",
        ),
    )
    values = dict(
        project(
            latest_form("form/b107"),
            CaseFile(
                **{
                    **case_file.__dict__,
                    "sofa_entries": (*case_file.sofa_entries, proceeding),
                }
            ),
        )
    )
    assert values["q26_gate_and_status"] == WidgetStates(states=("yes", "on appeal"))
    assert values["q26_case_number"] == Text("26-ENV-001")


def test_b107_shared_widget_boxes_refuse_a_value_they_cannot_hold() -> None:
    # Q20 row two's ZIP box is the PDF's second widget of row one's — a
    # second closed account in a different ZIP cannot land.
    case_file = reference_case_file()
    second = SofaEntryBody(
        entry_type="closed_account",
        payload=ClosedAccount(
            institution=Party(
                name="Second Bank",
                address=Address(
                    line1="9 Other Road", city="Ocala", state="FL", postal_code="34470"
                ),
            ),
            account_last4="7788",
            account_type="savings",
        ),
    )
    with pytest.raises(FormProjectionError, match="second widget of row one"):
        project(
            latest_form("form/b107"),
            CaseFile(
                **{
                    **case_file.__dict__,
                    "sofa_entries": (*case_file.sofa_entries, second),
                }
            ),
        )


def test_b107_q27_strips_the_ein_and_checks_the_connection() -> None:
    values = b107_values()
    entry = values["q27_ein"]
    assert entry == Text("123456789") or (
        isinstance(entry, dict) and Text("123456789") in entry.values()
    )
    assert values["q27_connection_sole_proprietor"] == Check()
    assert "q27_connection_partner" not in values


def test_b107_signature_block_prints_dates_and_the_preparer_answer() -> None:
    values = b107_values()
    assert values["sign.debtor1_date"] == Text("08/30/2026")
    assert values["sign.paid_preparer"] == Option("no")
    assert "sign.attached_pages" not in values


@pytest.mark.parametrize(
    ("stored", "printed"),
    [("1234.5", "1,234.50"), ("5200.00", "5,200.00"), ("1234567.89", "1,234,567.89")],
)
def test_money_prints_grouped_with_two_decimals(stored: str, printed: str) -> None:
    assert format_money(stored) == printed


def test_dates_print_as_the_forms_spell_them() -> None:
    assert format_date("2019-03-04") == "03/04/2019"
