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
from insolvia_api.core.debtors import CreditCounseling, Debtor, OtherName, Venue
from insolvia_api.core.exemption_claims import ExemptionBody
from insolvia_api.core.fields import Address, PersonName
from insolvia_api.core.form_fill import Option, Text, fill_form
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
    "series", ["form/b101", "form/b106ab", "form/b106c", "form/b106i"]
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


@pytest.mark.parametrize(
    ("stored", "printed"),
    [("1234.5", "1,234.50"), ("5200.00", "5,200.00"), ("1234567.89", "1,234,567.89")],
)
def test_money_prints_grouped_with_two_decimals(stored: str, printed: str) -> None:
    assert format_money(stored) == printed


def test_dates_print_as_the_forms_spell_them() -> None:
    assert format_date("2019-03-04") == "03/04/2019"
