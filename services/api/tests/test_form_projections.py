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
from insolvia_api.core.cases import Case
from insolvia_api.core.debtors import CreditCounseling, Debtor, OtherName, Venue
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


def reference_case_file() -> CaseFile:
    return CaseFile(
        case=REFERENCE_CASE,
        debtors=(_debtor_1(), _debtor_2()),
        petition=PetitionBody(
            fee_handling="installments",
            rents_residence=True,
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


@pytest.mark.parametrize("series", ["form/b101", "form/b106i"])
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


@pytest.mark.parametrize(
    ("stored", "printed"),
    [("1234.5", "1,234.50"), ("5200.00", "5,200.00"), ("1234567.89", "1,234,567.89")],
)
def test_money_prints_grouped_with_two_decimals(stored: str, printed: str) -> None:
    assert format_money(stored) == printed


def test_dates_print_as_the_forms_spell_them() -> None:
    assert format_date("2019-03-04") == "03/04/2019"
