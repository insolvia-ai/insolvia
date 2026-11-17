"""The client's read of a case: chapter and stage, through the binding
(ADR 0023 decision 4).

What is pinned here is the SHAPE of the read path, because the rule it
enforces is structural: a portal route never holds a `Case`. The store method
takes the `ClientAccessor` (no case id to pass wrongly), the Postgres adapter
selects exactly the public columns, and the projection answers
nothing for a row of another firm.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import fields

import pytest
from insolvia_core.access import ClientAccessor
from insolvia_core.cases import Case, CaseAssignment, case_item
from insolvia_core.clients import (
    ACTIVE,
    CasePublicStatus,
    ClientBinding,
    public_status_from_case_item,
    public_status_json,
)
from insolvia_core.errors import ValidationError
from insolvia_core.firms import Firm
from insolvia_core.ports import CaseStore

from tests.unit.backends import BACKENDS, case_stores

FIRM = "00000000-0000-4000-8000-00000000f18a"
OTHER_FIRM = "00000000-0000-4000-8000-00000000f18b"
CASE = "00000000-0000-4000-8000-0000000ca5e1"
STAFF = "00000000-0000-4000-8000-00000000a11c"
PAT = "00000000-0000-4000-8000-0000000000a1"
NOW = "2026-09-28T12:00:00Z"


def a_case(*, firm_id: str = FIRM, chapter: int = 13, status: str = "intake") -> Case:
    return Case(
        id=CASE,
        firm_id=firm_id,
        created_by=STAFF,
        chapter=chapter,
        district="District of Example",
        status=status,
        created_at=NOW,
        updated_at=NOW,
        filed_at="2026-09-01",
    )


def a_client(*, firm_id: str = FIRM) -> ClientAccessor:
    return ClientAccessor(
        firm=Firm(
            id=firm_id,
            name="Example & Partners",
            status="active",
            created_at=NOW,
            updated_at=NOW,
        ),
        binding=ClientBinding(
            firm_id=firm_id,
            case_id=CASE,
            subject=PAT,
            email="pat@example.test",
            display_name="Pat Example",
            roles=("debtor_1",),
            status=ACTIVE,
            invited_by=STAFF,
            created_at=NOW,
            updated_at=NOW,
        ),
    )


# ── The projection ──────────────────────────────────────────────────


def test_the_projection_is_chapter_and_stage_and_nothing_else():
    assert [f.name for f in fields(CasePublicStatus)] == ["chapter", "stage"]


def test_a_case_item_projects_to_its_chapter_and_lifecycle_status():
    status = public_status_from_case_item(
        case_item(a_case(chapter=7, status="ready_to_file")), firm_id=FIRM
    )

    assert status == CasePublicStatus(chapter=7, stage="ready_to_file")
    assert public_status_json(status) == {"chapter": 7, "stage": "ready_to_file"}


def test_another_firms_row_projects_to_nothing():
    assert public_status_from_case_item(case_item(a_case()), firm_id=OTHER_FIRM) is None


@pytest.mark.parametrize(
    "item",
    [
        {"firmId": FIRM, "status": "intake"},
        {"firmId": FIRM, "chapter": True, "status": "intake"},
        {"firmId": FIRM, "chapter": "seven", "status": "intake"},
        {"firmId": FIRM, "chapter": 7, "status": "shredded"},
        {"firmId": FIRM, "chapter": 7},
    ],
)
def test_a_row_this_domain_did_not_write_raises(item):
    with pytest.raises(ValidationError):
        public_status_from_case_item(item, firm_id=FIRM)


# ── The store, memory and Postgres ──────────────────────────────────


@pytest.fixture(params=BACKENDS)
def store(request) -> CaseStore:
    return case_stores(request)[0]


def seeded(store: CaseStore, case: Case) -> CaseStore:
    store.create(
        case,
        CaseAssignment(
            case_id=case.id,
            subject=STAFF,
            case_created_at=NOW,
            assigned_at=NOW,
            assigned_by=STAFF,
        ),
    )
    return store


def test_the_store_reads_the_bound_case_without_an_assignment(store):
    # The client is not an assignee and holds no Accessor: the binding is
    # the whole authority, and the read needs nothing else.
    seeded(store, a_case(status="filed"))

    assert store.public_status(a_client()) == CasePublicStatus(
        chapter=13, stage="filed"
    )


def test_the_store_refuses_a_binding_whose_firm_does_not_own_the_case(store):
    seeded(store, a_case())

    assert store.public_status(a_client(firm_id=OTHER_FIRM)) is None


def test_the_store_answers_nothing_for_a_missing_case(store):
    assert store.public_status(a_client()) is None
