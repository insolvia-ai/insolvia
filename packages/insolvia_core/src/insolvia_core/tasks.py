"""Case tasks (issue #356 / 14.4): the checklist a paralegal, an attorney and
a client actually work from — "get the vehicle payoff for Schedule D" — with
an optional anchor to the form series it feeds.

WHY THERE IS NO PROVENANCE ON THIS RECORD, since every case-scoped schedule
entity carries one and the omission would otherwise look like an oversight.

docs/reference/case-data-model.md's provenance section exists to answer "who
asserted this VALUE, and has a human confirmed it" for facts that end up
printed on a filed form. A task is not case data: it is operational metadata
about the PREPARATION of the case, the same category `document` is in — see
core/documents.py's module docstring for the fuller argument. Nobody extracts
a task from a credit report, nothing about "is this done yet" needs a human
to confirm before it enters the case, and no schedule prints a task's subject
or due date. `created_by` already attributes the one fact worth attributing —
who added it — exactly as `uploaded_by` does for a document, so a per-field
provenance map here would be a second, weaker copy of that single fact.

WHY THIS IS ITS OWN MODULE rather than a `case_collections.py` entry, since
`codebtor` and `contract_lease` show the generic shape and a task looks at
first glance like a third: `core/case_entities.py`'s machinery REQUIRES
provenance on every populated field (`parse_entity` runs `require_provenance`
unconditionally) precisely because every one of the ten generic collections
prints onto a form. Bending that machinery to skip provenance for one kind
would be a special case inside code whose entire point is having none — the
document precedent is the cleaner fit, and it is why this module's shape
mirrors core/documents.py rather than core/codebtors.py.

Everything here is pure: no Flask, no boto3. The item shape lives here rather
than in an adapter so the DynamoDB and in-memory stores cannot drift apart,
exactly as core/documents.py does for the record it is modelled on.
"""

from __future__ import annotations

import re
import uuid
from collections.abc import Mapping
from dataclasses import dataclass, replace
from datetime import date
from typing import Final

from insolvia_core.cases import partition_key
from insolvia_core.errors import FieldValidationError, ValidationError
from insolvia_core.fields import form_date, narrative, timestamp

MAX_SUBJECT: Final = 200
MAX_DESCRIPTION: Final = 2000

# The short form key a task may anchor to — "b106d", "b101", "b122a2" — the
# SAME spelling `core/forms_hub.FormSummary.form` and the forms-hub preview
# route's `<form>` URL segment use, deliberately not the long `form/<key>`
# series id. This package cannot import `services/api`'s form template
# registry (core depends on nothing above it), so this is a SHAPE check only
# — lowercase letters and digits, the way every real key is spelled — not a
# check against the set of forms that actually exist. An anchor naming a form
# this case does not file, or a key nobody ever defined, is harmless: nothing
# reads `form_series` except the forms-hub route's own task count, which
# simply never matches it to a row. That is the same "storage validates shape
# and type only" rule every other progressive-intake field in this service
# follows.
_FORM_SERIES_RE: Final = re.compile(r"^[a-z][a-z0-9]{1,31}\Z")

# A Cognito `sub`, the same shape firms.py validates a firm user's identity
# against. Checked here for the same reason: the value is compared for
# equality across every case a firm-wide "assigned to me" listing walks, and a
# malformed value would simply never match anyone, silently.
_SUBJECT_RE: Final = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z"
)


@dataclass(frozen=True)
class Task:
    """One task of a case — metadata only, no provenance. See the module
    docstring for why.

    `overdue` is deliberately NOT a field: docs/reference/case-data-model.md's
    "Derived values are computed, never stored" rule applies just as much to
    operational records as to case data, and a stored boolean would need a
    write on every day boundary to stay true. `is_overdue` computes it from
    `due_date` and `done` on read.
    """

    id: str
    case_id: str
    subject: str
    description: str | None
    due_date: str | None
    assignee_subject: str | None
    form_series: str | None
    done: bool
    completed_at: str | None
    created_at: str
    updated_at: str
    # An audit fact, exactly like `case.created_by` and `document.uploaded_by`
    # — it grants nothing on its own and is never read by any permission
    # check.
    created_by: str


@dataclass(frozen=True)
class TaskDraft:
    """A validated creation request, before server-generated identity."""

    subject: str
    description: str | None
    due_date: str | None
    assignee_subject: str | None
    form_series: str | None


# The sentinel that makes TaskChanges three-valued rather than two: a PATCH
# field can be ABSENT (leave unchanged, every other *Changes dataclass in this
# package's only state), or explicitly `null` (clear it), or a new value. Every
# other PATCH body in this service only ever sets — see core/cases.CaseChanges
# — because none of their optional fields are something a caller legitimately
# wants to blank once set. A task's are: unassigning it, or clearing a due date
# that turned out to be wrong, are ordinary edits from the case-overview panel,
# not corrections that warrant deleting and recreating the record.
class _Unset:
    __slots__ = ()

    def __repr__(self) -> str:  # pragma: no cover - debugging aid only
        return "UNSET"


UNSET: Final = _Unset()


@dataclass(frozen=True)
class TaskChanges:
    """A validated PATCH body. Absent (UNSET) means "leave unchanged"; an
    explicit `None` on a nullable field means "clear it" — see `UNSET`'s
    comment. `subject` cannot be cleared, only replaced: a task with no title
    is not a smaller task, it is not a task.
    """

    subject: str | _Unset = UNSET
    description: str | None | _Unset = UNSET
    due_date: str | None | _Unset = UNSET
    assignee_subject: str | None | _Unset = UNSET
    form_series: str | None | _Unset = UNSET
    # Completion is a state transition, not a plain field — see
    # `apply_task_changes` for how `completed_at` follows it. Kept in the same
    # PATCH body (rather than a separate endpoint) because "mark done" and
    # "reassign while I'm here" are one edit from the case-overview panel's
    # own row, and splitting them would double the round trips for no
    # protection anything needs.
    done: bool | _Unset = UNSET


def _parse_subject(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str) or not value.strip():
        errors["subject"] = "A subject is required."
        return None
    subject = value.strip()
    if len(subject) > MAX_SUBJECT:
        errors["subject"] = f"Must be {MAX_SUBJECT} characters or fewer."
        return None
    if "\n" in subject or "\r" in subject:
        errors["subject"] = "Must be a single line."
        return None
    return subject


def _parse_assignee_subject(value: object, errors: dict[str, str]) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or not _SUBJECT_RE.match(value.strip()):
        errors["assigneeSubject"] = "Must be a firm user's subject."
        return None
    return value.strip()


def _parse_form_series(value: object, errors: dict[str, str]) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or not _FORM_SERIES_RE.match(value.strip().lower()):
        errors["formSeries"] = (
            'Must be a form key, like "b106d" — lowercase letters and digits.'
        )
        return None
    return value.strip().lower()


def parse_task_creation(payload: Mapping[str, object]) -> TaskDraft:
    """Validate POST /v1/cases/<case_id>/tasks. Unknown keys are ignored.

    Only `subject` is required. `done` is never accepted here — every task
    starts not-done; use PATCH to complete one that already exists.
    """
    errors: dict[str, str] = {}
    subject = _parse_subject(payload.get("subject"), errors)
    description = narrative(
        payload.get("description"), "description", errors, limit=MAX_DESCRIPTION
    )
    due_date = form_date(payload.get("dueDate"), "dueDate", errors)
    assignee_subject = _parse_assignee_subject(payload.get("assigneeSubject"), errors)
    form_series = _parse_form_series(payload.get("formSeries"), errors)

    if errors or subject is None:
        raise FieldValidationError(errors)
    return TaskDraft(
        subject=subject,
        description=description,
        due_date=due_date,
        assignee_subject=assignee_subject,
        form_series=form_series,
    )


def parse_task_update(payload: Mapping[str, object]) -> TaskChanges:
    """Validate PATCH /v1/cases/<case_id>/tasks/<task_id>.

    Each field is independently optional, and a key's ABSENCE from the JSON
    body is what leaves it unchanged — the same rule every other PATCH parser
    in this package follows. Unlike those, a key present with a JSON `null` is
    not a type error on `description`/`dueDate`/`assigneeSubject`/`formSeries`:
    it is the caller asking to clear that field, and `TaskChanges`'s `UNSET`
    sentinel is what keeps that distinguishable from "not sent".
    """
    errors: dict[str, str] = {}
    changes: dict[str, object] = {}

    if "subject" in payload:
        subject = _parse_subject(payload["subject"], errors)
        if subject is not None:
            changes["subject"] = subject
    if "description" in payload:
        raw = payload["description"]
        changes["description"] = (
            None
            if raw is None
            else narrative(raw, "description", errors, limit=MAX_DESCRIPTION)
        )
    if "dueDate" in payload:
        raw = payload["dueDate"]
        changes["due_date"] = None if raw is None else form_date(raw, "dueDate", errors)
    if "assigneeSubject" in payload:
        raw = payload["assigneeSubject"]
        changes["assignee_subject"] = (
            None if raw is None else _parse_assignee_subject(raw, errors)
        )
    if "formSeries" in payload:
        raw = payload["formSeries"]
        changes["form_series"] = (
            None if raw is None else _parse_form_series(raw, errors)
        )
    if "done" in payload:
        done = payload["done"]
        if not isinstance(done, bool):
            errors["done"] = "Must be true or false."
        else:
            changes["done"] = done

    if errors:
        raise FieldValidationError(errors)
    if not changes:
        raise ValidationError("no supported fields to update")
    return TaskChanges(**changes)  # type: ignore[arg-type]


def create_task(draft: TaskDraft, *, case_id: str, created_by: str) -> Task:
    """Stamp a draft with server-generated identity. `created_by` comes from
    the verified caller, `case_id` from a case the caller was just shown to
    reach — neither is ever read from the request body."""
    now = timestamp()
    return Task(
        id=str(uuid.uuid4()),
        case_id=case_id,
        subject=draft.subject,
        description=draft.description,
        due_date=draft.due_date,
        assignee_subject=draft.assignee_subject,
        form_series=draft.form_series,
        done=False,
        completed_at=None,
        created_at=now,
        updated_at=now,
        created_by=created_by,
    )


def apply_task_changes(task: Task, changes: TaskChanges) -> Task:
    """A new Task with `changes` applied and `updated_at` refreshed.

    `completed_at` is never accepted from a caller — it FOLLOWS `done`, the
    same relationship `confirm_document` gives `status`/`etag`. Turning `done`
    on stamps it now; turning it off (reopening a task marked done too soon)
    clears it; leaving `done` unchanged leaves it alone, so an edit that only
    touches the due date does not disturb a completion that already happened.
    """
    updates: dict[str, object] = {}
    if not isinstance(changes.subject, _Unset):
        updates["subject"] = changes.subject
    if not isinstance(changes.description, _Unset):
        updates["description"] = changes.description
    if not isinstance(changes.due_date, _Unset):
        updates["due_date"] = changes.due_date
    if not isinstance(changes.assignee_subject, _Unset):
        updates["assignee_subject"] = changes.assignee_subject
    if not isinstance(changes.form_series, _Unset):
        updates["form_series"] = changes.form_series
    if not isinstance(changes.done, _Unset) and changes.done != task.done:
        updates["done"] = changes.done
        updates["completed_at"] = timestamp() if changes.done else None
    return replace(task, updated_at=timestamp(), **updates)  # type: ignore[arg-type]


def is_overdue(task: Task, *, today: date) -> bool:
    """Whether this task is late, computed rather than stored (the data
    model's own rule, applied to an operational record). A done task is never
    overdue — completing it late is still completing it — and a task with no
    due date has nothing to be overdue against."""
    if task.done or task.due_date is None:
        return False
    return date.fromisoformat(task.due_date) < today


def sort_key(task_id: str) -> str:
    return f"TASK#{task_id}"


def list_order(task: Task) -> tuple[str, str]:
    """Creation order, id as the tiebreak — the same rule
    core/case_entities.list_order states, and for the same reason: the sort
    key is a random uuid, so both stores sort explicitly against this rather
    than trusting query order. A case's task list reads top to bottom in the
    order a preparer added them, the same as its schedules."""
    return (task.created_at, task.id)


TaskItemValue = str | bool


def task_item(task: Task) -> dict[str, TaskItemValue]:
    """The exact stored item shape, shared by both TaskStore implementations.

    PK  CASE#<case_id>        the same partition as the case root and every
    SK  TASK#<id>              other case-scoped record, so a task is read
                               with the case it belongs to.

    No GSI keys, the same as documents and the generic case collections:
    a task is always reached through its case in `list_for_case`, never
    listed across cases by a store method — the cross-case "assigned to me"
    listing walks the CALLER'S REACHABLE CASES instead (see
    api/routes/tasks.py), so no index here could serve it without also
    re-deriving `may_see_case`.
    """
    item: dict[str, TaskItemValue] = {
        "PK": partition_key(task.case_id),
        "SK": sort_key(task.id),
        "id": task.id,
        "caseId": task.case_id,
        "subject": task.subject,
        "done": task.done,
        "createdAt": task.created_at,
        "updatedAt": task.updated_at,
        "createdBy": task.created_by,
    }
    # Omitted rather than written null/empty — the same "absence is the
    # honest encoding" rule document_item follows for `etag`.
    if task.description is not None:
        item["description"] = task.description
    if task.due_date is not None:
        item["dueDate"] = task.due_date
    if task.assignee_subject is not None:
        item["assigneeSubject"] = task.assignee_subject
    if task.form_series is not None:
        item["formSeries"] = task.form_series
    if task.completed_at is not None:
        item["completedAt"] = task.completed_at
    return item


def task_from_item(item: Mapping[str, TaskItemValue]) -> Task:
    """Inverse of task_item. Raises ValidationError on an item this service
    did not write — a corrupt row should fail loudly here rather than become a
    half-populated Task."""
    try:
        return Task(
            id=str(item["id"]),
            case_id=str(item["caseId"]),
            subject=str(item["subject"]),
            description=str(item["description"]) if "description" in item else None,
            due_date=str(item["dueDate"]) if "dueDate" in item else None,
            assignee_subject=(
                str(item["assigneeSubject"]) if "assigneeSubject" in item else None
            ),
            form_series=str(item["formSeries"]) if "formSeries" in item else None,
            # `is True` rather than a bare cast, the same guard firms.py's
            # `is_admin=item["isAdmin"] is True` uses: a row that somehow
            # holds anything else must not read as done by accident.
            done=item.get("done") is True,
            completed_at=str(item["completedAt"]) if "completedAt" in item else None,
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
            created_by=str(item["createdBy"]),
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(f"stored task item is malformed: {error}") from error


def task_json(task: Task, *, today: date) -> dict[str, object]:
    """The API representation. Absent optional values are omitted, matching
    every other progressive record in this service.

    `overdue` IS included, and it is the one field here that is not a column
    on `Task` at all — see `is_overdue`. `today` is threaded in rather than
    read from the clock here so a route computes it once (`datetime.now(UTC)`)
    and every task in one response agrees on what "today" means, even if the
    request happens to straddle midnight mid-render.
    """
    body: dict[str, object] = {
        "id": task.id,
        "caseId": task.case_id,
        "subject": task.subject,
        "done": task.done,
        "overdue": is_overdue(task, today=today),
        "createdAt": task.created_at,
        "updatedAt": task.updated_at,
        "createdBy": task.created_by,
    }
    if task.description is not None:
        body["description"] = task.description
    if task.due_date is not None:
        body["dueDate"] = task.due_date
    if task.assignee_subject is not None:
        body["assigneeSubject"] = task.assignee_subject
    if task.form_series is not None:
        body["formSeries"] = task.form_series
    if task.completed_at is not None:
        body["completedAt"] = task.completed_at
    return body
