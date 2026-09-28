"""A case's debtors copied from firm clients (ADR 0022, PR 2): the `client`
provenance source, the copy itself, the computed divergence, the debtor item
that feeds the `by-client` index, and both case stores' transaction and
listing.

The route-level behaviour — who may open a case for whom, what the API
echoes — is services/api's test_cases.py and test_debtor_routes.py; this file
pins the domain rules and the stores.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace
from typing import Any

import pytest
from insolvia_core.access import Accessor
from insolvia_core.adapters.aws import case_store as aws_case_store
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.dynamo import to_attributes
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.cases import assignment_item, case_item, create_case, parse_case_creation
from insolvia_core.debtors import (
    create_debtor,
    debtor_from_item,
    debtor_item,
    debtor_json,
    link_client,
    parse_debtor,
    replace_debtor,
)
from insolvia_core.errors import FieldValidationError
from insolvia_core.fields import Address
from insolvia_core.firm_clients import (
    COPIED_FIELDS,
    create_firm_client,
    debtor_from_client,
    differs_from_client,
    parse_firm_client,
)
from insolvia_core.firms import Firm, FirmUser, default_permissions
from insolvia_core.provenance import SOURCES, parse_provenance, provenance_json

FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
DANA = "00000000-0000-4000-8000-00000000da4a"
BOB = "00000000-0000-4000-8000-00000000b0b0"

CLIENT_BODY = {
    "name": {"given": "Jordan", "surname": "Example", "suffix": "Jr."},
    "other_names_used": [{"id": "alias-1", "surname": "Sample"}],
    "date_of_birth": "1980-02-29",
    "residence_address": {
        "line1": "1 Example St",
        "city": "Springfield",
        "state": "IL",
        "postal_code": "62701",
        "county": "Sangamon",
    },
    "phone": "555-0100",
    "email": "jordan@example.test",
    "lead_source": "Referral",
}


def a_client(firm_id=FIRM_A, **overrides):
    draft = parse_firm_client({**CLIENT_BODY, **overrides})
    return create_firm_client(draft, firm_id=firm_id, created_by=ALICE)


def a_case(firm_id=FIRM_A, created_by=ALICE):
    return create_case(
        parse_case_creation(
            {"chapter": 7, "court": "flmb", "division": "tampa"},
            require_clients=False,
        ),
        firm_id=firm_id,
        created_by=created_by,
    )


def accessor(subject, firm_id=FIRM_A, *, is_admin=False, access_all_cases=False):
    firm = Firm(
        id=firm_id,
        name="Example",
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )
    user = FirmUser(
        firm_id=firm_id,
        subject=subject,
        email="person@example.test",
        first_name="Person",
        last_name="Example",
        role="attorney",
        is_admin=is_admin,
        access_all_cases=access_all_cases,
        permissions=default_permissions("attorney"),
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )
    return Accessor(firm=firm, user=user)


# ── The `client` provenance source ──────────────────────────────────


def test_client_is_a_source_and_names_its_client():
    assert "client" in SOURCES
    entries = parse_provenance({"name.given": {"source": "client", "client_id": "c-1"}})
    assert entries["name.given"].client_id == "c-1"
    assert provenance_json(entries) == {
        "name.given": {"source": "client", "client_id": "c-1"}
    }


def test_a_client_value_needs_no_confirmation():
    """A person chose the client, as they choose a library entry — the
    confirm-before-entry rule is for machine sources only."""
    parse_provenance({"name.given": {"source": "client", "client_id": "c-1"}})


def test_a_client_id_that_is_not_text_is_refused():
    with pytest.raises(FieldValidationError) as caught:
        parse_provenance({"name.given": {"source": "client", "client_id": 7}})
    assert "provenance.name.given.client_id" in caught.value.fields


# ── The copy ────────────────────────────────────────────────────────


def test_the_copy_carries_the_identity_and_every_field_says_where_it_came_from():
    client = a_client()
    case, _ = a_case()
    debtor = debtor_from_client(client, case=case, filing_role="debtor_1")

    assert debtor.client_id == client.id
    assert debtor.case_id == case.id
    assert debtor.case_created_at == case.created_at
    assert debtor.name == client.name
    assert debtor.other_names_used == client.other_names_used
    assert debtor.residence_address == client.residence_address
    assert debtor.residence_address.county == "Sangamon"
    assert debtor.phone == "555-0100"
    assert debtor.email == "jordan@example.test"
    entry = {"source": "client", "client_id": client.id}
    assert provenance_json(debtor.provenance) == {
        "name.given": entry,
        "name.surname": entry,
        "name.suffix": entry,
        "other_names_used[alias-1].surname": entry,
        "residence_address.line1": entry,
        "residence_address.city": entry,
        "residence_address.state": entry,
        "residence_address.postal_code": entry,
        "residence_address.county": entry,
        "phone": entry,
        "email": entry,
    }


def test_the_firms_own_fields_are_never_copied():
    """Lead source is the firm's, and the date of birth is on no form."""
    debtor = debtor_from_client(a_client(), case=a_case()[0], filing_role="debtor_1")
    body = debtor_json(debtor)
    assert "date_of_birth" not in body
    assert "lead_source" not in body
    assert set(COPIED_FIELDS) >= {
        key for key in body if key in CLIENT_BODY and key != "date_of_birth"
    }


def test_the_copy_is_a_record_the_questionnaire_can_send_back():
    """What the copy stores must pass the debtor's own write rule, or the
    first autosave after opening a case would 400."""
    debtor = debtor_from_client(a_client(), case=a_case()[0], filing_role="debtor_1")
    echoed = debtor_json(debtor)
    parse_debtor(echoed)


def test_a_questionnaire_save_keeps_the_link_whatever_its_body_says():
    debtor = debtor_from_client(a_client(), case=a_case()[0], filing_role="debtor_1")
    draft = parse_debtor(
        {
            "client_id": "someone-else",
            "name": {"given": "Typed"},
            "provenance": {"name.given": {"source": "staff_typed"}},
        }
    )
    saved = replace_debtor(debtor, draft)
    assert saved.client_id == debtor.client_id
    assert saved.case_created_at == debtor.case_created_at
    assert saved.name.given == "Typed"


def test_client_id_is_server_owned_so_an_echo_needs_no_provenance():
    parse_debtor({"client_id": "c-1"})


def test_a_link_and_an_index_sort_key_travel_together():
    draft = parse_debtor({})
    with pytest.raises(ValueError, match="together"):
        create_debtor(draft, case_id="k", filing_role="debtor_1", client_id="c-1")


def test_re_linking_changes_the_client_and_never_the_copied_fields():
    debtor = debtor_from_client(a_client(), case=a_case()[0], filing_role="debtor_2")
    relinked = link_client(
        debtor, client_id="c-other", case_created_at=debtor.case_created_at or ""
    )
    assert relinked.client_id == "c-other"
    assert relinked.name == debtor.name
    assert relinked.provenance == debtor.provenance
    assert relinked.id == debtor.id


# ── differs_from_client ─────────────────────────────────────────────


def test_a_fresh_copy_differs_in_nothing():
    client = a_client()
    debtor = debtor_from_client(client, case=a_case()[0], filing_role="debtor_1")
    assert differs_from_client(debtor, client) == []


def test_divergence_is_listed_by_field_path_in_both_directions():
    client = a_client()
    debtor = debtor_from_client(client, case=a_case()[0], filing_role="debtor_1")
    # The case's copy was corrected; the client later gained a mobile number
    # and moved counties.
    debtor = replace(debtor, phone="555-0199")
    client = replace(
        client,
        mobile="555-0111",
        residence_address=replace(client.residence_address, county="Cook"),
    )
    assert differs_from_client(debtor, client) == [
        "mobile",
        "phone",
        "residence_address.county",
    ]


def test_an_alias_is_compared_by_its_id_not_its_position():
    client = a_client(
        other_names_used=[
            {"id": "alias-1", "surname": "Sample"},
            {"id": "alias-2", "surname": "Other"},
        ]
    )
    debtor = debtor_from_client(client, case=a_case()[0], filing_role="debtor_1")
    reordered = replace(
        client, other_names_used=tuple(reversed(client.other_names_used))
    )
    assert differs_from_client(debtor, reordered) == []
    dropped = replace(client, other_names_used=client.other_names_used[:1])
    assert differs_from_client(debtor, dropped) == [
        "other_names_used[alias-2].surname"
    ]


def test_the_firms_own_fields_never_count_as_divergence():
    client = a_client()
    debtor = debtor_from_client(client, case=a_case()[0], filing_role="debtor_1")
    assert differs_from_client(debtor, replace(client, lead_source="Web")) == []


def test_differs_from_client_is_served_only_when_computed():
    client = a_client()
    debtor = debtor_from_client(client, case=a_case()[0], filing_role="debtor_1")
    assert "differs_from_client" not in debtor_json(debtor)
    assert debtor_json(debtor, differs_from_client=[])["differs_from_client"] == []
    assert debtor_json(debtor)["client_id"] == client.id


# ── The debtor item feeds `by-client` ───────────────────────────────


def test_a_linked_debtor_is_an_entry_in_the_by_client_index():
    client = a_client()
    case, _ = a_case()
    debtor = debtor_from_client(client, case=case, filing_role="debtor_1")
    item = debtor_item(debtor)
    assert item["GSI3PK"] == f"CLIENT#{client.id}"
    # The same "<createdAt>#<id>" value the firm and assignee listings sort
    # on, so every case list orders one way.
    assert item["GSI3SK"] == case_item(case)["GSI1SK"]
    assert item["clientId"] == client.id
    # Beside the body, never in it: nobody sent it.
    assert "client_id" not in item["body"]  # type: ignore[operator]
    assert debtor_from_item(item) == debtor


def test_an_unlinked_debtor_is_not_in_the_index():
    debtor = create_debtor(
        parse_debtor({}), case_id="k-1", filing_role="non_filing_spouse"
    )
    item = debtor_item(debtor)
    assert "GSI3PK" not in item
    assert "GSI3SK" not in item
    assert debtor_from_item(item).client_id is None


# ── MemoryCaseStore ─────────────────────────────────────────────────


def test_the_case_its_assignment_and_its_debtors_are_written_together():
    debtors = MemoryDebtorStore()
    store = MemoryCaseStore(debtor_store=debtors)
    case, assignment = a_case()
    copy = debtor_from_client(a_client(), case=case, filing_role="debtor_1")
    store.create(case, assignment, [copy])
    assert debtors.get(case.id, filing_role="debtor_1") == copy
    assert store.get(case.id, accessor=accessor(ALICE)) == case


def test_a_debtor_for_another_case_refuses_the_whole_write():
    store = MemoryCaseStore()
    case, assignment = a_case()
    other, _ = a_case()
    stray = debtor_from_client(a_client(), case=other, filing_role="debtor_1")
    with pytest.raises(RuntimeError):
        store.create(case, assignment, [stray])
    assert case.id not in store.cases


def test_a_clients_cases_are_the_ones_the_caller_may_see_newest_first():
    debtors = MemoryDebtorStore()
    store = MemoryCaseStore(debtor_store=debtors)
    client = a_client()
    older, older_assignment = a_case(created_by=ALICE)
    newer, newer_assignment = a_case(created_by=DANA)
    store.create(
        older,
        older_assignment,
        [debtor_from_client(client, case=older, filing_role="debtor_2")],
    )
    store.create(
        newer,
        newer_assignment,
        [debtor_from_client(client, case=newer, filing_role="debtor_1")],
    )

    admin = store.list_for_client(client.id, accessor=accessor(ALICE, is_admin=True))
    assert [(entry.case.id, entry.filing_role) for entry in admin] == [
        (newer.id, "debtor_1"),
        (older.id, "debtor_2"),
    ]
    # Dana is linked to the newer case only: the older one is not listed,
    # and nothing says it exists.
    dana = store.list_for_client(client.id, accessor=accessor(DANA))
    assert [entry.case.id for entry in dana] == [newer.id]
    # Another firm sees nothing, even holding the id.
    assert store.list_for_client(client.id, accessor=accessor(BOB, FIRM_B)) == ()


# ── DynamoDbCaseStore, over a recording transport ───────────────────


class FakeDynamoDb:
    """Records calls and replays canned responses; conditions are not
    simulated — the integration tier runs the real table."""

    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.responses: dict[str, list[dict[str, Any]]] = {}

    def _record(self, name: str, kwargs: dict[str, Any]) -> Any:
        self.calls.append((name, kwargs))
        queue = self.responses.get(name) or [{}]
        return queue.pop(0) if len(queue) > 1 else queue[0]

    def transact_write_items(self, **kwargs: Any) -> Any:
        return self._record("transact_write_items", kwargs)

    def query(self, **kwargs: Any) -> Any:
        return self._record("query", kwargs)

    def batch_get_item(self, **kwargs: Any) -> Any:
        return self._record("batch_get_item", kwargs)


def dynamo_store(monkeypatch, fake: FakeDynamoDb) -> DynamoDbCaseStore:
    monkeypatch.setattr(aws_case_store.boto3, "client", lambda _service: fake)
    return DynamoDbCaseStore("insolvia-test-cases")


def test_one_transaction_writes_the_case_its_assignment_and_its_debtors(monkeypatch):
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    case, assignment = a_case()
    client_1, client_2 = a_client(), a_client(name={"given": "Sam"})
    copies = [
        debtor_from_client(client_1, case=case, filing_role="debtor_1"),
        debtor_from_client(client_2, case=case, filing_role="debtor_2"),
    ]

    store.create(case, assignment, copies)

    [(name, kwargs)] = fake.calls
    assert name == "transact_write_items"
    puts = [item["Put"] for item in kwargs["TransactItems"]]
    assert [put["Item"]["SK"]["S"] for put in puts] == [
        "META",
        f"ASSIGNEE#{ALICE}",
        "DEBTOR#debtor_1",
        "DEBTOR#debtor_2",
    ]
    assert puts[2]["Item"]["GSI3PK"] == {"S": f"CLIENT#{client_1.id}"}
    assert puts[3]["Item"]["GSI3PK"] == {"S": f"CLIENT#{client_2.id}"}
    # A role exists at most once however it was written.
    assert all(
        put["ConditionExpression"] == "attribute_not_exists(SK)" for put in puts[1:]
    )


def test_the_client_listing_reads_the_index_then_decides_per_case(monkeypatch):
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    client = a_client()
    mine, mine_assignment = a_case(created_by=DANA)
    theirs, _ = a_case(created_by=ALICE)
    fake.responses["query"] = [
        {
            "Items": [
                to_attributes(
                    debtor_item(
                        debtor_from_client(client, case=case, filing_role=role)
                    )
                )
                for case, role in ((mine, "debtor_1"), (theirs, "debtor_2"))
            ]
        }
    ]
    fake.responses["batch_get_item"] = [
        {
            "Responses": {
                "insolvia-test-cases": [
                    to_attributes(case_item(mine)),
                    to_attributes(assignment_item(mine_assignment)),
                    to_attributes(case_item(theirs)),
                ]
            }
        }
    ]

    listed = store.list_for_client(client.id, accessor=accessor(DANA))

    assert [(entry.case.id, entry.filing_role) for entry in listed] == [
        (mine.id, "debtor_1")
    ]
    query = next(kwargs for name, kwargs in fake.calls if name == "query")
    assert query["IndexName"] == "by-client"
    assert query["ExpressionAttributeValues"] == {
        ":client": {"S": f"CLIENT#{client.id}"}
    }
    assert query["ScanIndexForward"] is False
    batch = next(kwargs for name, kwargs in fake.calls if name == "batch_get_item")
    keys = batch["RequestItems"]["insolvia-test-cases"]["Keys"]
    # Each case's META and the caller's own assignment row — nobody else's.
    assert {key["SK"]["S"] for key in keys} == {"META", f"ASSIGNEE#{DANA}"}


def test_a_client_with_no_cases_costs_one_query(monkeypatch):
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    assert store.list_for_client("c-none", accessor=accessor(ALICE)) == ()
    assert [name for name, _ in fake.calls] == ["query"]


def test_the_address_county_is_part_of_the_copy():
    """B101 line 5's County box: the one Address member only a client
    supplies today, so the one most likely to be dropped on the way."""
    client = a_client()
    debtor = debtor_from_client(client, case=a_case()[0], filing_role="debtor_1")
    assert debtor.residence_address == Address(
        line1="1 Example St",
        city="Springfield",
        state="IL",
        postal_code="62701",
        county="Sangamon",
    )
