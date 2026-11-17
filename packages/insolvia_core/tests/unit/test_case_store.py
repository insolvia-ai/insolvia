"""The CaseStore port's contract, held by the memory twin and the Postgres
adapter alike (ADR 0030): the access rule on every read, the two listings and
their cursors, and the assignment rows. Every identifier is obviously fake."""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_core.access import Accessor
from insolvia_core.cases import assign_case, create_case, parse_case_creation
from insolvia_core.errors import ValidationError
from insolvia_core.firms import Firm, FirmUser

from tests.unit.backends import BACKENDS, case_stores

FIRM = "00000000-0000-4000-8000-00000000f18a"
OTHER_FIRM = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
DANA = "00000000-0000-4000-8000-00000000da1a"
OPENING = {"chapter": 7, "court": "flmb", "division": "tampa"}


def accessor(subject: str, *, firm_id: str = FIRM, sees_all: bool = False) -> Accessor:
    firm = Firm(
        id=firm_id, name="Example", status="active", created_at="t", updated_at="t"
    )
    user = FirmUser(
        firm_id=firm_id,
        subject=subject,
        email="someone@example.test",
        first_name="Some",
        last_name="One",
        role="attorney",
        is_admin=False,
        access_all_cases=sees_all,
        permissions={},
        status="active",
        created_at="t",
        updated_at="t",
    )
    return Accessor(firm=firm, user=user)


@pytest.fixture(params=BACKENDS)
def store(request):
    return case_stores(request)[0]


def opened(store, *, by: str = ALICE, firm_id: str = FIRM):
    case, assignment = create_case(
        parse_case_creation(OPENING, require_clients=False),
        firm_id=firm_id,
        created_by=by,
    )
    store.create(case, assignment)
    return case


def test_a_case_is_read_back_exactly(store):
    case = opened(store)
    assert store.get(case.id, accessor=accessor(ALICE)) == case
    assert store.read_for_worker(case.id) == case


def test_a_colleague_not_linked_and_another_firm_both_get_nothing(store):
    case = opened(store)
    assert store.get(case.id, accessor=accessor(DANA)) is None
    assert store.get(case.id, accessor=accessor(ALICE, firm_id=OTHER_FIRM)) is None
    assert store.get(case.id, accessor=accessor(DANA, sees_all=True)) == case


def test_creating_the_same_case_twice_is_refused(store):
    case = opened(store)
    with pytest.raises(RuntimeError):
        store.create(case, assign_case(case, subject=ALICE, assigned_by=ALICE))


def test_both_listings_page_newest_first_and_refuse_each_others_cursor(store):
    cases = [opened(store) for _ in range(3)]
    newest_first = [case.id for case in reversed(cases)]

    for who in (accessor(ALICE), accessor(DANA, sees_all=True)):
        first = store.list_for_accessor(who, limit=2, cursor=None)
        assert first.next_cursor is not None
        rest = store.list_for_accessor(who, limit=2, cursor=first.next_cursor)
        assert [c.id for c in first.cases + rest.cases] == newest_first
        assert rest.next_cursor is None

    by_assignee = store.list_for_accessor(accessor(ALICE), limit=1, cursor=None)
    with pytest.raises(ValidationError):
        store.list_for_accessor(
            accessor(ALICE, sees_all=True), limit=1, cursor=by_assignee.next_cursor
        )


def test_the_listings_show_only_the_callers_firm_and_links(store):
    mine = opened(store)
    opened(store, firm_id=OTHER_FIRM)
    assert [
        c.id
        for c in store.list_for_accessor(
            accessor(DANA, sees_all=True), limit=10, cursor=None
        ).cases
    ] == [mine.id]
    assert store.list_for_accessor(accessor(DANA), limit=10, cursor=None).cases == ()


def test_an_update_of_a_case_that_moved_firms_writes_nothing(store):
    case = opened(store)
    assert store.update(replace(case, firm_id=OTHER_FIRM, judge="J")) is None
    assert store.read_for_worker(case.id) == case


def test_assignment_is_idempotent_and_ordered_oldest_first(store):
    case = opened(store)
    store.assign(assign_case(case, subject=DANA, assigned_by=ALICE))
    store.assign(assign_case(case, subject=DANA, assigned_by=ALICE))

    assert [a.subject for a in store.assignees(case.id)] == [ALICE, DANA]
    assert store.get(case.id, accessor=accessor(DANA)) == case
    assert store.unassign(case.id, DANA) is True
    assert store.unassign(case.id, DANA) is False
    assert store.get(case.id, accessor=accessor(DANA)) is None
