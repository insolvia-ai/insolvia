"""The generic case-entity machinery and the ten collection parsers (#249).

Parametrised over the registry wherever the behaviour is the framework's, so
adding an eleventh collection extends the suite by adding its sample payload —
and a kind that is registered without a sample here fails loudly rather than
going untested.
"""

from __future__ import annotations

import pytest
from insolvia_core.case_collections import COLLECTIONS, RESERVED_SK_NAMESPACES
from insolvia_core.case_entities import (
    create_entity,
    entity_body,
    entity_from_item,
    entity_item,
    entity_json,
    parse_entity,
    replace_entity,
)
from insolvia_core.claims import parse_claim
from insolvia_core.codebtors import parse_community_household_member
from insolvia_core.creditors import parse_creditor
from insolvia_core.errors import FieldValidationError
from insolvia_core.exemption_claims import parse_exemption
from insolvia_core.expenses import parse_dependent
from insolvia_core.provenance import populated_paths

CASE = "case-0001"
TYPED = {"source": "staff_typed"}

# One representative populated body per collection. The keys of this dict are
# asserted against the registry below, so a new collection cannot land without
# a sample.
SAMPLE_BODIES: dict[str, dict[str, object]] = {
    "creditors": {"name": "Example Bank", "address": {"line1": "1 Example Way"}},
    "claims": {
        "creditor_id": "cr-1",
        "claim_class": "secured",
        "amount": "12500.00",
        "contingent": False,
        "collateral_description": "2016 sedan",
        "collateral_value": "9000.00",
        "lien_nature": ["agreement"],
        "notice_parties": [{"id": "np1", "name": "Example Servicer"}],
    },
    "assets": {
        "category": "vehicle",
        "description": "2016 sedan, 90k miles",
        "value_entire": "9000.00",
        "value_portion_owned": "9000.00",
        "ownership_interest": "debtor_1",
        "community_property": False,
    },
    "employments": {
        "debtor_id": "d-1",
        "status": "employed",
        "occupation": "Example occupation",
        "employer_name": "Example Employer",
        "employer_address": {"city": "Exampleville"},
        "employed_since": "2019-02-14",
    },
    "income_summaries": {
        "debtor_id": "d-1",
        "wages": "5200.00",
        "deduction_tax": "830.00",
        "change_expected": False,
    },
    "pay_period_records": {
        "employment_id": "emp-1",
        "period_start": "2026-08-01",
        "period_end": "2026-08-14",
        "pay_date": "2026-08-19",
        "gross": "2400.00",
        "net": "1890.50",
        "frequency": "biweekly",
        "deductions": [
            {"id": "ded1", "category": "tax", "amount": "410.00"},
            {
                "id": "ded2",
                "category": "insurance",
                "amount": "99.50",
                "description": "Medical premium",
            },
        ],
    },
    "households": {"which_household": "main", "separate_household": False},
    "expenses": {
        "household_id": "h-1",
        "category": "rent_or_home_ownership",
        "amount": "1800.00",
    },
    "dependents": {
        "household_id": "h-1",
        "relationship": "daughter",
        "age": 9,
        "lives_with_debtor": True,
    },
    "codebtors": {
        "name": "Example Cosigner",
        "address": {"city": "Exampleville"},
        "claim_ids": ["cl-1"],
    },
    "sofa_entries": {
        "entry_type": "gift",
        "payload": {
            "recipient": {"name": "Example Recipient"},
            "relationship": "niece",
            "dates": ["2025-12-25"],
            "value": "700.00",
        },
    },
    "petitions": {
        "fee_handling": "installments",
        "rents_residence": True,
        "eviction_judgment_against_you": False,
        "small_business_status": "not_filing_under_chapter_11",
        "hazardous_property": {
            "description": "Propane tanks at a former stand",
            "why_immediate": "Leak risk",
            "address": {"city": "Exampleville"},
        },
        "debt_character": "consumer",
        "ch7_funds_available_for_creditors": False,
        "estimated_creditors": "1_49",
        "estimated_assets": "0_50000",
        "estimated_liabilities": "50001_100000",
    },
    "prior_cases": {
        "district": "Middle District of Florida",
        "filed_on": "2019-03-04",
        "case_number": "19-01234",
    },
    "related_cases": {
        "debtor_name": "Example Spouse",
        "relationship": "Spouse",
        "district": "Middle District of Florida",
        "filed_on": "2026-01-15",
        "case_number": "26-00042",
    },
    "sole_proprietorships": {
        "name": "Example Lawn Care",
        "address": {"city": "Exampleville"},
        "business_type": "none_of_the_above",
    },
    "filing_professionals": {
        "role": "attorney",
        "name": {"given": "Alex", "surname": "Counsel"},
        "firm_name": "Counsel & Counsel PA",
        "address": {"line1": "1 Example Way", "city": "Exampleville"},
        "phone": "(305) 555-0100",
        "email": "alex@example.com",
        "bar_number": "112233",
        "bar_state": "FL",
        "signature_date": "2026-09-01",
    },
    "exemptions": {
        "asset_id": "as-1",
        "statute_citation": "Fla. Stat. § 222.25(1)",
        "amount": "5000.00",
        "claims_full_fmv": False,
        "acquired_within_1215_days": False,
    },
    "contract_leases": {
        "counterparty_name": "Example Storage LLC",
        "counterparty_address": {"line1": "1 Example Way", "city": "Exampleville"},
        "description": "Month-to-month storage unit lease, unit 12",
    },
    "community_household_members": {
        "name": "Example Former Spouse",
        "address": {"city": "Exampleville", "state": "TX"},
        "community_state": "TX",
        "lived_with_debtor": False,
    },
    "other_income_records": {
        "debtor_id": "d-1",
        "category": "unemployment",
        "received_on": "2026-04-07",
        "amount": "275.00",
        "payer": "Example State Agency",
    },
}


def typed_provenance(collection: str, body: dict[str, object]) -> dict[str, object]:
    """A staff_typed entry for every populated path — built with the server's
    own walk so these tests exercise the same pairing the client relies on."""
    kind = COLLECTIONS[collection]
    draft = parse_entity(kind, body, enforce_provenance=False)
    return {path: dict(TYPED) for path in populated_paths(entity_body(draft))}


def sample_payload(collection: str) -> dict[str, object]:
    body = dict(SAMPLE_BODIES[collection])
    return {**body, "provenance": typed_provenance(collection, body)}


# ── The registry ────────────────────────────────────────────────


def test_every_collection_has_a_sample_body() -> None:
    assert set(SAMPLE_BODIES) == set(COLLECTIONS)


def test_collection_keys_match_their_kinds() -> None:
    assert all(
        kind.collection == collection for collection, kind in COLLECTIONS.items()
    )


def test_sort_key_namespaces_cannot_collide() -> None:
    """A duplicated prefix would hand one collection's items to another's
    begins_with query — including the non-generic namespaces the case
    partition already uses."""
    prefixes = [kind.sk_prefix for kind in COLLECTIONS.values()]
    all_namespaces = prefixes + list(RESERVED_SK_NAMESPACES)
    assert len(set(all_namespaces)) == len(all_namespaces)


# ── Parsing and the provenance invariants ───────────────────────


@pytest.mark.parametrize("collection", sorted(COLLECTIONS))
def test_a_populated_body_with_full_provenance_parses(collection: str) -> None:
    draft = parse_entity(COLLECTIONS[collection], sample_payload(collection))
    assert draft.provenance


@pytest.mark.parametrize("collection", sorted(COLLECTIONS))
def test_a_populated_field_without_provenance_is_rejected(collection: str) -> None:
    payload = dict(SAMPLE_BODIES[collection])
    with pytest.raises(FieldValidationError) as failure:
        parse_entity(COLLECTIONS[collection], payload)
    assert all(path.startswith("provenance.") for path in failure.value.fields)


@pytest.mark.parametrize("collection", sorted(COLLECTIONS))
def test_an_empty_body_parses_with_no_provenance(collection: str) -> None:
    # Progressive intake: a record with nothing in it is savable.
    draft = parse_entity(COLLECTIONS[collection], {})
    assert draft.provenance == {}


@pytest.mark.parametrize("collection", sorted(COLLECTIONS))
def test_an_unconfirmed_machine_value_cannot_be_stored(collection: str) -> None:
    """Invariant 2, inherited by every collection: ai_extracted without a
    confirmation is refused. This is the seam that keeps extraction review a
    UI change."""
    body = dict(SAMPLE_BODIES[collection])
    provenance = typed_provenance(collection, body)
    first = next(iter(provenance))
    provenance[first] = {"source": "ai_extracted"}
    with pytest.raises(FieldValidationError) as failure:
        parse_entity(COLLECTIONS[collection], {**body, "provenance": provenance})
    assert f"provenance.{first}" in failure.value.fields


def test_a_confirmed_machine_value_is_accepted() -> None:
    body = {"name": "Example Bank"}
    provenance = {
        "name": {
            "source": "ai_extracted",
            "confirmed_by": "00000000-0000-4000-8000-00000000a11c",
            "confirmed_at": "2026-08-01T12:00:00.000000Z",
            "confidence": 0.93,
        }
    }
    draft = parse_entity(COLLECTIONS["creditors"], {**body, "provenance": provenance})
    assert draft.provenance["name"].confirmed_by is not None


# ── The stored item and the wire shape ──────────────────────────


@pytest.mark.parametrize("collection", sorted(COLLECTIONS))
def test_an_entity_survives_the_item_round_trip(collection: str) -> None:
    kind = COLLECTIONS[collection]
    draft = parse_entity(kind, sample_payload(collection))
    entity = create_entity(kind, draft, case_id=CASE)
    restored = entity_from_item(kind, entity_item(entity))
    assert restored == entity


@pytest.mark.parametrize("collection", sorted(COLLECTIONS))
def test_the_item_lives_in_the_case_partition_under_its_prefix(
    collection: str,
) -> None:
    kind = COLLECTIONS[collection]
    entity = create_entity(kind, parse_entity(kind, {}), case_id=CASE)
    item = entity_item(entity)
    assert item["PK"] == f"CASE#{CASE}"
    assert item["SK"] == f"{kind.sk_prefix}#{entity.id}"


def test_json_omits_absent_members_and_keeps_false() -> None:
    kind = COLLECTIONS["claims"]
    payload = sample_payload("claims")
    entity = create_entity(kind, parse_entity(kind, payload), case_id=CASE)
    json = entity_json(entity)
    # `contingent: False` is an answer and survives; everything never sent is
    # genuinely absent rather than null.
    assert json["contingent"] is False
    assert "disputed" not in json
    assert json["case_id"] == CASE
    assert json["provenance"]


def test_replace_keeps_id_and_created_at() -> None:
    kind = COLLECTIONS["creditors"]
    entity = create_entity(
        kind, parse_entity(kind, sample_payload("creditors")), case_id=CASE
    )
    body = {"name": "Renamed Bank"}
    replaced = replace_entity(
        entity,
        parse_entity(kind, {**body, "provenance": typed_provenance("creditors", body)}),
    )
    assert (replaced.id, replaced.case_id, replaced.created_at) == (
        entity.id,
        entity.case_id,
        entity.created_at,
    )
    assert replaced.body.name == "Renamed Bank"


# ── Field rules, exercised through the entity parsers ───────────


@pytest.mark.parametrize(
    "amount",
    ["1,200.00", "12.345", "-5.00", "NaN", "Infinity", "abc"],
)
def test_a_malformed_amount_is_rejected(amount: str) -> None:
    with pytest.raises(FieldValidationError) as failure:
        parse_claim({"amount": amount})
    assert "amount" in failure.value.fields


def test_an_amount_sent_as_a_number_is_rejected() -> None:
    # Money is a string on the wire — a JSON number has already been through
    # the sender's floating point.
    with pytest.raises(FieldValidationError):
        parse_claim({"amount": 1200.0})


def test_an_amount_is_canonicalised_to_two_places() -> None:
    assert parse_claim({"amount": "1200"}).amount == "1200.00"
    assert parse_claim({"amount": "1200.5"}).amount == "1200.50"


def test_an_unknown_enum_member_is_rejected() -> None:
    with pytest.raises(FieldValidationError) as failure:
        parse_claim({"claim_class": "unsecured"})
    assert "claim_class" in failure.value.fields


def test_a_notice_party_without_an_id_is_refused() -> None:
    # The same contract as a debtor's other_names_used: provenance for the
    # row's fields must name an id the client already chose.
    with pytest.raises(FieldValidationError) as failure:
        parse_claim({"notice_parties": [{"name": "Example Servicer"}]})
    assert "notice_parties[0].id" in failure.value.fields


def test_a_dependents_name_is_refused_not_dropped() -> None:
    # The form does not ask for dependents' names, so storing one would be
    # collecting a child's PII no form prints — and silently dropping it would
    # leave the client believing it was stored.
    with pytest.raises(FieldValidationError) as failure:
        parse_dependent({"name": "A Child"})
    assert "name" in failure.value.fields


def test_an_age_of_true_is_not_a_count() -> None:
    with pytest.raises(FieldValidationError) as failure:
        parse_dependent({"age": True})
    assert "age" in failure.value.fields


def test_a_cleared_field_collapses_to_absent() -> None:
    body = parse_creditor({"name": "   "})
    assert body.name is None


def test_an_exemption_accepts_amount_and_election_together() -> None:
    # Mutually exclusive per the model, but that is the completeness gate's
    # rule: an intake that typed the amount before answering the election
    # must persist (storage validates shape and type only).
    body = parse_exemption({"amount": "5000.00", "claims_full_fmv": True})
    assert (body.amount, body.claims_full_fmv) == ("5000.00", True)


def test_a_community_state_is_a_two_letter_code() -> None:
    with pytest.raises(FieldValidationError) as failure:
        parse_community_household_member({"community_state": "Texas"})
    assert "community_state" in failure.value.fields
