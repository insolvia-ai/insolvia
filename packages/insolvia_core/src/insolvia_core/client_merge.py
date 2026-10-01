"""Merging two firm clients (ADR 0022's PR 7 / #354).

A preparer who creates a second client for a person the firm already knows
gets two rows; *merge* folds one (B, "merged") into the other (A, "survivor").
The survivor keeps its id. Every case whose debtor names B is re-pointed to
A — the debtor's `client_id` and the `by-client` entry it feeds, one
conditional write per case — and B is archived with `merged_into: A`, never
deleted. A debtor's copied identity fields, and the provenance that says they
were copied from B, are NOT touched: a filed petition still says what it
said, and "copied from B, which was merged into A" is the truthful history.

## The claim, and why it exists

The ADR names the risk: *two merges into and out of the same client at once
must not strand a case.* Run B→A and A→C together without coordination and
A→C can list A's cases, archive A, and finish — while B→A is still moving
B's cases onto A. Those cases now name a merged client, and nothing will
ever move them again.

So a merge first CLAIMS both rows in one conditional write
(`FirmStore.claim_client_merge`): B is marked as merging into A and A as
receiving from B, only if both are active, neither is merged, B is not
itself receiving a merge and A is not itself being merged away. While the
claim holds, A→C is refused (A is receiving) and so is C→B (B is being
emptied). The per-case writes then happen under the claim, and the last
write archives B and releases both halves together
(`finish_client_merge`). A claim for the same pair passes again, so a merge
that stopped half-way — a Lambda timeout — is finished by running it again.

## Joint cases

A and B can both be debtors on one case: two records for one person, each
put on the joint petition. Re-pointing B's role to A would make one client
two debtors of one case, which ADR 0022 forbids (one client, one role per
case — `DebtorStore.link`'s rule) and the `by-client` index would list
twice. The ADR does not say which role should win, and choosing silently
would rewrite who the petition's second debtor is. So the merge is REFUSED
before anything is written, and the firm resolves the case first (re-link
the role to the right person). The per-case write re-checks the same rule as
its own condition, so a link racing the merge cannot slip past the pre-check.

Pure orchestration over the ports: no boto3.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Final

from insolvia_core.errors import ConflictError, FieldValidationError
from insolvia_core.fields import timestamp
from insolvia_core.firm_clients import FirmClient
from insolvia_core.ports import DebtorStore, FirmStore

JOINT_CASE: Final = (
    "These two clients are both debtors on the same case. Link that case's "
    "debtor to the right client first, then merge."
)
IN_PROGRESS: Final = (
    "Another merge involving one of these clients is in progress. "
    "Try again when it has finished."
)


@dataclass(frozen=True)
class MergeResult:
    """What a finished merge leaves: the survivor as it now stands and the
    merged client, archived with `merged_into`. Deliberately NO count of the
    cases moved — it would include cases the person merging cannot see, and
    a count is the enumeration ADR 0009's 404 hides."""

    survivor: FirmClient
    merged: FirmClient


def refusal(survivor: FirmClient, merged: FirmClient) -> str | None:
    """Why `merged` cannot be folded into `survivor` as the two rows stand,
    or None. A pre-check for a clear message; the claim's own condition is
    what actually decides, so a stale read here cannot let a merge through."""
    if merged.merged_into is not None:
        return "That client was already merged into another client."
    if survivor.merged_into is not None:
        return "This client was merged into another client and cannot receive a merge."
    if merged.archived or survivor.archived:
        return "An archived client cannot be merged — restore them first."
    in_flight = (
        merged.merging_from is not None
        or merged.merging_into not in (None, survivor.id)
        or survivor.merging_into is not None
        or survivor.merging_from not in (None, merged.id)
    )
    return IN_PROGRESS if in_flight else None


def merge_clients(
    firm_store: FirmStore,
    debtor_store: DebtorStore,
    *,
    survivor: FirmClient,
    merged: FirmClient,
) -> MergeResult:
    """Fold `merged` into `survivor`, both already resolved in one firm.

    Raises FieldValidationError for a client merged into itself, and
    ConflictError for every refusal about the clients' state (merged,
    archived, a merge in flight, a joint case)."""
    if survivor.firm_id != merged.firm_id:
        raise RuntimeError("a merge's clients must be resolved in one firm")
    if survivor.id == merged.id:
        raise FieldValidationError(
            {"merged_client_id": "A client cannot be merged into itself."}
        )
    reason = refusal(survivor, merged)
    if reason is not None:
        raise ConflictError(reason)

    firm_id = survivor.firm_id
    # Before the claim, so a refusal leaves nothing to release.
    if _shares_a_case(debtor_store, survivor.id, merged.id):
        raise ConflictError(JOINT_CASE)
    if not firm_store.claim_client_merge(
        firm_id, merged_id=merged.id, survivor_id=survivor.id
    ):
        raise ConflictError(IN_PROGRESS)

    # Twice over the index: the second pass catches an entry the first read
    # missed because the index lagged a write that landed just before the
    # claim. Entries the first pass already moved answer `absent` and cost
    # one refused condition each.
    for _ in range(2):
        for case_id, role in debtor_store.roles_for_client(merged.id):
            outcome = debtor_store.repoint_client(
                case_id, role, from_client_id=merged.id, to_client_id=survivor.id
            )
            if outcome == "client_taken":
                firm_store.release_client_merge(
                    firm_id, merged_id=merged.id, survivor_id=survivor.id
                )
                raise ConflictError(JOINT_CASE)

    archived = firm_store.finish_client_merge(
        firm_id, merged_id=merged.id, survivor_id=survivor.id, merged_at=timestamp()
    )
    if archived is None:
        # A concurrent run of this same merge finished first — the claim is
        # released only by finishing or refusing. Answer what it left.
        archived = firm_store.get_client(firm_id, merged.id)
        if archived is None or archived.merged_into != survivor.id:
            raise ConflictError(IN_PROGRESS)
    current = firm_store.get_client(firm_id, survivor.id)
    return MergeResult(survivor=current or survivor, merged=archived)


def _shares_a_case(debtor_store: DebtorStore, survivor_id: str, merged_id: str) -> bool:
    for case_id, role in debtor_store.roles_for_client(merged_id):
        for debtor in debtor_store.list_for_case(case_id):
            if debtor.filing_role != role and debtor.client_id == survivor_id:
                return True
    return False
