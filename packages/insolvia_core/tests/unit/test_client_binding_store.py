"""Both ClientBindingStore implementations (ADR 0023).

The MEMORY store is held to the conditions the DynamoDB transaction enforces
— one live binding per subject per firm, one holder per role per case — so
that a route test running against it proves something about the real one.

The DYNAMODB store is tested through a faked boto3 client, monkeypatched at
the transport as test_firm_store.py does: what it buys is the transaction's
SHAPE — both tables, the conditions, the prefix — because the conditions
themselves only a real table can evaluate.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace
from typing import Any

import pytest
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws import client_binding_store as aws_store
from insolvia_core.adapters.aws.client_binding_store import DynamoDbClientBindingStore
from insolvia_core.adapters.aws.dynamo import to_attributes
from insolvia_core.adapters.memory.client_binding_store import (
    MemoryClientBindingStore,
)
from insolvia_core.clients import (
    ACTIVE,
    INVITED,
    REVOKED,
    ClientBinding,
    binding_item,
    mirror_item,
    revoke,
)
from insolvia_core.errors import ConflictError

FIRM = "00000000-0000-4000-8000-00000000f18a"
OTHER_FIRM = "00000000-0000-4000-8000-00000000f18b"
CASE = "00000000-0000-4000-8000-0000000ca5e1"
STAFF = "00000000-0000-4000-8000-00000000a11c"
PAT = "00000000-0000-4000-8000-0000000000a1"
SAM = "00000000-0000-4000-8000-0000000000a2"


def binding(
    subject: str = PAT,
    roles: tuple[str, ...] = ("debtor_1",),
    status: str = INVITED,
    firm_id: str = FIRM,
) -> ClientBinding:
    return ClientBinding(
        firm_id=firm_id,
        case_id=CASE,
        subject=subject,
        email=f"{subject[-2:]}@example.test",
        display_name="Pat Example",
        roles=roles,
        status=status,
        invited_by=STAFF,
        created_at="2026-09-26T00:00:00.000000Z",
        updated_at="2026-09-26T00:00:00.000000Z",
    )


# ── Memory ──────────────────────────────────────────────────────


def test_two_bindings_cannot_both_hold_debtor_2():
    """The ADR's rule under a race: the second writer did not see the first
    and so narrowed nobody, and the claim refuses it."""
    store = MemoryClientBindingStore()
    store.bind(binding(PAT, roles=("debtor_2",)), narrowed=())

    with pytest.raises(ConflictError):
        store.bind(binding(SAM, roles=("debtor_2",)), narrowed=())

    assert store.claims[(CASE, "debtor_2")] == PAT
    assert store.get(FIRM, SAM) is None


def test_a_narrowing_moves_the_role_and_rewrites_the_first_binding():
    store = MemoryClientBindingStore()
    first = binding(PAT, roles=("debtor_1", "debtor_2"), status=ACTIVE)
    store.bind(first, narrowed=())

    store.bind(
        binding(SAM, roles=("debtor_2",)),
        narrowed=(replace(first, roles=("debtor_1",)),),
    )

    assert store.claims[(CASE, "debtor_1")] == PAT
    assert store.claims[(CASE, "debtor_2")] == SAM
    assert store.get(FIRM, PAT).roles == ("debtor_1",)  # type: ignore[union-attr]
    assert [b.subject for b in store.list_for_case(CASE)] == [PAT, SAM]


def test_a_live_subject_cannot_be_bound_twice_but_a_revoked_one_can():
    store = MemoryClientBindingStore()
    store.bind(binding(), narrowed=())
    with pytest.raises(ConflictError):
        store.bind(binding(), narrowed=())

    store.update(revoke(binding()))
    store.bind(binding(), narrowed=())

    assert store.get(FIRM, PAT).status == INVITED  # type: ignore[union-attr]


def test_revoking_releases_the_role_for_someone_else():
    store = MemoryClientBindingStore()
    store.bind(binding(PAT, roles=("debtor_2",)), narrowed=())

    store.update(revoke(binding(PAT, roles=("debtor_2",))))
    store.bind(binding(SAM, roles=("debtor_2",)), narrowed=())

    assert store.claims[(CASE, "debtor_2")] == SAM


def test_updating_a_binding_that_does_not_exist_returns_none():
    assert MemoryClientBindingStore().update(binding(status=REVOKED)) is None


def test_a_subject_in_two_firms_raises_rather_than_choosing():
    store = MemoryClientBindingStore()
    store.bind(binding(firm_id=FIRM), narrowed=())
    store.bind(binding(firm_id=OTHER_FIRM), narrowed=())

    with pytest.raises(RuntimeError):
        store.find(PAT)


def test_the_email_lookup_is_firm_scoped():
    store = MemoryClientBindingStore()
    store.bind(binding(firm_id=OTHER_FIRM), narrowed=())

    assert store.find_by_email(FIRM, "a1@example.test") is None
    assert store.find_by_email(OTHER_FIRM, "A1@example.test") is not None


# ── DynamoDB ────────────────────────────────────────────────────


class FakeDynamoDb:
    """Records calls and replays canned responses; conditions are not
    simulated."""

    def __init__(self, responses: dict[str, Any] | None = None) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.responses = responses or {}
        self.raises: Exception | None = None

    def _record(self, name: str, kwargs: dict[str, Any]) -> Any:
        self.calls.append((name, kwargs))
        if self.raises is not None:
            raise self.raises
        return self.responses.get(name, {})

    def transact_write_items(self, **kwargs: Any) -> Any:
        return self._record("transact_write_items", kwargs)

    def get_item(self, **kwargs: Any) -> Any:
        return self._record("get_item", kwargs)

    def query(self, **kwargs: Any) -> Any:
        return self._record("query", kwargs)


def dynamo(monkeypatch, fake: FakeDynamoDb) -> DynamoDbClientBindingStore:
    monkeypatch.setattr(aws_store.boto3, "client", lambda _service: fake)
    return DynamoDbClientBindingStore("insolvia-test-firms", "insolvia-test-cases")


def test_binding_is_one_transaction_across_both_tables(monkeypatch):
    fake = FakeDynamoDb()
    first = binding(PAT, roles=("debtor_1", "debtor_2"), status=ACTIVE)

    dynamo(monkeypatch, fake).bind(
        binding(SAM, roles=("debtor_2",)),
        narrowed=(replace(first, roles=("debtor_1",)),),
    )

    [(name, kwargs)] = fake.calls
    puts = [item["Put"] for item in kwargs["TransactItems"]]
    assert name == "transact_write_items"
    assert [(p["TableName"], p["Item"]["SK"]["S"]) for p in puts] == [
        ("insolvia-test-firms", f"CLIENT#{SAM}"),
        ("insolvia-test-cases", f"CLIENT#{SAM}"),
        ("insolvia-test-cases", "CLIENTROLE#debtor_2"),
        ("insolvia-test-firms", f"CLIENT#{PAT}"),
        ("insolvia-test-cases", f"CLIENT#{PAT}"),
    ]
    claim = puts[2]
    assert claim["ConditionExpression"] == (
        "attribute_not_exists(SK) OR #subject IN (:h0, :h1)"
    )
    assert claim["ExpressionAttributeValues"] == {
        ":h0": {"S": SAM},
        ":h1": {"S": PAT},
    }
    # Roles are stored as a list, and survive the wire format.
    assert puts[3]["Item"]["roles"] == {"L": [{"S": "debtor_1"}]}


def test_a_cancelled_transaction_is_a_conflict(monkeypatch):
    fake = FakeDynamoDb()
    fake.raises = ClientError(
        {"Error": {"Code": "TransactionCanceledException"}}, "TransactWriteItems"
    )

    with pytest.raises(ConflictError):
        dynamo(monkeypatch, fake).bind(binding(), narrowed=())


def test_revoking_releases_only_claims_this_subject_holds(monkeypatch):
    fake = FakeDynamoDb()

    dynamo(monkeypatch, fake).update(revoke(binding(roles=("debtor_1", "debtor_2"))))

    [(_, kwargs)] = fake.calls
    deletes = [item["Delete"] for item in kwargs["TransactItems"] if "Delete" in item]
    assert [d["Key"]["SK"]["S"] for d in deletes] == [
        "CLIENTROLE#debtor_1",
        "CLIENTROLE#debtor_2",
    ]
    assert all(d["ExpressionAttributeValues"][":subject"]["S"] == PAT for d in deletes)


def test_an_update_whose_binding_vanished_returns_none(monkeypatch):
    fake = FakeDynamoDb()
    fake.raises = ClientError(
        {
            "Error": {"Code": "TransactionCanceledException"},
            "CancellationReasons": [
                {"Code": "ConditionalCheckFailed"},
                {"Code": "None"},
            ],
        },
        "TransactWriteItems",
    )

    assert dynamo(monkeypatch, fake).update(binding(status=ACTIVE)) is None


def test_find_reads_the_client_key_and_asks_for_two_rows(monkeypatch):
    fake = FakeDynamoDb({"query": {"Items": [to_attributes(binding_item(binding()))]}})

    found = dynamo(monkeypatch, fake).find(PAT)

    [(_, kwargs)] = fake.calls
    assert kwargs["IndexName"] == "by-subject"
    assert kwargs["ExpressionAttributeValues"] == {":subject": {"S": f"CLIENT#{PAT}"}}
    assert kwargs["Limit"] == 2
    assert found == binding()


def test_the_case_listing_reads_client_rows_and_never_the_role_claims(monkeypatch):
    fake = FakeDynamoDb({"query": {"Items": [to_attributes(mirror_item(binding()))]}})

    listed = dynamo(monkeypatch, fake).list_for_case(CASE)

    [(_, kwargs)] = fake.calls
    assert kwargs["TableName"] == "insolvia-test-cases"
    assert kwargs["ExpressionAttributeValues"][":prefix"] == {"S": "CLIENT#"}
    assert kwargs["ConsistentRead"] is True
    assert listed == (binding(),)
