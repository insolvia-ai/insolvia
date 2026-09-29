"""The debtor screen's client acts (ADR 0022, PR 5): a copied field keeps its
`client` provenance until a human changes it, *re-copy from client*,
*update client from this case*, and the conditional write that holds "one
client, one role per case" on the link route.

The routes — who may, what a filed case refuses, what is logged — are
services/api's test_debtor_routes.py; this file pins the domain rules and
both debtor stores.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace
from typing import Any

import pytest
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws import debtor_store as aws_debtor_store
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.cases import create_case, parse_case_creation
from insolvia_core.debtors import (
    Venue,
    debtor_body,
    link_client,
    parse_debtor,
    replace_debtor,
    require_client_provenance_kept,
)
from insolvia_core.errors import ConflictError, FieldValidationError
from insolvia_core.fields import PersonName
from insolvia_core.firm_clients import (
    client_updated_from_debtor,
    create_firm_client,
    debtor_from_client,
    parse_firm_client,
    recopy_from_client,
)
from insolvia_core.provenance import ProvenanceEntry, provenance_json, value_at
from insolvia_core.tax_ids import TaxIdRef

FIRM_A = "00000000-0000-4000-8000-00000000f18a"
ALICE = "00000000-0000-4000-8000-00000000a11c"
TYPED = ProvenanceEntry(source="staff_typed")

CLIENT_BODY = {
    "name": {"given": "Jordan", "surname": "Example"},
    "other_names_used": [{"id": "alias-1", "surname": "Sample"}],
    "date_of_birth": "1980-02-29",
    "residence_address": {"line1": "1 Example St", "city": "Springfield"},
    "phone": "555-0100",
    "lead_source": "Referral",
}


def a_client(**overrides: Any):
    draft = parse_firm_client({**CLIENT_BODY, **overrides})
    return create_firm_client(draft, firm_id=FIRM_A, created_by=ALICE)


def a_case():
    case, _ = create_case(
        parse_case_creation(
            {"chapter": 7, "court": "flmb", "division": "tampa"},
            require_clients=False,
        ),
        firm_id=FIRM_A,
        created_by=ALICE,
    )
    return case


def a_copy(client=None, role="debtor_1"):
    return debtor_from_client(client or a_client(), case=a_case(), filing_role=role)


def as_sent(debtor, **changes: Any):
    """What the questionnaire sends for `debtor`: its body and provenance as
    JSON, with `changes` applied over both."""
    provenance = provenance_json(debtor.provenance)
    provenance.update(changes.pop("provenance", {}))
    return parse_debtor({**debtor_body(debtor), **changes, "provenance": provenance})


# ── value_at: a provenance path, resolved ───────────────────────────


@pytest.mark.parametrize(
    ("path", "expected"),
    [
        ("name.given", "Jordan"),
        ("other_names_used[alias-1].surname", "Sample"),
        ("other_names_used[no-such-row].surname", None),
        ("residence_address.state", None),
        ("phone", "555-0100"),
        ("not_a_field", None),
    ],
)
def test_a_path_resolves_to_the_value_it_names(path, expected):
    assert value_at(debtor_body(a_copy()), path) == expected


# ── A copied field keeps `client` provenance until a human changes it ─


def test_an_echo_of_the_stored_record_keeps_every_client_entry():
    stored = a_copy()
    require_client_provenance_kept(as_sent(stored), stored)


def test_a_changed_field_sent_as_typed_beside_untouched_copies_is_accepted():
    stored = a_copy()
    draft = as_sent(
        stored,
        phone="555-0199",
        provenance={"phone": {"source": "staff_typed"}},
    )
    require_client_provenance_kept(draft, stored)


def test_a_changed_field_that_still_claims_the_client_is_refused():
    stored = a_copy()
    draft = as_sent(stored, phone="555-0199")
    with pytest.raises(FieldValidationError) as refused:
        require_client_provenance_kept(draft, stored)
    assert list(refused.value.fields) == ["provenance.phone"]


def test_a_client_entry_the_stored_record_never_had_is_refused():
    stored = replace(a_copy(), email=None)
    client_id = stored.client_id
    draft = as_sent(
        stored,
        email="new@example.test",
        provenance={"email": {"source": "client", "client_id": client_id}},
    )
    with pytest.raises(FieldValidationError) as refused:
        require_client_provenance_kept(draft, stored)
    assert list(refused.value.fields) == ["provenance.email"]


def test_a_client_entry_naming_another_client_is_refused():
    stored = a_copy()
    draft = as_sent(
        stored, provenance={"phone": {"source": "client", "client_id": "someone"}}
    )
    with pytest.raises(FieldValidationError):
        require_client_provenance_kept(draft, stored)


def test_nothing_stored_means_no_client_entry_can_be_kept():
    draft = as_sent(a_copy())
    with pytest.raises(FieldValidationError):
        require_client_provenance_kept(draft, None)


def test_an_entry_for_a_field_now_empty_describes_nothing_and_passes():
    stored = a_copy()
    draft = as_sent(stored, phone=None)
    require_client_provenance_kept(draft, stored)


# ── Re-copy from client ─────────────────────────────────────────────


def diverged(stored):
    """`stored` with a retyped phone, a venue answer and a tax id — the
    case's own facts beside a copied one."""
    return replace(
        stored,
        phone="555-0199",
        venue=Venue(basis="lived_longest_180_days"),
        tax_id=TaxIdRef(kind="ssn", last_four="4321", ref="ref-1"),
        provenance={
            **stored.provenance,
            "phone": TYPED,
            "venue.basis": TYPED,
            "tax_id": TYPED,
        },
    )


def test_recopy_puts_the_clients_values_back_with_client_provenance():
    client = a_client()
    stored = diverged(a_copy(client))

    recopied = recopy_from_client(stored, client)

    assert recopied.phone == "555-0100"
    assert recopied.provenance["phone"] == ProvenanceEntry(
        source="client", client_id=client.id
    )


def test_recopy_keeps_the_cases_own_answers_and_their_provenance():
    client = a_client()
    stored = diverged(a_copy(client))

    recopied = recopy_from_client(stored, client)

    assert (recopied.id, recopied.created_at) == (stored.id, stored.created_at)
    assert recopied.venue == stored.venue
    assert recopied.tax_id == stored.tax_id
    assert recopied.provenance["venue.basis"] == TYPED
    assert recopied.provenance["tax_id"] == TYPED
    assert recopied.client_id == stored.client_id


def test_recopy_is_a_copy_of_the_record_not_a_merge_into_the_case():
    client = a_client()
    stored = replace(
        a_copy(client),
        email="typed@example.test",
        provenance={**a_copy(client).provenance, "email": TYPED},
    )

    recopied = recopy_from_client(stored, client)

    assert recopied.email is None
    assert "email" not in recopied.provenance


def test_recopy_follows_a_client_that_changed_since_the_copy():
    client = a_client()
    stored = a_copy(client)
    changed = replace(client, name=PersonName(given="Jo", surname="Example"))

    recopied = recopy_from_client(stored, changed)

    assert recopied.name.given == "Jo"


# ── Update client from this case ────────────────────────────────────


def test_update_client_takes_the_cases_copied_fields():
    client = a_client()
    stored = diverged(a_copy(client))

    updated = client_updated_from_debtor(client, stored)

    assert updated.phone == "555-0199"
    assert updated.id == client.id


def test_update_client_keeps_the_clients_own_fields():
    client = a_client()

    updated = client_updated_from_debtor(client, diverged(a_copy(client)))

    assert updated.date_of_birth == client.date_of_birth
    assert updated.lead_source == client.lead_source
    assert (updated.status, updated.created_by) == (client.status, client.created_by)


def test_update_client_refuses_to_leave_a_nameless_client():
    client = a_client()
    nameless = replace(a_copy(client), name=PersonName())
    with pytest.raises(FieldValidationError) as refused:
        client_updated_from_debtor(client, nameless)
    assert "name" in refused.value.fields


# ── One client, one role — the conditional write ────────────────────


def test_linking_into_an_empty_role_is_written():
    store = MemoryDebtorStore()
    assert store.link(a_copy(), create=True) == "written"


def test_a_create_that_lost_its_race_writes_nothing():
    store = MemoryDebtorStore()
    first = a_copy()
    store.link(first, create=True)
    rival = replace(a_copy(a_client()), case_id=first.case_id)
    assert store.link(rival, create=True) == "role_taken"
    assert store.get(first.case_id, filing_role="debtor_1") == first


def test_a_client_on_another_role_of_the_case_is_refused():
    store = MemoryDebtorStore()
    client = a_client()
    debtor_1 = a_copy(client)
    store.link(debtor_1, create=True)
    debtor_2 = replace(a_copy(client, role="debtor_2"), case_id=debtor_1.case_id)

    assert store.link(debtor_2, create=True) == "client_taken"
    assert store.get(debtor_1.case_id, filing_role="debtor_2") is None


def test_re_linking_a_role_to_the_client_it_already_holds_is_written():
    store = MemoryDebtorStore()
    debtor_1 = a_copy()
    store.link(debtor_1, create=True)
    assert store.link(debtor_1, create=False) == "written"


class FakeDynamoDb:
    """Records calls; raises what it is told to. Conditions are not
    simulated — the integration tier runs the real table."""

    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.raises: Exception | None = None

    def transact_write_items(self, **kwargs: Any) -> Any:
        self.calls.append(("transact_write_items", kwargs))
        if self.raises is not None:
            raise self.raises
        return {}


def dynamo(monkeypatch, fake: FakeDynamoDb) -> DynamoDbDebtorStore:
    monkeypatch.setattr(aws_debtor_store.boto3, "client", lambda _service: fake)
    return DynamoDbDebtorStore("insolvia-test-cases")


def cancelled(*codes: str) -> ClientError:
    return ClientError(
        {
            "Error": {"Code": "TransactionCanceledException"},
            "CancellationReasons": [{"Code": code} for code in codes],
        },
        "TransactWriteItems",
    )


def test_the_link_is_one_transaction_checking_every_other_role(monkeypatch):
    fake = FakeDynamoDb()
    debtor = a_copy(role="debtor_2")

    dynamo(monkeypatch, fake).link(debtor, create=True)

    [(name, kwargs)] = fake.calls
    put, *checks = kwargs["TransactItems"]
    assert name == "transact_write_items"
    assert put["Put"]["Item"]["SK"] == {"S": "DEBTOR#debtor_2"}
    assert put["Put"]["ConditionExpression"] == "attribute_not_exists(SK)"
    assert [check["ConditionCheck"]["Key"]["SK"]["S"] for check in checks] == [
        "DEBTOR#debtor_1",
        "DEBTOR#non_filing_spouse",
    ]
    for check in checks:
        assert check["ConditionCheck"]["ConditionExpression"] == (
            "attribute_not_exists(clientId) OR clientId <> :client"
        )
        assert check["ConditionCheck"]["ExpressionAttributeValues"] == {
            ":client": {"S": debtor.client_id}
        }


def test_moving_a_link_puts_without_a_role_condition(monkeypatch):
    fake = FakeDynamoDb()
    stored = a_copy()
    moved = link_client(stored, client_id="client-2", case_created_at="2026-01-01")

    dynamo(monkeypatch, fake).link(moved, create=False)

    [(_, kwargs)] = fake.calls
    assert "ConditionExpression" not in kwargs["TransactItems"][0]["Put"]


@pytest.mark.parametrize(
    ("codes", "outcome"),
    [
        (("ConditionalCheckFailed", "None", "None"), "role_taken"),
        (("None", "ConditionalCheckFailed", "None"), "client_taken"),
        (("None", "None", "ConditionalCheckFailed"), "client_taken"),
    ],
)
def test_a_cancelled_link_says_which_condition_refused_it(monkeypatch, codes, outcome):
    fake = FakeDynamoDb()
    fake.raises = cancelled(*codes)
    assert dynamo(monkeypatch, fake).link(a_copy(), create=True) == outcome


def test_a_link_cancelled_by_a_concurrent_write_is_a_conflict(monkeypatch):
    fake = FakeDynamoDb()
    fake.raises = cancelled("TransactionConflict", "None", "None")
    with pytest.raises(ConflictError):
        dynamo(monkeypatch, fake).link(a_copy(), create=True)


def test_replace_debtor_is_what_recopy_builds_on():
    """Guard for the one assumption `recopy_from_client` makes: a whole-record
    replace keeps the link, so a re-copy cannot unlink the debtor."""
    stored = a_copy()
    assert replace_debtor(stored, as_sent(stored)).client_id == stored.client_id
