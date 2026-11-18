"""The debtor stores.

The DebtorStore contract runs against the memory twin and the Postgres
adapter alike (ADR 0030).

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import pytest
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.postgres.debtor_store import PostgresDebtorStore
from insolvia_core.debtors import (
    FILING_ROLES,
    Address,
    CreditCounseling,
    Debtor,
    OtherName,
    PersonName,
    Venue,
    debtor_item,
)
from insolvia_core.provenance import ProvenanceEntry

from tests.unit.backends import BACKENDS, postgres_url_with_cases

CASE = "case-0001"
WHEN = "2026-01-01T00:00:00.000000Z"
TYPED = ProvenanceEntry(source="staff_typed")


def a_debtor(*, case_id=CASE, filing_role="debtor_1", surname="Nakamura"):
    """A minimal but *valid* debtor: every populated field carries provenance,
    so it can be pushed through debtor_from_item as well as into a store."""
    return Debtor(
        id=f"debtor-{filing_role}",
        case_id=case_id,
        filing_role=filing_role,
        created_at=WHEN,
        updated_at=WHEN,
        name=PersonName(surname=surname),
        provenance={"name.surname": TYPED},
    )


def a_filled_in_debtor():
    """One with something in every shape the converter has to carry: nested
    maps, a list of maps, a list of scalars, and — inside provenance — a float
    and a map of mixed values."""
    extracted = ProvenanceEntry(
        source="ai_extracted",
        confirmed_by="staff-0001",
        confirmed_at=WHEN,
        document_id="doc-0001",
        locator={"page": 2, "bbox": [0.1, 0.25, 0.3, 0.4]},
        confidence=0.87,
    )
    typed_paths = (
        "name.given",
        "name.surname",
        "other_names_used[alias-0001].surname",
        "employer_ids",
        "residence_address.city",
        "residence_address.state",
        "residence_address.postal_code",
        "phone",
        "venue.basis",
        "credit_counseling.status",
    )
    return Debtor(
        id="debtor-0001",
        case_id=CASE,
        filing_role="debtor_1",
        created_at=WHEN,
        updated_at=WHEN,
        name=PersonName(given="Ada", surname="Nakamura"),
        other_names_used=(OtherName(id="alias-0001", surname="Okafor"),),
        employer_ids=("EIN-000000000",),
        residence_address=Address(
            line1="1 Example Way",
            city="Springfield",
            state="CA",
            postal_code="90000",
        ),
        phone="+1-555-0100",
        venue=Venue(basis="lived_longest_180_days"),
        credit_counseling=CreditCounseling(status="not_required"),
        provenance={
            **dict.fromkeys(typed_paths, TYPED),
            "residence_address.line1": extracted,
        },
    )


# ── The stored debtor item, through each store ──────────────────


def test_a_filled_in_debtor_survives_the_store(store):
    original = a_filled_in_debtor()
    store.put(original)
    assert store.get(CASE, filing_role="debtor_1") == original


def test_an_empty_debtor_survives_the_store(store):
    """A record created before anyone has typed anything, which is what the
    first autosave of every case writes."""
    original = Debtor(
        id="debtor-0002",
        case_id=CASE,
        filing_role="debtor_2",
        created_at=WHEN,
        updated_at=WHEN,
    )
    store.put(original)
    assert store.get(CASE, filing_role="debtor_2") == original


def test_the_stored_item_keys_place_the_debtor_in_its_cases_partition():
    item = debtor_item(a_debtor())
    assert item["PK"] == f"CASE#{CASE}"
    assert item["SK"] == "DEBTOR#debtor_1"
    assert str(item["SK"]).startswith("DEBTOR#")


def test_a_float_in_provenance_is_not_rounded_to_an_int(store):
    """`confidence` is the only float the case data model stores today, and an
    int-only number decoder would turn 0.87 into a crash and 1.0 into 1."""
    store.put(a_filled_in_debtor())
    restored = store.get(CASE, filing_role="debtor_1")
    assert restored is not None
    entry = restored.provenance["residence_address.line1"]
    assert entry.confidence == 0.87
    assert entry.locator == {"page": 2, "bbox": [0.1, 0.25, 0.3, 0.4]}


# ── DebtorStore, memory and Postgres ────────────────────────────


@pytest.fixture(params=BACKENDS)
def store(request):
    if request.param == "memory":
        return MemoryDebtorStore()
    return PostgresDebtorStore(
        postgres_url_with_cases(request, "case-0001", "case-0002")
    )


def test_put_then_get_returns_the_debtor(store):
    debtor = a_debtor()
    store.put(debtor)
    assert store.get(CASE, filing_role="debtor_1") == debtor


def test_get_of_a_role_that_was_never_written_is_none(store):
    store.put(a_debtor())
    assert store.get(CASE, filing_role="debtor_2") is None


def test_get_is_scoped_to_the_case(store):
    """Same role, different case. The record is reached through its case or
    not at all — see the DebtorStore port on why ownership is not a parameter
    here, which makes this scoping the only thing separating two cases."""
    store.put(a_debtor(case_id="case-0001"))
    assert store.get("case-0002", filing_role="debtor_1") is None


def test_put_replaces_the_whole_record(store):
    store.put(a_debtor(surname="Nakamura"))
    store.put(a_debtor(surname="Okafor"))
    assert store.get(CASE, filing_role="debtor_1").name.surname == "Okafor"
    assert len(store.list_for_case(CASE)) == 1


def test_list_for_case_returns_only_that_cases_debtors(store):
    store.put(a_debtor(case_id="case-0001", filing_role="debtor_1"))
    store.put(a_debtor(case_id="case-0001", filing_role="debtor_2"))
    store.put(a_debtor(case_id="case-0002", filing_role="debtor_1"))
    assert [d.filing_role for d in store.list_for_case("case-0001")] == [
        "debtor_1",
        "debtor_2",
    ]


def test_list_for_case_of_a_case_with_no_debtors_is_empty(store):
    assert store.list_for_case(CASE) == ()


def test_list_for_case_is_in_filing_role_order(store):
    for role in reversed(FILING_ROLES):
        store.put(a_debtor(filing_role=role))
    assert [d.filing_role for d in store.list_for_case(CASE)] == list(FILING_ROLES)


def test_ordering_is_by_position_on_the_form_not_alphabetical(store):
    """The test above cannot tell the two apart, because FILING_ROLES happens
    to be in alphabetical order today — a store that sorted by name would pass
    it. This one uses a role the list does not know: it sorts first
    alphabetically and last by position, and it must not raise on the way.
    """
    store.put(a_debtor(filing_role="aa_role_from_a_later_revision"))
    store.put(a_debtor(filing_role="non_filing_spouse"))
    assert [d.filing_role for d in store.list_for_case(CASE)] == [
        "non_filing_spouse",
        "aa_role_from_a_later_revision",
    ]
