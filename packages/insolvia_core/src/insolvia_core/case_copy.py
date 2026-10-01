"""Copy a case — a new matter from an existing one's data (issue 14.3 / #355).

A refiled Chapter 7 after a dismissal, a second matter for the same people,
a case opened by mistake under the wrong court: each starts from facts the
firm has already gathered. The copy is a NEW case — new id, new history,
nothing of the source's filing — carrying the source's preparation data, and
every copied value names the case it came from in its provenance
(`ProvenanceEntry.copied_from_case_id`).

WHAT IS COPIED, and the rule that decides it: the facts the PETITION is
prepared from.

- the case's own preparation fields — chapter, court and division, the
  106C exemption election;
- every debtor, with its client link (so the copy appears in each client's
  case list, ADR 0022) and its tax-id pointer (the sealed item is copied
  by the route — it is ciphertext under a context that never named the
  case, `tax_ids.encryption_context`, so no decrypt is needed);
- every generic case collection (`case_collections.COLLECTIONS`): the
  creditors, claims, assets, income, expenses, SOFA, the petition's
  answers, the plan and the means-test figures.

WHAT IS NOT, and why each is the source's alone: the status and its
history, the post-filing docket facts and dates, the packet pins (each
records what one filing used), the archive stamp, documents (bytes belong to
the matter they were uploaded to — a copied value's `document_id` still
names the source's), extraction candidates, notes, tasks, calendar events,
portal bindings, and every assignment but the copier's own.

IDS ARE KEPT. Every record's id is scoped by its case — the case is half the
key — so the copy's records keep the ids they had, and with them every
cross-reference between them: a claim's `creditor_id`, an exemption's
`asset_id`, a pay period's employment, a debtor's `employer_ids`, and every
provenance path that addresses a list element by id. Re-minting ids would
mean rewriting each of those, which is exactly the class of bug that leaves
a claim pointing at a creditor of another case.

`created_at` is KEPT too: a collection lists in creation order
(`case_entities.list_order`), and the forms print in that order — stamping
every copied record with one instant would reorder the creditor matrix.
`updated_at` is the copy's.

`amended` is cleared on every copied record: the copy has filed nothing, so
nothing in it can amend a filing (the flag's own rule, case_entities.py).
"""

from __future__ import annotations

import uuid
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, replace
from typing import Any

from insolvia_core.case_entities import CaseEntity
from insolvia_core.cases import (
    INTAKE,
    Case,
    CaseAssignment,
    assign_case,
)
from insolvia_core.debtors import Debtor
from insolvia_core.fields import timestamp
from insolvia_core.provenance import ProvenanceEntry


@dataclass(frozen=True)
class CaseCopy:
    """Everything the copy writes: the case and its copier's assignment (one
    transaction with the debtors, `CaseStore.create`'s rule), and the
    collection records that follow it."""

    case: Case
    assignment: CaseAssignment
    debtors: tuple[Debtor, ...]
    entities: tuple[CaseEntity[Any], ...]


def _marked(
    provenance: Mapping[str, ProvenanceEntry], source_case_id: str
) -> dict[str, ProvenanceEntry]:
    """Every entry as it was, naming the case it was copied from."""
    return {
        path: replace(entry, copied_from_case_id=source_case_id)
        for path, entry in provenance.items()
    }


def copy_case(
    source: Case,
    *,
    created_by: str,
    debtors: Sequence[Debtor],
    entities: Sequence[CaseEntity[Any]],
) -> CaseCopy:
    """The copy of `source` that `created_by` is opening, from the source's
    debtors and collection records as read.

    It opens RETAINED (`intake`), as every case does — there is no prospect
    case; the funnel is the client's (`firm_clients.PROSPECT_STAGES`), and
    the route's retained stamp takes the copy's clients out of it."""
    now = timestamp()
    case = Case(
        id=str(uuid.uuid4()),
        firm_id=source.firm_id,
        created_by=created_by,
        chapter=source.chapter,
        district=source.district,
        status=INTAKE,
        created_at=now,
        updated_at=now,
        court=source.court,
        division=source.division,
        exemption_set=source.exemption_set,
    )
    copied_debtors = tuple(
        replace(
            debtor,
            case_id=case.id,
            updated_at=now,
            # The `by-client` entry sorts by the CASE's creation (ADR 0022) —
            # the copy's, so it lists as the newer matter it is.
            case_created_at=case.created_at if debtor.client_id is not None else None,
            provenance=_marked(debtor.provenance, source.id),
        )
        for debtor in debtors
    )
    copied_entities = tuple(
        replace(
            entity,
            case_id=case.id,
            updated_at=now,
            amended=False,
            provenance=_marked(entity.provenance, source.id),
        )
        for entity in entities
    )
    return CaseCopy(
        case=case,
        assignment=assign_case(case, subject=created_by, assigned_by=created_by),
        debtors=copied_debtors,
        entities=copied_entities,
    )
