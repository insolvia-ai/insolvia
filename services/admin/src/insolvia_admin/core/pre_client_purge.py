"""Which case partitions are pre-client data — ADR 0022's one release-time
deletion, decided here, purely, so the rule is readable and tested apart from
the table it runs against.

## The rule, and what it can never do

ADR 0022 wrote no migration: the product is not live, so the cases opened
before `POST /v1/cases` required clients are deleted rather than upgraded.
"Pre-client" is decided per case partition from its own items:

    A case is pre-client  ⇔  its partition holds a case root (SK META)
                             AND no debtor item (SK DEBTOR#…) in it carries a
                             `clientId`.

A case opened for a client has a Debtor 1 naming that client from the
moment it exists (one transaction — `CaseStore.create`), so it can never
satisfy the rule. The rule looks at nothing else: not the fixture version,
not who opened it, not its age. A partition with no META (a half-written
row nobody can reach) is never touched either — "and nothing else" is the
ADR's phrase, and a partition this rule cannot identify as a case is
something else.

The DELETE is fenced a second time, at the item, by the adapter: every
debtor item is deleted on `attribute_not_exists(clientId)`, so a client
linked between the plan and the write stops that partition rather than
being deleted with it. The case root goes last, so a stopped partition is
still a case the next run re-decides.

## What goes with a partition

Every item in it — the root, assignments, debtors, sealed tax ids,
collection items, documents' rows, jobs, the portal binding's mirror and
role claims — and ONE thing outside it: the portal binding's authoritative
row in the firm table (ADR 0023), deleted only while it still names the
purged case. Left behind, it would bind a portal client to a case that no
longer exists, and the seed loader — which leaves an existing binding
alone — would never rebind them to the fixture's replacement case.

NOT deleted: the case's rows in the access log (append-only by design; the
API cannot even read them), and its documents' bytes in the case-documents
bucket. Both outlive the rows by intent or by grant; see the entrypoint.

Pure: no boto3.
"""

from __future__ import annotations

from collections.abc import Iterable, Mapping
from dataclasses import dataclass
from typing import Final, Protocol

CASE_PREFIX: Final = "CASE#"
META: Final = "META"
DEBTOR_PREFIX: Final = "DEBTOR#"
# The portal binding's mirror in the case partition (insolvia_core.clients).
BINDING_MIRROR_PREFIX: Final = "CLIENT#"


@dataclass(frozen=True)
class BindingRow:
    """The firm-table binding row a purged case's mirror points at."""

    firm_id: str
    subject: str
    case_id: str


@dataclass(frozen=True)
class PurgePlan:
    """What one run will delete, and what it decided to keep."""

    doomed: tuple[str, ...]
    kept_with_client: int

    @property
    def empty(self) -> bool:
        return not self.doomed


def case_id_of(pk: str) -> str | None:
    return pk[len(CASE_PREFIX) :] if pk.startswith(CASE_PREFIX) else None


def plan(summaries: Iterable[Mapping[str, object]]) -> PurgePlan:
    """The pre-client cases among `summaries` — one row per item, carrying at
    least `PK`, `SK` and, on a debtor that names a client, `clientId`.

    Order is the scan's; the result is sorted so a report reads the same
    twice."""
    roots: set[str] = set()
    with_client: set[str] = set()
    for row in summaries:
        case_id = case_id_of(str(row.get("PK", "")))
        if case_id is None:
            continue
        sort_key = str(row.get("SK", ""))
        if sort_key == META:
            roots.add(case_id)
        elif sort_key.startswith(DEBTOR_PREFIX) and row.get("clientId"):
            with_client.add(case_id)
    return PurgePlan(
        doomed=tuple(sorted(roots - with_client)),
        kept_with_client=len(roots & with_client),
    )


def still_pre_client(items: Iterable[Mapping[str, object]]) -> bool:
    """Re-decided on the partition's CURRENT items, read consistently just
    before deleting it: a client linked since the scan saves the case."""
    rows = list(items)
    has_root = any(row.get("SK") == META for row in rows)
    has_client = any(
        str(row.get("SK", "")).startswith(DEBTOR_PREFIX) and row.get("clientId")
        for row in rows
    )
    return has_root and not has_client


def binding_rows(items: Iterable[Mapping[str, object]]) -> tuple[BindingRow, ...]:
    """The firm-table binding rows a partition's mirrors point at."""
    found: list[BindingRow] = []
    for row in items:
        if not str(row.get("SK", "")).startswith(BINDING_MIRROR_PREFIX):
            continue
        firm_id, subject, case_id = (
            row.get("firmId"),
            row.get("subject"),
            row.get("caseId"),
        )
        if (
            isinstance(firm_id, str)
            and isinstance(subject, str)
            and isinstance(case_id, str)
        ):
            found.append(BindingRow(firm_id=firm_id, subject=subject, case_id=case_id))
    return tuple(found)


def deletion_order(items: Iterable[Mapping[str, object]]) -> list[Mapping[str, object]]:
    """Every item but the root first, the root last — a partition stopped
    part-way is still a case the next run finds and re-decides."""
    rows = list(items)
    return [row for row in rows if row.get("SK") != META] + [
        row for row in rows if row.get("SK") == META
    ]


# ── The run, over a port ────────────────────────────────────────────


class CaseTableRows(Protocol):
    """The case table (and the firm table's binding rows) as the purge sees
    them. Implemented in adapters/aws (DynamoDB) and adapters/memory (tests)."""

    def summaries(self) -> Iterable[Mapping[str, object]]:
        """Every case-table item's `PK`, `SK` and, where set, `clientId`."""
        ...

    def partition(self, case_id: str) -> list[Mapping[str, object]]:
        """Every item in one case partition, read consistently."""
        ...

    def delete(self, item: Mapping[str, object]) -> bool:
        """Delete one case-table item by its keys. A DEBTOR# item is deleted
        only while it names no client; False when that condition refused."""
        ...

    def delete_binding(self, row: BindingRow) -> bool:
        """Delete one firm-table binding row only while it still names
        `row.case_id`. False when it no longer did (or was already gone)."""
        ...


@dataclass(frozen=True)
class PurgeReport:
    planned: tuple[str, ...]
    deleted: tuple[str, ...]
    stopped: tuple[str, ...]
    items_deleted: int
    bindings_deleted: int
    kept_with_client: int


def purge(rows: CaseTableRows, *, apply: bool) -> PurgeReport:
    """Plan, then — with `apply` — delete each planned partition after
    re-reading it. Without `apply` nothing is written: the report is the
    plan. Idempotent by construction: a deleted partition has no root, so a
    second run plans nothing."""
    decided = plan(rows.summaries())
    if not apply:
        return PurgeReport(
            planned=decided.doomed,
            deleted=(),
            stopped=(),
            items_deleted=0,
            bindings_deleted=0,
            kept_with_client=decided.kept_with_client,
        )
    deleted: list[str] = []
    stopped: list[str] = []
    items_deleted = 0
    bindings_deleted = 0
    for case_id in decided.doomed:
        items = rows.partition(case_id)
        if not still_pre_client(items):
            stopped.append(case_id)
            continue
        complete = True
        for item in deletion_order(items):
            if not rows.delete(item):
                # A debtor gained a client since the re-read: stop here, root
                # still in place, and leave the rest to the next run's plan.
                complete = False
                break
            items_deleted += 1
        if not complete:
            stopped.append(case_id)
            continue
        for binding in binding_rows(items):
            if binding.case_id == case_id and rows.delete_binding(binding):
                bindings_deleted += 1
        deleted.append(case_id)
    return PurgeReport(
        planned=decided.doomed,
        deleted=tuple(deleted),
        stopped=tuple(stopped),
        items_deleted=items_deleted,
        bindings_deleted=bindings_deleted,
        kept_with_client=decided.kept_with_client,
    )
