"""Case tasks (issue #356 / 14.4) — parsing, the completion state machine,
overdue computation, and the item round trip.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from datetime import date

import pytest
from insolvia_core.errors import FieldValidationError, ValidationError
from insolvia_core.tasks import (
    MAX_SUBJECT,
    Task,
    apply_task_changes,
    create_task,
    is_overdue,
    list_order,
    parse_task_creation,
    parse_task_update,
    sort_key,
    task_from_item,
    task_item,
    task_json,
)

CASE_ID = "00000000-0000-4000-8000-0000000000ca"
TASK_ID = "00000000-0000-4000-8000-0000000000ta"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"

TODAY = date(2026, 9, 24)

VALID = {
    "subject": "Get the vehicle payoff",
    "description": "Call the lender listed on the credit report.",
    "dueDate": "2026-10-01",
    "assigneeSubject": ALICE,
    "formSeries": "b106d",
}


def payload(**overrides: object) -> dict[str, object]:
    return {**VALID, **overrides}


def make(**overrides: object) -> Task:
    draft = parse_task_creation(payload(**overrides))
    return create_task(draft, case_id=CASE_ID, created_by=ALICE)


# ── Creation parsing ─────────────────────────────────────────────


def test_only_subject_is_required():
    draft = parse_task_creation({"subject": "Follow up with client"})
    assert draft.subject == "Follow up with client"
    assert draft.description is None
    assert draft.due_date is None
    assert draft.assignee_subject is None
    assert draft.form_series is None


@pytest.mark.parametrize(
    ("body", "field"),
    [
        ({"subject": None}, "subject"),
        ({"subject": "   "}, "subject"),
        ({"subject": "x" * (MAX_SUBJECT + 1)}, "subject"),
        ({"subject": "two\nlines"}, "subject"),
        ({"dueDate": "not-a-date"}, "dueDate"),
        ({"dueDate": "2026-02-30"}, "dueDate"),
        ({"assigneeSubject": "not-a-subject"}, "assigneeSubject"),
        ({"assigneeSubject": 7}, "assigneeSubject"),
        ({"formSeries": "UPPER CASE!"}, "formSeries"),
        ({"formSeries": "x" * 40}, "formSeries"),
    ],
)
def test_creation_refuses_bad_fields(body, field):
    with pytest.raises(FieldValidationError) as caught:
        parse_task_creation(payload(**body))
    assert field in caught.value.fields


def test_a_blank_optional_field_collapses_to_none_rather_than_erroring():
    """The same "cleared" rule `fields.text`/`fields.narrative` state for
    every other progressive-intake field: an empty string is not a type
    error, it is the caller leaving the box blank."""
    draft = parse_task_creation(payload(description="   ", formSeries=None))
    assert draft.description is None
    assert draft.form_series is None


def test_creation_never_accepts_done():
    """`done` is not a creation field at all — a client sending it is simply
    ignored, the same "unknown keys are ignored" rule every parser here
    follows."""
    draft = parse_task_creation(payload(**{"done": True}))
    task = create_task(draft, case_id=CASE_ID, created_by=ALICE)
    assert task.done is False
    assert task.completed_at is None


def test_create_task_stamps_identity_and_audit_fact():
    task = make()
    assert task.case_id == CASE_ID
    assert task.created_by == ALICE
    assert task.id
    assert task.created_at == task.updated_at
    assert task.done is False
    assert task.completed_at is None


# ── PATCH parsing and the tri-state UNSET/None/value rule ────────


def test_update_requires_at_least_one_field():
    with pytest.raises(ValidationError):
        parse_task_update({})


def test_update_leaves_absent_fields_unchanged():
    task = make()
    changes = parse_task_update({"subject": "Renamed"})
    updated = apply_task_changes(task, changes)
    assert updated.subject == "Renamed"
    assert updated.description == task.description
    assert updated.due_date == task.due_date
    assert updated.assignee_subject == task.assignee_subject
    assert updated.form_series == task.form_series


@pytest.mark.parametrize(
    "field",
    ["description", "dueDate", "assigneeSubject", "formSeries"],
)
def test_update_clears_a_nullable_field_on_explicit_null(field):
    task = make()
    changes = parse_task_update({field: None})
    updated = apply_task_changes(task, changes)
    attr = {
        "description": "description",
        "dueDate": "due_date",
        "assigneeSubject": "assignee_subject",
        "formSeries": "form_series",
    }[field]
    assert getattr(updated, attr) is None


def test_subject_cannot_be_cleared():
    with pytest.raises(FieldValidationError) as caught:
        parse_task_update({"subject": None})
    assert "subject" in caught.value.fields


def test_reassign_changes_only_the_assignee():
    task = make(assigneeSubject=ALICE)
    changes = parse_task_update({"assigneeSubject": BOB})
    updated = apply_task_changes(task, changes)
    assert updated.assignee_subject == BOB
    assert updated.subject == task.subject


# ── Completion is a state transition, not a plain field ──────────


def test_completing_a_task_stamps_completed_at():
    task = make()
    changes = parse_task_update({"done": True})
    updated = apply_task_changes(task, changes)
    assert updated.done is True
    assert updated.completed_at is not None


def test_reopening_a_task_clears_completed_at():
    task = make()
    done = apply_task_changes(task, parse_task_update({"done": True}))
    reopened = apply_task_changes(done, parse_task_update({"done": False}))
    assert reopened.done is False
    assert reopened.completed_at is None


def test_resending_the_same_done_value_does_not_touch_completed_at():
    task = make()
    done = apply_task_changes(task, parse_task_update({"done": True}))
    resent = apply_task_changes(done, parse_task_update({"done": True}))
    assert resent.completed_at == done.completed_at


def test_editing_other_fields_does_not_disturb_completion():
    task = make()
    done = apply_task_changes(task, parse_task_update({"done": True}))
    edited = apply_task_changes(done, parse_task_update({"subject": "Renamed"}))
    assert edited.done is True
    assert edited.completed_at == done.completed_at


def test_done_must_be_a_boolean():
    with pytest.raises(FieldValidationError) as caught:
        parse_task_update({"done": "yes"})
    assert "done" in caught.value.fields


# ── Overdue is computed, never stored ─────────────────────────────


def test_a_task_past_its_due_date_is_overdue():
    task = make(dueDate="2026-01-01")
    assert is_overdue(task, today=TODAY) is True


def test_a_task_due_today_or_later_is_not_overdue():
    assert is_overdue(make(dueDate="2026-09-24"), today=TODAY) is False
    assert is_overdue(make(dueDate="2026-12-01"), today=TODAY) is False


def test_a_task_with_no_due_date_is_never_overdue():
    assert is_overdue(make(dueDate=None), today=TODAY) is False


def test_a_done_task_is_never_overdue_even_when_late():
    task = make(dueDate="2026-01-01")
    done = apply_task_changes(task, parse_task_update({"done": True}))
    assert is_overdue(done, today=TODAY) is False


def test_task_json_carries_the_computed_overdue_flag():
    task = make(dueDate="2026-01-01")
    body = task_json(task, today=TODAY)
    assert body["overdue"] is True
    assert "completedAt" not in body


# ── Item round trip ────────────────────────────────────────────────


def test_item_round_trip_preserves_every_field():
    task = make()
    done = apply_task_changes(task, parse_task_update({"done": True}))
    restored = task_from_item(task_item(done))
    assert restored == done


def test_item_omits_absent_optional_fields():
    task = create_task(
        parse_task_creation({"subject": "Bare task"}), case_id=CASE_ID, created_by=ALICE
    )
    item = task_item(task)
    for key in (
        "description",
        "dueDate",
        "assigneeSubject",
        "formSeries",
        "completedAt",
    ):
        assert key not in item


def test_sort_key_is_namespaced_by_task():
    assert sort_key(TASK_ID) == f"TASK#{TASK_ID}"


def test_list_order_is_creation_time_then_id():
    older = make()
    newer = create_task(
        parse_task_creation(payload()), case_id=CASE_ID, created_by=ALICE
    )
    ordered = sorted([newer, older], key=list_order)
    assert ordered[0].created_at <= ordered[1].created_at
