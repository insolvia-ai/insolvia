"""Both FirmStore implementations, held to the same rules.

Two things are going on here and they are worth separating.

The MEMORY store is tested for the conditions DynamoDB enforces in the table —
no overwrite on create, firm-scoped reads, a raise on a subject in two firms.
A suite running against the looser of two stores proves nothing about the one
holding the data, and none of these can be observed from a route.

The DYNAMODB store is tested through a faked boto3 client, monkeypatched at the
transport exactly as test_mailer_client.py patches urlopen. What that buys is
the attribute conversion — the one piece of this adapter with a silent failure
mode, where a boolean stored as a number turns every admin in the system into a
non-admin and nothing anywhere raises.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace
from typing import Any

import pytest
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws import firm_store as aws_firm_store
from insolvia_core.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.firms import (
    ADD_EDIT,
    CASES,
    Firm,
    FirmUser,
    default_permissions,
)

FIRM_ID = "00000000-0000-4000-8000-00000000f18a"
OTHER_FIRM_ID = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"


def firm(firm_id: str = FIRM_ID, name: str = "Example & Partners") -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def user(
    subject: str = ALICE,
    firm_id: str = FIRM_ID,
    display_name: str = "Alice Attorney",
    **overrides: object,
) -> FirmUser:
    defaults: dict[str, object] = {
        "firm_id": firm_id,
        "subject": subject,
        "email": f"{display_name.split()[0].lower()}@example.test",
        "display_name": display_name,
        "role": "attorney",
        "is_admin": False,
        "access_all_cases": False,
        "permissions": default_permissions("attorney"),
        "status": "active",
        "created_at": "2026-01-01T00:00:00.000Z",
        "updated_at": "2026-01-01T00:00:00.000Z",
    }
    return FirmUser(**{**defaults, **overrides})  # type: ignore[arg-type]


# ── The memory store ────────────────────────────────────────────────


def test_adding_the_same_person_twice_is_refused():
    """attribute_not_exists(SK), as a dict. An overwrite would reset a
    colleague's permissions to whatever the caller sent, while the caller
    believes they are adding somebody new."""
    store = MemoryFirmStore()
    store.add_user(user())
    with pytest.raises(RuntimeError):
        store.add_user(user(display_name="Alice Impostor"))
    assert store.get_user(FIRM_ID, ALICE).display_name == "Alice Attorney"


def test_a_user_is_read_within_their_firm():
    """The key is (firm, subject), as it is in the table. If it were the
    subject alone, an admin of one firm could read a user of another by
    knowing their Cognito sub."""
    store = MemoryFirmStore()
    store.add_user(user())
    assert store.get_user(FIRM_ID, ALICE) is not None
    assert store.get_user(OTHER_FIRM_ID, ALICE) is None


def test_finding_a_subject_in_two_firms_raises_rather_than_guessing():
    """One person, one firm is an application invariant — nothing in the key
    schema enforces it, because a DynamoDB condition cannot span partitions.
    Picking one would make somebody's tenancy depend on index ordering, and it
    would be stable enough in testing to look correct."""
    store = MemoryFirmStore()
    store.add_user(user())
    store.add_user(user(firm_id=OTHER_FIRM_ID))
    with pytest.raises(RuntimeError):
        store.find_user(ALICE)


def test_finding_an_unprovisioned_subject_is_none_not_an_error():
    """Authenticated but not in any firm. It is a real state — a Cognito user
    exists before anyone attaches them to a firm — and resolution turns it into
    a 403, so the store must not raise on it."""
    assert MemoryFirmStore().find_user(ALICE) is None


def test_a_staff_list_is_one_firm_ordered_by_display_name():
    store = MemoryFirmStore()
    store.add_user(user(subject=BOB, display_name="Bob Paralegal"))
    store.add_user(user())
    store.add_user(user(subject=BOB, firm_id=OTHER_FIRM_ID, display_name="Aaron Other"))

    listed = store.list_users(FIRM_ID)
    assert [person.display_name for person in listed] == [
        "Alice Attorney",
        "Bob Paralegal",
    ]


def test_updating_someone_who_is_not_there_is_none():
    """The window between an administration route reading a user and writing
    them back. If a removal lands in between, the write must not resurrect the
    row — None rather than an exception, so the route answers the same 404 a
    foreign subject gets."""
    store = MemoryFirmStore()
    assert store.update_user(user()) is None
    assert store.users == {}


def test_updating_across_firms_does_not_move_someone():
    """A PATCH carrying another firm's id must not create a row there. The
    DynamoDB adapter's `firmId = :firm` condition says the same thing from the
    other side; without this the memory store would happily insert."""
    store = MemoryFirmStore()
    store.add_user(user())
    moved = replace(user(), firm_id=OTHER_FIRM_ID)
    assert store.update_user(moved) is None
    assert store.get_user(OTHER_FIRM_ID, ALICE) is None


def test_removing_someone_twice_reports_the_truth():
    store = MemoryFirmStore()
    store.add_user(user())
    assert store.remove_user(FIRM_ID, ALICE) is True
    assert store.remove_user(FIRM_ID, ALICE) is False


# ── The DynamoDB store ──────────────────────────────────────────────


class FakeDynamoDb:
    """Enough of the DynamoDB client to observe what the adapter sends.

    Not a general fake: it records calls and replays canned responses. The
    conditions are NOT simulated — that is what the real-table probe is for.
    """

    def __init__(self, responses: dict[str, Any] | None = None) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.responses = responses or {}
        self.raises: Exception | None = None

    def _record(self, name: str, kwargs: dict[str, Any]) -> Any:
        self.calls.append((name, kwargs))
        if self.raises is not None:
            raise self.raises
        return self.responses.get(name, {})

    def put_item(self, **kwargs: Any) -> Any:
        return self._record("put_item", kwargs)

    def get_item(self, **kwargs: Any) -> Any:
        return self._record("get_item", kwargs)

    def query(self, **kwargs: Any) -> Any:
        return self._record("query", kwargs)

    def scan(self, **kwargs: Any) -> Any:
        return self._record("scan", kwargs)

    def delete_item(self, **kwargs: Any) -> Any:
        return self._record("delete_item", kwargs)


def dynamo_store(monkeypatch, fake: FakeDynamoDb) -> DynamoDbFirmStore:
    monkeypatch.setattr(aws_firm_store.boto3, "client", lambda _service: fake)
    return DynamoDbFirmStore("insolvia-test-firms")


def conditional_check_failed() -> ClientError:
    return ClientError(
        {"Error": {"Code": "ConditionalCheckFailedException", "Message": "no"}},
        "PutItem",
    )


def test_a_boolean_is_written_as_bool_not_as_a_number(monkeypatch):
    """THE SILENT FAILURE THIS FILE EXISTS FOR.

    In Python `True` is an instance of `int`. A converter that checked int
    before bool would store `isAdmin` as {"N": "1"}, DynamoDB would accept it,
    and `firm_user_from_item` — which reads `item["isAdmin"] is True` — would
    return False. Every admin in the system would quietly stop being one, with
    nothing raising anywhere and the console showing a plausible-looking 1.
    """
    fake = FakeDynamoDb()
    dynamo_store(monkeypatch, fake).add_user(user(is_admin=True))

    item = fake.calls[0][1]["Item"]
    assert item["isAdmin"] == {"BOOL": True}
    assert item["accessAllCases"] == {"BOOL": False}


def test_the_permission_map_is_written_as_a_map(monkeypatch):
    fake = FakeDynamoDb()
    dynamo_store(monkeypatch, fake).add_user(user())

    permissions = fake.calls[0][1]["Item"]["permissions"]["M"]
    assert permissions[CASES] == {"S": ADD_EDIT}


def test_an_item_round_trips_through_the_wire_format(monkeypatch):
    """Write it, hand the recorded attribute values back as a GetItem
    response, and require the same FirmUser out. This is the pair of
    converters checked against each other rather than against my reading of
    the DynamoDB docs."""
    original = user(is_admin=True, access_all_cases=True)
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    store.add_user(original)

    fake.responses["get_item"] = {"Item": fake.calls[0][1]["Item"]}
    assert store.get_user(FIRM_ID, ALICE) == original


def test_adding_a_user_conditions_on_the_sort_key_not_the_partition(monkeypatch):
    """attribute_not_exists(PK) would refuse every user after the first: PK is
    the firm, and it exists by the time anyone is added to it."""
    fake = FakeDynamoDb()
    dynamo_store(monkeypatch, fake).add_user(user())
    assert fake.calls[0][1]["ConditionExpression"] == "attribute_not_exists(SK)"


def test_finding_a_subject_asks_for_two_rows(monkeypatch):
    """Limit=2, not 1. One row is the answer; two is the invariant violation
    the port requires a raise on, and Limit=1 would make a duplicate
    indistinguishable from the healthy case."""
    fake = FakeDynamoDb()
    dynamo_store(monkeypatch, fake).find_user(ALICE)

    _, kwargs = fake.calls[0]
    assert kwargs["IndexName"] == "by-subject"
    assert kwargs["Limit"] == 2
    assert kwargs["ExpressionAttributeValues"][":subject"] == {"S": f"USER#{ALICE}"}
    # No ConsistentRead: a GSI cannot serve one, and passing it is an error
    # rather than a no-op.
    assert "ConsistentRead" not in kwargs


def test_two_rows_for_one_subject_raise(monkeypatch):
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    fake.responses["query"] = {"Items": [{"subject": {"S": ALICE}}] * 2}
    with pytest.raises(RuntimeError):
        store.find_user(ALICE)


def test_a_staff_list_follows_every_page(monkeypatch):
    """A single Query returns at most 1 MB. Without the loop a firm admin
    would see eleven of their twelve colleagues and nothing anywhere would say
    so — which is why this is a paging test rather than an ordering one."""
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    first_page = _written_item(
        monkeypatch, user(display_name="Bob Paralegal", subject=BOB)
    )
    second_page = _written_item(monkeypatch, user())

    pages = [
        {
            "Items": [first_page],
            "LastEvaluatedKey": {"PK": {"S": "x"}, "SK": {"S": "y"}},
        },
        {"Items": [second_page]},
    ]

    def query(**kwargs: Any) -> Any:
        fake.calls.append(("query", kwargs))
        return pages.pop(0)

    fake.query = query  # type: ignore[method-assign]
    listed = store.list_users(FIRM_ID)

    assert len(fake.calls) == 2
    assert "ExclusiveStartKey" in fake.calls[1][1]
    # And the two pages are merged and ordered together, not per page.
    assert [person.display_name for person in listed] == [
        "Alice Attorney",
        "Bob Paralegal",
    ]


def test_a_staff_list_excludes_the_firms_own_meta_item(monkeypatch):
    fake = FakeDynamoDb()
    dynamo_store(monkeypatch, fake).list_users(FIRM_ID)
    _, kwargs = fake.calls[0]
    assert kwargs["KeyConditionExpression"] == "PK = :firm AND begins_with(SK, :prefix)"
    assert kwargs["ExpressionAttributeValues"][":prefix"] == {"S": "USER#"}


def test_an_update_is_scoped_to_the_firm(monkeypatch):
    fake = FakeDynamoDb()
    dynamo_store(monkeypatch, fake).update_user(user())
    _, kwargs = fake.calls[0]
    assert kwargs["ConditionExpression"] == "attribute_exists(SK) AND firmId = :firm"
    assert kwargs["ExpressionAttributeValues"][":firm"] == {"S": FIRM_ID}


def test_a_refused_update_is_none_not_an_exception(monkeypatch):
    """So the route can answer the same 404 a foreign subject gets, rather
    than a 500 for a race it is expected to lose sometimes."""
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    fake.raises = conditional_check_failed()
    assert store.update_user(user()) is None


def test_a_refused_delete_is_false_not_an_exception(monkeypatch):
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    fake.raises = conditional_check_failed()
    assert store.remove_user(FIRM_ID, ALICE) is False


def test_a_real_error_still_propagates(monkeypatch):
    """The ClientError branch matches ONE code. Throttling, an expired
    credential or a missing table must not read as "the row wasn't there"."""
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    fake.raises = ClientError(
        {"Error": {"Code": "ProvisionedThroughputExceededException"}}, "PutItem"
    )
    with pytest.raises(ClientError):
        store.update_user(user())


def _written_item(monkeypatch, person: FirmUser) -> dict[str, Any]:
    """The wire-format item the adapter would write for `person` — used to
    build query responses out of the adapter's own converter rather than a
    hand-typed literal that could drift from it."""
    recorder = FakeDynamoDb()
    dynamo_store(monkeypatch, recorder).add_user(person)
    return dict(recorder.calls[0][1]["Item"])


# ── Firms cross-tenant (#212): list and update, both stores ─────────


def test_the_memory_firm_list_is_ordered_by_name_then_id():
    store = MemoryFirmStore()
    store.create_firm(firm(OTHER_FIRM_ID, "Other Firm LLP"))
    store.create_firm(firm(FIRM_ID, "Example & Partners"))
    assert [f.name for f in store.list_firms()] == [
        "Example & Partners",
        "Other Firm LLP",
    ]


def test_updating_a_firm_that_is_not_there_is_none():
    """A deletion racing an update must not resurrect the firm from the
    caller's stale read — None, exactly as update_user answers."""
    store = MemoryFirmStore()
    assert store.update_firm(firm()) is None
    assert store.firms == {}


def test_updating_a_firm_writes_it_back():
    from insolvia_core.firms import set_firm_status

    store = MemoryFirmStore()
    store.create_firm(firm())
    suspended = set_firm_status(firm(), "suspended")
    assert store.update_firm(suspended) == suspended
    assert store.get_firm(FIRM_ID).status == "suspended"


def _written_firm_item(monkeypatch, record: Firm) -> dict[str, Any]:
    """Wire-format via the adapter's own converter, same rule as
    _written_item above."""
    recorder = FakeDynamoDb()
    dynamo_store(monkeypatch, recorder).create_firm(record)
    return dict(recorder.calls[0][1]["Item"])


def test_the_scan_is_filtered_to_meta_and_paginates(monkeypatch):
    """Two promises in one call shape: only firm META items come back (the
    filter is what keeps users out of the firm list), and a page boundary
    does not silently truncate — the adapter must follow LastEvaluatedKey."""
    first = _written_firm_item(monkeypatch, firm(FIRM_ID, "Example & Partners"))
    second = _written_firm_item(monkeypatch, firm(OTHER_FIRM_ID, "Other Firm LLP"))

    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    pages = [
        {"Items": [second], "LastEvaluatedKey": {"PK": second["PK"]}},
        {"Items": [first]},
    ]
    fake.responses["scan"] = pages[0]

    original_record = fake._record

    def paged(name: str, kwargs: dict[str, Any]) -> Any:
        if name == "scan" and "ExclusiveStartKey" in kwargs:
            fake.responses["scan"] = pages[1]
        return original_record(name, kwargs)

    monkeypatch.setattr(fake, "_record", paged)

    listed = store.list_firms()

    scans = [call for call in fake.calls if call[0] == "scan"]
    assert len(scans) == 2
    assert scans[0][1]["FilterExpression"] == "SK = :meta"
    assert scans[1][1]["ExclusiveStartKey"] == {"PK": second["PK"]}
    # Sorted by name despite arriving in scan (hash) order.
    assert [f.name for f in listed] == ["Example & Partners", "Other Firm LLP"]


def test_a_refused_firm_update_is_none_not_an_exception(monkeypatch):
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    fake.raises = conditional_check_failed()
    assert store.update_firm(firm()) is None


def test_the_firm_update_is_conditioned_on_existence(monkeypatch):
    fake = FakeDynamoDb()
    dynamo_store(monkeypatch, fake).update_firm(firm())
    name, kwargs = fake.calls[0]
    assert name == "put_item"
    assert kwargs["ConditionExpression"] == "attribute_exists(PK)"


def test_firm_provenance_survives_the_wire_format(monkeypatch):
    """The sparse createdBy attributes through _to_attributes/_from_attributes
    — a provisioned firm must read back with its author, and a seeded one
    must not grow a fabricated author from the conversion."""
    from dataclasses import replace as dc_replace

    provisioned = dc_replace(
        firm(), created_by=ALICE, created_by_email="staff@example.test"
    )
    item = _written_firm_item(monkeypatch, provisioned)
    assert item["createdBy"] == {"S": ALICE}

    fake = FakeDynamoDb({"get_item": {"Item": item}})
    read_back = dynamo_store(monkeypatch, fake).get_firm(FIRM_ID)
    assert read_back == provisioned
