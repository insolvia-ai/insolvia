"""Merging two firm clients (ADR 0022's PR 7): the re-point, the archive with
`merged_into`, the refusals, the claim that keeps two merges into and out of
one client from stranding a case, and the DynamoDB debtor store's half of it.

The route — who may, the status codes, the access log — is services/api's
test_firm_clients_routes.py; the firm store's transactions are pinned in
test_firm_store.py beside the rest of that adapter.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import asdict, replace
from typing import Any

import pytest
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws import debtor_store as aws_debtor_store
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.cases import create_case, parse_case_creation
from insolvia_core.client_merge import merge_clients
from insolvia_core.errors import ConflictError, FieldValidationError
from insolvia_core.firm_clients import (
    ARCHIVED,
    create_firm_client,
    debtor_from_client,
    firm_client_from_item,
    firm_client_item,
    firm_client_json,
    parse_client_merge,
    parse_firm_client,
)

FIRM = "00000000-0000-4000-8000-00000000f18a"
ALICE = "00000000-0000-4000-8000-00000000a11c"


class World:
    """One firm's directory and case table, composed as the API composes
    them: the case store and the debtor routes share one debtor store."""

    def __init__(self) -> None:
        self.firms = MemoryFirmStore()
        self.debtors = MemoryDebtorStore()
        self.cases = MemoryCaseStore(self.debtors)

    def client(self, given: str, surname: str = "Example"):
        client = create_firm_client(
            parse_firm_client(
                {
                    "name": {"given": given, "surname": surname},
                    "phone": "555-0100",
                    "residence_address": {"line1": "1 Example St"},
                }
            ),
            firm_id=FIRM,
            created_by=ALICE,
        )
        self.firms.create_client(client)
        return client

    def case_for(self, *clients):
        case, assignment = create_case(
            parse_case_creation(
                {"chapter": 7, "court": "flmb", "division": "tampa"},
                require_clients=False,
            ),
            firm_id=FIRM,
            created_by=ALICE,
        )
        roles = ("debtor_1", "debtor_2")
        debtors = [
            debtor_from_client(client, case=case, filing_role=role)
            for client, role in zip(clients, roles, strict=False)
        ]
        self.cases.create(case, assignment, debtors)
        return case

    def get(self, client):
        return self.firms.get_client(FIRM, client.id)

    def merge(self, merged, survivor):
        return merge_clients(
            self.firms,
            self.debtors,
            survivor=self.get(survivor),
            merged=self.get(merged),
        )


# ── What a merge does ───────────────────────────────────────────────


def test_every_case_of_the_merged_client_is_repointed_to_the_survivor():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordan", "Exampel")
    first, second = world.case_for(dupe), world.case_for(dupe)

    world.merge(dupe, keep)

    assert world.debtors.roles_for_client(dupe.id) == ()
    assert set(world.debtors.roles_for_client(keep.id)) == {
        (first.id, "debtor_1"),
        (second.id, "debtor_1"),
    }


def test_the_merged_client_is_archived_pointing_at_the_survivor():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")

    result = world.merge(dupe, keep)

    stored = world.get(dupe)
    assert stored.status == ARCHIVED
    assert stored.merged_into == keep.id
    assert result.merged == stored
    assert result.survivor.id == keep.id
    assert world.get(keep).status == "active"


def test_a_debtors_copied_fields_and_provenance_are_unchanged():
    """The copy is the case's: a filed petition still says what it said, and
    its provenance still truthfully says it was copied from the merged
    client — which is why that record is archived, never deleted."""
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy", "Other")
    case = world.case_for(dupe)
    before = world.debtors.get(case.id, filing_role="debtor_1")

    world.merge(dupe, keep)

    after = world.debtors.get(case.id, filing_role="debtor_1")
    assert after.client_id == keep.id
    moved = {"client_id", "updated_at"}
    assert {k: v for k, v in asdict(after).items() if k not in moved} == {
        k: v for k, v in asdict(before).items() if k not in moved
    }
    assert after.provenance["name.given"].client_id == dupe.id


def test_a_client_with_no_cases_merges():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    assert world.merge(dupe, keep).merged.merged_into == keep.id


def test_the_claim_is_released_on_both_rows():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    world.merge(dupe, keep)
    assert world.get(keep).merging_from is None
    assert world.get(dupe).merging_into is None


# ── What a merge refuses ────────────────────────────────────────────


def test_a_client_cannot_be_merged_into_itself():
    world = World()
    one = world.client("Jordan")
    with pytest.raises(FieldValidationError) as refused:
        world.merge(one, one)
    assert "merged_client_id" in refused.value.fields


@pytest.mark.parametrize("direction", ["same", "reverse", "onward"])
def test_a_merged_client_cannot_be_merged_again(direction):
    world = World()
    keep, dupe, third = world.client("A"), world.client("B"), world.client("C")
    world.merge(dupe, keep)
    merged, survivor = {
        "same": (dupe, keep),
        "reverse": (keep, dupe),
        "onward": (dupe, third),
    }[direction]
    with pytest.raises(ConflictError):
        world.merge(merged, survivor)


def test_a_merged_client_cannot_be_opened_for_a_new_case():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    world.merge(dupe, keep)
    assert world.get(dupe).refusal_for_new_case() is not None
    assert world.get(keep).refusal_for_new_case() is None


def test_an_archived_client_is_refused_either_way():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    world.firms.update_client(replace(world.get(dupe), status=ARCHIVED))
    with pytest.raises(ConflictError):
        world.merge(dupe, keep)
    with pytest.raises(ConflictError):
        world.merge(keep, dupe)


def test_two_clients_on_one_joint_case_are_refused_and_nothing_moves():
    """ADR 0022: one client, one role per case. Folding B into A on a case
    where both are debtors would make A both debtors, and which role should
    win is the firm's call, not the merge's."""
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    joint = world.case_for(keep, dupe)
    alone = world.case_for(dupe)

    with pytest.raises(ConflictError, match="both debtors"):
        world.merge(dupe, keep)

    assert set(world.debtors.roles_for_client(dupe.id)) == {
        (joint.id, "debtor_2"),
        (alone.id, "debtor_1"),
    }
    assert world.get(dupe).merging_into is None
    assert world.get(keep).merging_from is None


def test_a_joint_case_that_appears_mid_merge_releases_the_claim():
    """The pre-check read passed; the per-case write's own condition is what
    catches a link that landed in between."""
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    case = world.case_for(dupe)
    original = world.debtors.repoint_client

    def link_lands_first(case_id, role, **kwargs):
        world.debtors.debtors[(case_id, "debtor_2")] = replace(
            world.debtors.get(case_id, filing_role=role),
            filing_role="debtor_2",
            client_id=keep.id,
        )
        return original(case_id, role, **kwargs)

    world.debtors.repoint_client = link_lands_first  # type: ignore[method-assign]

    with pytest.raises(ConflictError, match="both debtors"):
        world.merge(dupe, keep)

    assert world.get(dupe).status == "active"
    assert world.get(dupe).merging_into is None
    assert world.get(keep).merging_from is None
    assert world.debtors.get(case.id, filing_role="debtor_1").client_id == dupe.id


# ── Merges into and out of the same client at once ──────────────────


@pytest.mark.parametrize(
    ("merged", "survivor"),
    [
        ("keep", "third"),  # the survivor being merged away
        ("third", "dupe"),  # the client being emptied receiving a merge
        ("third", "keep"),  # a second merge into the same survivor
        ("dupe", "third"),  # the client being emptied going elsewhere
    ],
)
def test_while_a_merge_is_claimed_no_overlapping_merge_can_claim(merged, survivor):
    world = World()
    clients = {
        "keep": world.client("A"),
        "dupe": world.client("B"),
        "third": world.client("C"),
    }
    assert world.firms.claim_client_merge(
        FIRM, merged_id=clients["dupe"].id, survivor_id=clients["keep"].id
    )
    with pytest.raises(ConflictError, match="in progress"):
        world.merge(clients[merged], clients[survivor])


def test_a_merge_that_stopped_half_way_finishes_when_run_again():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    case = world.case_for(dupe)
    world.firms.claim_client_merge(FIRM, merged_id=dupe.id, survivor_id=keep.id)

    world.merge(dupe, keep)

    assert world.get(dupe).merged_into == keep.id
    assert world.debtors.get(case.id, filing_role="debtor_1").client_id == keep.id


def test_a_client_being_merged_away_is_not_opened_for_a_case():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    world.firms.claim_client_merge(FIRM, merged_id=dupe.id, survivor_id=keep.id)
    assert world.get(dupe).refusal_for_new_case() == (
        "That client is being merged into another client."
    )
    assert world.get(keep).refusal_for_new_case() is None


def test_an_edit_read_before_the_claim_does_not_erase_it():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    stale = world.get(keep)
    world.firms.claim_client_merge(FIRM, merged_id=dupe.id, survivor_id=keep.id)

    world.firms.update_client(replace(stale, phone="555-0199"))

    assert world.get(keep).merging_from == dupe.id


def test_a_merged_client_cannot_be_edited_back_to_life():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    stale = world.get(dupe)
    world.merge(dupe, keep)
    assert world.firms.update_client(replace(stale, status="active")) is None
    assert world.get(dupe).status == ARCHIVED


def test_an_unclaimed_finish_changes_nothing():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    finished = world.firms.finish_client_merge(
        FIRM, merged_id=dupe.id, survivor_id=keep.id, merged_at="2026-09-30"
    )
    assert finished is None
    assert world.get(dupe).status == "active"


def test_a_concurrent_duplicate_of_the_same_merge_answers_what_it_left():
    world = World()
    keep, dupe = world.client("Jordan"), world.client("Jordy")
    stale_keep, stale_dupe = world.get(keep), world.get(dupe)
    original = world.firms.finish_client_merge

    def the_other_run_finishes_first(firm_id, **kwargs):
        original(firm_id, **kwargs)
        return original(firm_id, **kwargs)

    world.firms.finish_client_merge = the_other_run_finishes_first  # type: ignore[method-assign]
    result = merge_clients(
        world.firms, world.debtors, survivor=stale_keep, merged=stale_dupe
    )
    assert result.merged.merged_into == keep.id


# ── Shapes ──────────────────────────────────────────────────────────


def test_the_merge_attributes_round_trip_through_the_item():
    world = World()
    client = replace(
        world.client("Jordan"),
        status=ARCHIVED,
        merged_into="survivor-1",
        merging_into="survivor-2",
        merging_from="merged-3",
    )
    item = firm_client_item(client)
    assert item["mergedInto"] == "survivor-1"
    assert firm_client_from_item(item) == client


def test_an_unmerged_client_item_carries_no_merge_attributes():
    item = firm_client_item(World().client("Jordan"))
    assert not {"mergedInto", "mergingInto", "mergingFrom"} & item.keys()


def test_the_wire_says_merged_into_and_never_the_claim():
    client = replace(
        World().client("Jordan"), merged_into="survivor-1", merging_from="x"
    )
    body = firm_client_json(client)
    assert body["merged_into"] == "survivor-1"
    assert "merging_from" not in body
    assert "merging_into" not in body
    assert "merged_into" not in firm_client_json(World().client("Jordan"))


@pytest.mark.parametrize(
    "payload", [{}, {"merged_client_id": " "}, {"merged_client_id": 7}]
)
def test_a_merge_body_must_name_the_merged_client(payload):
    with pytest.raises(FieldValidationError) as refused:
        parse_client_merge(payload)
    assert "merged_client_id" in refused.value.fields


def test_a_merge_body_names_the_merged_client():
    assert parse_client_merge({"merged_client_id": " client-b "}) == "client-b"


# ── The DynamoDB debtor store's half ────────────────────────────────


class FakeDynamoDb:
    """Records calls; replays canned query pages; raises what it is told.
    Conditions are not simulated — the integration tier runs the table."""

    def __init__(self, pages: list[dict[str, Any]] | None = None) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.pages = pages or []
        self.raises: Exception | None = None

    def query(self, **kwargs: Any) -> Any:
        self.calls.append(("query", kwargs))
        return self.pages.pop(0)

    def transact_write_items(self, **kwargs: Any) -> Any:
        self.calls.append(("transact_write_items", kwargs))
        if self.raises is not None:
            raise self.raises
        return {}


def dynamo(monkeypatch, fake: FakeDynamoDb) -> DynamoDbDebtorStore:
    monkeypatch.setattr(aws_debtor_store.boto3, "client", lambda _service: fake)
    return DynamoDbDebtorStore("insolvia-test-cases")


def test_the_unfiltered_listing_reads_every_page_of_the_by_client_index(monkeypatch):
    fake = FakeDynamoDb(
        pages=[
            {
                "Items": [{"caseId": {"S": "case-2"}, "filingRole": {"S": "debtor_1"}}],
                "LastEvaluatedKey": {"PK": {"S": "x"}},
            },
            {"Items": [{"caseId": {"S": "case-1"}, "filingRole": {"S": "debtor_2"}}]},
        ]
    )

    found = dynamo(monkeypatch, fake).roles_for_client("client-b")

    assert found == (("case-1", "debtor_2"), ("case-2", "debtor_1"))
    first, second = (kwargs for _, kwargs in fake.calls)
    assert first["IndexName"] == "by-client"
    assert first["ExpressionAttributeValues"] == {":client": {"S": "CLIENT#client-b"}}
    assert second["ExclusiveStartKey"] == {"PK": {"S": "x"}}


def test_a_repoint_moves_only_the_link_and_checks_the_other_roles(monkeypatch):
    fake = FakeDynamoDb()

    outcome = dynamo(monkeypatch, fake).repoint_client(
        "case-1", "debtor_2", from_client_id="client-b", to_client_id="client-a"
    )

    assert outcome == "written"
    [(_, kwargs)] = fake.calls
    update, *checks = kwargs["TransactItems"]
    assert update["Update"]["Key"]["SK"] == {"S": "DEBTOR#debtor_2"}
    assert update["Update"]["UpdateExpression"] == (
        "SET clientId = :to, GSI3PK = :index, updatedAt = :now"
    )
    assert update["Update"]["ConditionExpression"] == "clientId = :from"
    values = update["Update"]["ExpressionAttributeValues"]
    assert values[":index"] == {"S": "CLIENT#client-a"}
    assert values[":from"] == {"S": "client-b"}
    assert [check["ConditionCheck"]["Key"]["SK"]["S"] for check in checks] == [
        "DEBTOR#debtor_1",
        "DEBTOR#non_filing_spouse",
    ]
    for check in checks:
        assert check["ConditionCheck"]["ExpressionAttributeValues"] == {
            ":client": {"S": "client-a"}
        }


def cancelled(*codes: str) -> ClientError:
    return ClientError(
        {
            "Error": {"Code": "TransactionCanceledException"},
            "CancellationReasons": [{"Code": code} for code in codes],
        },
        "TransactWriteItems",
    )


@pytest.mark.parametrize(
    ("codes", "outcome"),
    [
        (("ConditionalCheckFailed", "None", "None"), "absent"),
        (("None", "ConditionalCheckFailed", "None"), "client_taken"),
        (("None", "None", "ConditionalCheckFailed"), "client_taken"),
    ],
)
def test_a_cancelled_repoint_says_which_condition_refused_it(
    monkeypatch, codes, outcome
):
    fake = FakeDynamoDb()
    fake.raises = cancelled(*codes)
    store = dynamo(monkeypatch, fake)
    assert (
        store.repoint_client(
            "case-1", "debtor_1", from_client_id="client-b", to_client_id="client-a"
        )
        == outcome
    )


def test_a_repoint_cancelled_by_a_concurrent_write_is_a_conflict(monkeypatch):
    fake = FakeDynamoDb()
    fake.raises = cancelled("TransactionConflict", "None", "None")
    with pytest.raises(ConflictError):
        dynamo(monkeypatch, fake).repoint_client(
            "case-1", "debtor_1", from_client_id="client-b", to_client_id="client-a"
        )
