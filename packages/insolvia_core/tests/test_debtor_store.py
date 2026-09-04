"""The debtor stores, and the attribute converter both of them rest on.

DynamoDbDebtorStore is deliberately NOT tested here. This suite has no boto3
stub and no moto by decision (ADR 0008: monkeypatch a transport, fake at a
port), and there is no transport to patch under a boto3 client — so a test of
its query wiring would be a test of a mock's shape.

What IS reachable is the part that would actually be wrong, and it is tested
end to end: `debtor_item → to_attributes → from_attributes → debtor_from_item`
is every line of conversion the AWS adapter performs, minus the network. A
debtor that survives that is one the real store can round-trip.

Two things the AWS adapter owns alone stay unverified, and they are named here
so nobody assumes otherwise: that `begins_with(SK, "DEBTOR#")` keeps the case's
own SK=META row out of a list, and that a query comes back in sort-key order.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import pytest
from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.cases import Case, case_from_item, case_item
from insolvia_core.debtors import (
    FILING_ROLES,
    Address,
    CreditCounseling,
    Debtor,
    OtherName,
    PersonName,
    Venue,
    debtor_from_item,
    debtor_item,
)
from insolvia_core.provenance import ProvenanceEntry

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


# ── The converter ───────────────────────────────────────────────


@pytest.mark.parametrize(
    "value",
    [
        "text",
        "",
        0,
        7,
        -3,
        0.5,
        1.0,
        True,
        False,
        None,
        {},
        [],
        {"a": 1, "b": ["x", None], "c": {"d": {"e": False}}},
        [{"id": "alias-1", "surname": "Okafor"}, {"id": "alias-2"}],
        [[1, [2, [3]]]],
    ],
)
def test_every_supported_value_round_trips(value):
    assert from_attributes(to_attributes({"v": value}))["v"] == value


def test_a_boolean_is_not_stored_as_a_number():
    """The trap this converter exists to avoid. `bool` is a subclass of `int`,
    so an `isinstance(value, int)` branch placed first writes True as the
    number 1 — which reads back as 1, and `1 == True`, so every equality
    assertion in the test above would still pass. Hence the stored shape and
    the type, rather than equality.
    """
    assert to_attributes({"flag": True, "off": False}) == {
        "flag": {"BOOL": True},
        "off": {"BOOL": False},
    }
    assert from_attributes({"flag": {"BOOL": True}})["flag"] is True
    assert from_attributes({"flag": {"BOOL": False}})["flag"] is False


@pytest.mark.parametrize(
    ("value", "expected"),
    [(7, int), (0, int), (-3, int), (0.87, float), (1.0, float), (True, bool)],
)
def test_a_number_keeps_the_python_type_it_went_in_as(value, expected):
    # DynamoDB has one number type. case_from_item wants an int chapter and a
    # provenance confidence is a float, so the split has to survive the trip.
    assert type(from_attributes(to_attributes({"v": value}))["v"]) is expected


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        (7, {"N": "7"}),
        (0, {"N": "0"}),
        ("7", {"S": "7"}),
        ("META", {"S": "META"}),
    ],
)
def test_the_encoding_the_case_store_already_wrote_is_unchanged(value, expected):
    """Rows written before this converter existed are still in the dev table
    and in staging. S and N have to mean exactly what they meant then."""
    assert to_attributes({"v": value}) == {"v": expected}


def test_a_case_item_still_round_trips():
    """The property case_store's own two helpers had, asserted after they were
    replaced — this is the whole risk of that migration."""
    case = Case(
        id="c-0001",
        firm_id="00000000-0000-4000-8000-00000000f18a",
        created_by="00000000-0000-4000-8000-00000000a11c",
        chapter=7,
        district="NDCA",
        status="intake",
        created_at=WHEN,
        updated_at=WHEN,
    )
    assert case_from_item(from_attributes(to_attributes(case_item(case)))) == case


def test_empty_containers_survive():
    """Not an edge case: a debtor who has answered nothing yet stores a body of
    `{}`, and that is the normal state for most of an intake."""
    stored = to_attributes({"body": {}, "aliases": []})
    assert stored == {"body": {"M": {}}, "aliases": {"L": []}}
    assert from_attributes(stored) == {"body": {}, "aliases": []}


def test_a_tuple_comes_back_as_a_list():
    # DynamoDB has one sequence type, so this asymmetry is real. Nothing in
    # core depends on it — the parse functions rebuild their own tuples.
    assert from_attributes(to_attributes({"ids": ("a", "b")}))["ids"] == ["a", "b"]


def test_a_null_is_a_null_and_not_a_missing_key():
    assert to_attributes({"v": None}) == {"v": {"NULL": True}}
    assert from_attributes({"v": {"NULL": True}}) == {"v": None}


@pytest.mark.parametrize("value", [b"bytes", bytearray(b"bytes"), {1, 2}, object()])
def test_a_value_that_cannot_be_stored_is_refused_rather_than_mangled(value):
    # bytes is a Sequence, so a converter that let it fall through would write
    # it as a list of integers and never say anything.
    with pytest.raises(TypeError):
        to_attributes({"v": value})


@pytest.mark.parametrize("attribute", [{"SS": ["a"]}, {"B": "abc"}, {}])
def test_an_attribute_type_this_service_never_writes_is_refused(attribute):
    with pytest.raises(ValueError, match="unsupported DynamoDB attribute"):
        from_attributes({"v": attribute})


# ── The stored debtor item, through the converter ───────────────


def test_a_filled_in_debtor_survives_the_trip_to_dynamodb_and_back():
    original = a_filled_in_debtor()
    stored = from_attributes(to_attributes(debtor_item(original)))
    assert debtor_from_item(stored) == original


def test_an_empty_debtor_survives_the_trip_to_dynamodb_and_back():
    """A record created before anyone has typed anything, which is what the
    first autosave of every case writes."""
    original = Debtor(
        id="debtor-0002",
        case_id=CASE,
        filing_role="debtor_2",
        created_at=WHEN,
        updated_at=WHEN,
    )
    stored = from_attributes(to_attributes(debtor_item(original)))
    assert debtor_from_item(stored) == original


def test_the_stored_item_keys_place_the_debtor_in_its_cases_partition():
    item = debtor_item(a_debtor())
    assert item["PK"] == f"CASE#{CASE}"
    assert item["SK"] == "DEBTOR#debtor_1"
    # The prefix DynamoDbDebtorStore queries on, and the reason a list of
    # debtors does not also return the case's own SK=META row.
    assert str(item["SK"]).startswith("DEBTOR#")


def test_a_float_in_provenance_is_not_rounded_to_an_int():
    """`confidence` is the only float the case data model stores today, and an
    int-only number decoder would turn 0.87 into a crash and 1.0 into 1."""
    original = a_filled_in_debtor()
    restored = debtor_from_item(from_attributes(to_attributes(debtor_item(original))))
    entry = restored.provenance["residence_address.line1"]
    assert entry.confidence == 0.87
    assert entry.locator == {"page": 2, "bbox": [0.1, 0.25, 0.3, 0.4]}


# ── MemoryDebtorStore ───────────────────────────────────────────


def test_put_then_get_returns_the_debtor():
    store = MemoryDebtorStore()
    debtor = a_debtor()
    store.put(debtor)
    assert store.get(CASE, filing_role="debtor_1") == debtor


def test_get_of_a_role_that_was_never_written_is_none():
    store = MemoryDebtorStore()
    store.put(a_debtor())
    assert store.get(CASE, filing_role="debtor_2") is None


def test_get_is_scoped_to_the_case():
    """Same role, different case. The record is reached through its case or
    not at all — see the DebtorStore port on why ownership is not a parameter
    here, which makes this scoping the only thing separating two cases."""
    store = MemoryDebtorStore()
    store.put(a_debtor(case_id="case-0001"))
    assert store.get("case-0002", filing_role="debtor_1") is None


def test_put_replaces_the_whole_record():
    store = MemoryDebtorStore()
    store.put(a_debtor(surname="Nakamura"))
    store.put(a_debtor(surname="Okafor"))
    assert store.get(CASE, filing_role="debtor_1").name.surname == "Okafor"
    assert len(store.list_for_case(CASE)) == 1


def test_list_for_case_returns_only_that_cases_debtors():
    store = MemoryDebtorStore()
    store.put(a_debtor(case_id="case-0001", filing_role="debtor_1"))
    store.put(a_debtor(case_id="case-0001", filing_role="debtor_2"))
    store.put(a_debtor(case_id="case-0002", filing_role="debtor_1"))
    assert [d.filing_role for d in store.list_for_case("case-0001")] == [
        "debtor_1",
        "debtor_2",
    ]


def test_list_for_case_of_a_case_with_no_debtors_is_empty():
    assert MemoryDebtorStore().list_for_case(CASE) == ()


def test_list_for_case_is_in_filing_role_order():
    store = MemoryDebtorStore()
    for role in reversed(FILING_ROLES):
        store.put(a_debtor(filing_role=role))
    assert [d.filing_role for d in store.list_for_case(CASE)] == list(FILING_ROLES)


def test_ordering_is_by_position_on_the_form_not_alphabetical():
    """The test above cannot tell the two apart, because FILING_ROLES happens
    to be in alphabetical order today — a store that sorted by name would pass
    it. This one uses a role the list does not know: it sorts first
    alphabetically and last by position, and it must not raise on the way.
    """
    store = MemoryDebtorStore()
    store.put(a_debtor(filing_role="aa_role_from_a_later_revision"))
    store.put(a_debtor(filing_role="non_filing_spouse"))
    assert [d.filing_role for d in store.list_for_case(CASE)] == [
        "non_filing_spouse",
        "aa_role_from_a_later_revision",
    ]
