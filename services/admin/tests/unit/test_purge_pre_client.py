"""ADR 0022's release step: delete the case partitions whose debtors name no
firm client, and nothing else.

The rows are planted with `insolvia_core`'s own item functions — the exact
shapes the API and the seeder write — so "what the purge recognises" and
"what the store holds" cannot drift. The DynamoDB adapter is pinned over a
recording transport at the end.

Every identifier is obviously fake. This repo is public.
"""

from __future__ import annotations

from typing import Any

import pytest
from botocore.exceptions import ClientError
from insolvia_admin.adapters.aws import case_purge as aws_case_purge
from insolvia_admin.adapters.memory.case_purge import MemoryCaseTableRows
from insolvia_admin.core.pre_client_purge import BindingRow, plan, purge
from insolvia_admin.entrypoints.purge_pre_client import main
from insolvia_core.cases import (
    assign_case,
    assignment_item,
    case_item,
    create_case,
    parse_case_creation,
)
from insolvia_core.clients import (
    binding_item,
    create_binding,
    mirror_item,
    parse_invitation,
    role_claim_item,
)
from insolvia_core.debtors import create_debtor, debtor_item, parse_debtor
from insolvia_core.firm_clients import (
    create_firm_client,
    debtor_from_client,
    parse_firm_client,
)

FIRM = "00000000-0000-4000-8000-00000000f18a"
ALICE = "00000000-0000-4000-8000-00000000a11c"
PORTAL = "00000000-0000-4000-8000-00000000c11e"

DEV_CASES = "insolvia-dev-0123456789ab-cases"
DEV_FIRMS = "insolvia-dev-0123456789ab-firms"


def a_case():
    case, _ = create_case(
        parse_case_creation(
            {"chapter": 7, "court": "flmb", "division": "tampa"},
            require_clients=False,
        ),
        firm_id=FIRM,
        created_by=ALICE,
    )
    return case


def plant_case(rows: MemoryCaseTableRows, *, client: bool, spouse: bool = False):
    """One case partition: root, assignment, Debtor 1 — copied from a client
    or typed without one — and optionally an unlinked non-filing spouse."""
    case = a_case()
    rows.put_case_item(case_item(case))
    rows.put_case_item(
        assignment_item(assign_case(case, subject=ALICE, assigned_by=ALICE))
    )
    if client:
        firm_client = create_firm_client(
            parse_firm_client({"name": {"given": "Jordan"}}),
            firm_id=FIRM,
            created_by=ALICE,
        )
        debtor = debtor_from_client(firm_client, case=case, filing_role="debtor_1")
    else:
        debtor = create_debtor(
            parse_debtor({}), case_id=case.id, filing_role="debtor_1"
        )
    rows.put_case_item(debtor_item(debtor))
    if spouse:
        rows.put_case_item(
            debtor_item(
                create_debtor(
                    parse_debtor({}), case_id=case.id, filing_role="non_filing_spouse"
                )
            )
        )
    return case


def bind_portal_client(rows: MemoryCaseTableRows, case) -> None:
    binding = create_binding(
        parse_invitation(
            {
                "email": "portal@example.test",
                "displayName": "Portal Person",
                "roles": ["debtor_1"],
            }
        ),
        firm_id=FIRM,
        case_id=case.id,
        subject=PORTAL,
        invited_by=ALICE,
    )
    rows.put_firm_item(binding_item(binding))
    rows.put_case_item(mirror_item(binding))
    rows.put_case_item(role_claim_item(case.id, "debtor_1", PORTAL))


# ── The rule ────────────────────────────────────────────────────────


def test_a_case_whose_debtors_name_no_client_is_deleted_whole():
    rows = MemoryCaseTableRows()
    old = plant_case(rows, client=False)

    report = purge(rows, apply=True)

    assert report.deleted == (old.id,)
    assert rows.partition(old.id) == []


def test_a_case_opened_for_a_client_is_never_planned():
    rows = MemoryCaseTableRows()
    kept = plant_case(rows, client=True, spouse=True)
    before = dict(rows.cases)

    report = purge(rows, apply=True)

    assert report.planned == ()
    assert report.kept_with_client == 1
    assert rows.cases == before
    assert rows.partition(kept.id)


def test_a_case_with_no_debtors_at_all_is_pre_client():
    """Opened before PR 2 and never given an intake: no debtor, no client."""
    rows = MemoryCaseTableRows()
    case = a_case()
    rows.put_case_item(case_item(case))
    assert plan(rows.summaries()).doomed == (case.id,)


def test_a_partition_with_no_case_root_is_not_a_case_and_is_left():
    rows = MemoryCaseTableRows()
    rows.put_case_item({"PK": "CASE#stray", "SK": "DEBTOR#debtor_1"})
    assert plan(rows.summaries()).doomed == ()


def test_only_case_partitions_are_considered():
    rows = MemoryCaseTableRows()
    rows.put_case_item({"PK": "JOB#x", "SK": "META"})
    assert plan(rows.summaries()).doomed == ()


def test_a_second_run_deletes_nothing():
    rows = MemoryCaseTableRows()
    plant_case(rows, client=False)
    plant_case(rows, client=True)
    purge(rows, apply=True)

    again = purge(rows, apply=True)

    assert (again.planned, again.deleted, again.items_deleted) == ((), (), 0)


def test_a_dry_run_writes_nothing():
    rows = MemoryCaseTableRows()
    old = plant_case(rows, client=False)
    before = dict(rows.cases)

    report = purge(rows, apply=False)

    assert report.planned == (old.id,)
    assert rows.cases == before


def test_a_client_linked_after_the_plan_saves_the_case():
    """The race the per-partition re-read exists for: the plan named the
    case, then someone linked a client to its Debtor 1."""
    rows = MemoryCaseTableRows()
    case = plant_case(rows, client=False)
    stale_scan = list(rows.summaries())
    assert plan(stale_scan).doomed == (case.id,)
    debtor_key = (f"CASE#{case.id}", "DEBTOR#debtor_1")
    rows.cases[debtor_key] = {**rows.cases[debtor_key], "clientId": "c-late"}

    report = purge(_Replaying(rows, stale_scan), apply=True)

    assert report.stopped == (case.id,)
    assert rows.partition(case.id)


class _Replaying:
    """`rows`, but `summaries` answers a stale scan — the plan from before a
    client was linked."""

    def __init__(self, rows: MemoryCaseTableRows, stale_scan) -> None:
        self.rows = rows
        self.decided = stale_scan

    def summaries(self):
        return iter(self.decided)

    def partition(self, case_id):
        return self.rows.partition(case_id)

    def delete(self, item):
        return self.rows.delete(item)

    def delete_binding(self, row):
        return self.rows.delete_binding(row)


def test_the_item_fence_stops_a_partition_whose_debtor_names_a_client():
    """Even past the re-read: the adapter refuses to delete a debtor that
    names a client, and the case root — deleted last — survives."""
    rows = MemoryCaseTableRows()
    case = plant_case(rows, client=False)
    real_partition = rows.partition

    def stale_partition(case_id):
        items = real_partition(case_id)
        # The re-read saw no client; the table has one by delete time.
        key = (f"CASE#{case.id}", "DEBTOR#debtor_1")
        rows.cases[key] = {**rows.cases[key], "clientId": "c-late"}
        return items

    rows.partition = stale_partition  # type: ignore[method-assign]
    report = purge(rows, apply=True)

    assert report.stopped == (case.id,)
    assert (f"CASE#{case.id}", "META") in rows.cases
    assert (f"CASE#{case.id}", "DEBTOR#debtor_1") in rows.cases


def test_a_purged_cases_portal_binding_goes_with_it():
    """Left behind, it would bind the portal client to a case that no longer
    exists — and the seeder never rebinds a subject that has a binding."""
    rows = MemoryCaseTableRows()
    case = plant_case(rows, client=False)
    bind_portal_client(rows, case)

    report = purge(rows, apply=True)

    assert report.bindings_deleted == 1
    assert rows.firms == {}


def test_a_binding_since_moved_to_another_case_is_kept():
    rows = MemoryCaseTableRows()
    case = plant_case(rows, client=False)
    bind_portal_client(rows, case)
    key = (f"FIRM#{FIRM}", f"CLIENT#{PORTAL}")
    rows.firms[key] = {**rows.firms[key], "caseId": "another-case"}

    purge(rows, apply=True)

    assert key in rows.firms


# ── The entrypoint's fences ─────────────────────────────────────────


@pytest.mark.parametrize(
    ("case_table", "firm_table"),
    [
        ("insolvia-dev-0123456789ab-cases", "insolvia-staging-firms"),
        ("insolvia-staging-cases", "insolvia-prod-firms"),
        ("insolvia-prod-firms", "insolvia-prod-firms"),
        ("some-other-table", "insolvia-prod-firms"),
        ("insolvia-prod-cases-backup", "insolvia-prod-firms"),
    ],
)
def test_tables_must_be_one_environments_case_and_firm_tables(case_table, firm_table):
    touched: list[str] = []

    def rows(_c, _f):
        touched.append("constructed")
        return MemoryCaseTableRows()

    code = main(["--case-table", case_table, "--firm-table", firm_table], rows=rows)

    assert code == 2
    assert touched == []


@pytest.mark.parametrize("env", ["dev-0123456789ab", "staging", "prod"])
def test_every_environment_may_run_it(env):
    rows = MemoryCaseTableRows()
    code = main(
        [
            "--case-table",
            f"insolvia-{env}-cases",
            "--firm-table",
            f"insolvia-{env}-firms",
        ],
        rows=lambda _c, _f: rows,
    )
    assert code == 0


def test_check_fails_while_anything_pre_client_remains_and_apply_clears_it():
    rows = MemoryCaseTableRows()
    plant_case(rows, client=False)
    argv = ["--case-table", DEV_CASES, "--firm-table", DEV_FIRMS]

    assert main([*argv, "--check"], rows=lambda _c, _f: rows) == 1
    assert main(argv, rows=lambda _c, _f: rows) == 0  # a report writes nothing
    assert main([*argv, "--check"], rows=lambda _c, _f: rows) == 1
    assert main([*argv, "--apply"], rows=lambda _c, _f: rows) == 0
    assert main([*argv, "--check"], rows=lambda _c, _f: rows) == 0


# ── The DynamoDB adapter, over a recording transport ────────────────


class FakeDynamoDb:
    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.fail_conditions = False

    def scan(self, **kwargs: Any) -> Any:
        self.calls.append(("scan", kwargs))
        return {"Items": [{"PK": {"S": "CASE#k"}, "SK": {"S": "META"}}]}

    def query(self, **kwargs: Any) -> Any:
        self.calls.append(("query", kwargs))
        return {"Items": []}

    def delete_item(self, **kwargs: Any) -> Any:
        self.calls.append(("delete_item", kwargs))
        if self.fail_conditions and "ConditionExpression" in kwargs:
            raise ClientError(
                {"Error": {"Code": "ConditionalCheckFailedException", "Message": ""}},
                "DeleteItem",
            )
        return {}


def adapter(monkeypatch, fake):
    monkeypatch.setattr(aws_case_purge.boto3, "client", lambda _service: fake)
    return aws_case_purge.DynamoDbCaseTableRows(DEV_CASES, DEV_FIRMS)


def test_the_scan_reads_keys_and_the_client_link_only(monkeypatch):
    fake = FakeDynamoDb()
    rows = list(adapter(monkeypatch, fake).summaries())
    assert rows == [{"PK": "CASE#k", "SK": "META"}]
    [(_, kwargs)] = fake.calls
    assert kwargs["ProjectionExpression"] == "PK, SK, clientId"
    assert kwargs["ConsistentRead"] is True


def test_a_debtor_is_deleted_only_while_it_names_no_client(monkeypatch):
    fake = FakeDynamoDb()
    store = adapter(monkeypatch, fake)
    store.delete({"PK": "CASE#k", "SK": "DEBTOR#debtor_1"})
    store.delete({"PK": "CASE#k", "SK": "META"})
    debtor, root = (kwargs for _, kwargs in fake.calls)
    assert debtor["ConditionExpression"] == "attribute_not_exists(clientId)"
    assert "ConditionExpression" not in root

    fake.fail_conditions = True
    assert store.delete({"PK": "CASE#k", "SK": "DEBTOR#debtor_1"}) is False


def test_a_binding_row_is_deleted_only_while_it_names_the_purged_case(monkeypatch):
    fake = FakeDynamoDb()
    store = adapter(monkeypatch, fake)
    assert store.delete_binding(BindingRow(firm_id="f", subject="s", case_id="k"))
    [(_, kwargs)] = fake.calls
    assert kwargs["TableName"] == DEV_FIRMS
    assert kwargs["Key"] == {"PK": {"S": "FIRM#f"}, "SK": {"S": "CLIENT#s"}}
    assert kwargs["ConditionExpression"] == "caseId = :case"
    fake.fail_conditions = True
    assert (
        store.delete_binding(BindingRow(firm_id="f", subject="s", case_id="k")) is False
    )


def test_the_plan_ignores_a_debtor_whose_client_link_is_empty():
    """`clientId` must name something to count."""
    assert plan(
        [
            {"PK": "CASE#k", "SK": "META"},
            {"PK": "CASE#k", "SK": "DEBTOR#debtor_1", "clientId": ""},
        ]
    ).doomed == ("k",)
