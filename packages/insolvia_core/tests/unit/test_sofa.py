"""The SOFA typed-entry table: one sample per entry type, plus the dispatch
rules.

The model doc is explicit that the ~two dozen hand-written payload parsers
"need per-type tests" — the table below is those tests, and its key set is
asserted against the dispatch table so a parser cannot be added untested.
"""

from __future__ import annotations

from dataclasses import asdict

import pytest
from insolvia_core.errors import FieldValidationError
from insolvia_core.fields import prune_body
from insolvia_core.sofa import PAYLOAD_PARSERS, parse_sofa_entry

# One representative payload per entry type: every value below survives the
# parse unchanged (amounts are already canonical), so the assertion can be an
# exact round trip rather than a spot check.
SAMPLE_PAYLOADS: dict[str, dict[str, object]] = {
    "marital_status": {"status": "married"},
    "prior_address": {
        "which_debtor": "both",
        "address": {"line1": "2 Former Rd", "city": "Oldtown"},
        "from_date": "2023-01-01",
        "to_date": "2024-06-30",
    },
    "community_property_residence": {"state": "TX"},
    "income_by_period": {
        "which_debtor": "debtor_1",
        "kind": "wages_and_commissions",
        "description": "Example Employer",
        "period_start": "2025-01-01",
        "period_end": "2025-12-31",
        "gross_amount": "62000.00",
    },
    "consumer_debt_declaration": {"primarily_consumer_debts": True},
    "creditor_payment": {
        "creditor": {"name": "Example Bank", "address": {"city": "Exampleville"}},
        "dates": ["2026-07-01", "2026-08-01"],
        "total_paid": "1300.00",
        "amount_still_owed": "8200.00",
        "payment_for": ["car"],
    },
    "insider_payment": {
        "insider": {"name": "Example Relative"},
        "relationship": "brother",
        "dates": ["2026-03-15"],
        "total_paid": "900.00",
        "amount_still_owed": "0.00",
        "reason": "loan repayment",
    },
    "insider_benefit_payment": {
        "recipient": {"name": "Example Lender"},
        "insider_name": "Example Relative",
        "dates": ["2026-02-01"],
        "total_paid": "450.00",
        "reason": "payment on a cosigned note",
    },
    "lawsuit": {
        "case_title": "Example Bank v. Debtor",
        "case_number": "26-cv-0001",
        "nature_of_case": "collection",
        "court": {"name": "Example County Court"},
        "status": "pending",
    },
    "repossession": {
        "creditor": {"name": "Example Finance"},
        "action": "repossessed",
        "description": "2014 pickup",
        "date": "2026-01-20",
        "value": "6000.00",
    },
    "setoff": {
        "creditor": {"name": "Example Credit Union"},
        "description": "savings applied to loan",
        "date": "2026-07-15",
        "amount": "350.00",
    },
    "receivership": {
        "custodian": {"name": "Example Receiver LLC"},
        "description": "rental property",
        "value": "150000.00",
        "case_title": "In re Example",
        "case_number": "26-mc-0002",
        "court": {"name": "Example District Court"},
        "date": "2026-04-01",
    },
    "gift": {
        "recipient": {"name": "Example Recipient"},
        "relationship": "niece",
        "description": "used car",
        "dates": ["2025-12-25"],
        "value": "700.00",
    },
    "charitable_contribution": {
        "organization": {"name": "Example Charity"},
        "description": "cash",
        "dates": ["2025-11-30"],
        "value": "650.00",
    },
    "loss": {
        "description": "kitchen fire",
        "insurance_coverage": "homeowner's policy, claim pending",
        "date": "2026-02-10",
        "value": "4000.00",
    },
    "consultant_payment": {
        "person": {"name": "Example Law Firm"},
        "email_or_website": "example.test",
        "who_made_payment": "debtor's mother",
        "description": "chapter 7 preparation",
        "date": "2026-08-01",
        "amount": "1500.00",
    },
    "creditor_assistance_payment": {
        "person": {"name": "Example Debt Relief Co"},
        "description": "debt settlement plan",
        "date": "2026-05-01",
        "amount": "300.00",
    },
    "property_transfer": {
        "transferee": {"name": "Example Buyer"},
        "relationship": "none",
        "description": "boat",
        "value_received": "cash at fair value",
        "date": "2025-09-01",
    },
    "self_settled_trust": {
        "trust_name": "Example Family Trust",
        "description": "brokerage account",
        "date": "2020-06-01",
    },
    "closed_account": {
        "institution": {"name": "Example Savings"},
        "account_last4": "1234",
        "account_type": "savings",
        "date_closed": "2026-03-01",
        "last_balance": "80.00",
    },
    "safe_deposit_box": {
        "institution": {"name": "Example Bank"},
        "who_has_access": ["Debtor", "Spouse"],
        "description": "documents and jewelry",
        "still_have": True,
    },
    "storage_unit": {
        "facility": {"name": "Example Storage"},
        "who_has_access": ["Debtor"],
        "description": "furniture",
        "still_have": False,
    },
    "held_for_another": {
        "owner": {"name": "Example Neighbour"},
        "location": "debtor's garage",
        "description": "motorcycle",
        "value": "3500.00",
    },
    "environmental_notice": {
        "kind": "liability_notice_received",
        "site": {"name": "Example Site", "address": {"city": "Exampleville"}},
        "governmental_unit": {"name": "State EPA"},
        "environmental_law": "state cleanup act",
        "date": "2025-10-01",
    },
    "environmental_proceeding": {
        "case_title": "State v. Example",
        "case_number": "25-env-0003",
        "court": {"name": "Example Admin Board"},
        "nature_of_case": "cleanup order",
        "status": "concluded",
    },
    "business_connection": {
        "business": {"name": "Example Consulting LLC"},
        "nature_of_business": "consulting",
        "ein": "12-3456789",
        "from_date": "2022-01-01",
        "to_date": "2025-12-31",
        "connection": ["sole_proprietor"],
    },
    "financial_statement_issued": {
        "recipient": {"name": "Example Bank"},
        "date_issued": "2025-05-01",
    },
}


def test_every_entry_type_has_a_sample() -> None:
    assert set(SAMPLE_PAYLOADS) == set(PAYLOAD_PARSERS)


@pytest.mark.parametrize("entry_type", sorted(SAMPLE_PAYLOADS))
def test_a_valid_payload_parses_and_round_trips(entry_type: str) -> None:
    sent = SAMPLE_PAYLOADS[entry_type]
    body = parse_sofa_entry({"entry_type": entry_type, "payload": sent})
    assert body.entry_type == entry_type
    assert body.payload is not None
    # Exact: what was sent is what is stored, in the stored (pruned) shape.
    assert prune_body(asdict(body.payload)) == sent


@pytest.mark.parametrize("entry_type", sorted(SAMPLE_PAYLOADS))
def test_an_empty_payload_is_valid_for_every_type(entry_type: str) -> None:
    # Progressive intake: a typed row with nothing filled in yet must persist.
    body = parse_sofa_entry({"entry_type": entry_type, "payload": {}})
    assert body.payload is not None


def test_a_payload_without_a_type_is_refused() -> None:
    with pytest.raises(FieldValidationError) as failure:
        parse_sofa_entry({"payload": {"status": "married"}})
    assert "entry_type" in failure.value.fields


def test_an_unknown_entry_type_is_refused() -> None:
    with pytest.raises(FieldValidationError) as failure:
        parse_sofa_entry({"entry_type": "question_29", "payload": {}})
    assert "entry_type" in failure.value.fields


def test_a_type_with_no_payload_yet_is_valid() -> None:
    body = parse_sofa_entry({"entry_type": "lawsuit"})
    assert body.entry_type == "lawsuit"
    assert body.payload is None


@pytest.mark.parametrize(
    ("entry_type", "payload", "bad_path"),
    [
        ("marital_status", {"status": "divorced"}, "payload.status"),
        ("gift", {"value": "sev hundred"}, "payload.value"),
        ("prior_address", {"from_date": "2023-13-01"}, "payload.from_date"),
        ("closed_account", {"account_last4": "12ab"}, "payload.account_last4"),
        ("creditor_payment", {"dates": ["yesterday"]}, "payload.dates[0]"),
        (
            "consumer_debt_declaration",
            {"primarily_consumer_debts": "yes"},
            "payload.primarily_consumer_debts",
        ),
        ("business_connection", {"connection": ["landlord"]}, "payload.connection[0]"),
    ],
)
def test_a_malformed_payload_field_is_named_in_the_error(
    entry_type: str, payload: dict[str, object], bad_path: str
) -> None:
    with pytest.raises(FieldValidationError) as failure:
        parse_sofa_entry({"entry_type": entry_type, "payload": payload})
    assert bad_path in failure.value.fields
