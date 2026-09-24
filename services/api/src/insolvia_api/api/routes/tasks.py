"""Case tasks (issue #356 / 14.4): list, add, edit, complete, reassign and
remove, per case — plus `GET /v1/me/tasks`, the ones assigned to the caller
across every case in their firm they can reach.

A STATIC SEGMENT, like `/debtors` and `/documents` — see
api/routes/case_entities.py's docstring for why that beats Werkzeug's
`<collection>` catch-all regardless of blueprint registration order, and
app_factory.py's comment for where this needs to register relative to it.

`GET /v1/me/tasks` DOES NOT ADD A NEW STORE METHOD to walk. It resolves the
caller's reachable cases through `CaseStore.list_for_accessor` — the SAME
listing `GET /v1/cases` uses, which already applies `core/access.may_see_case`
— and reads each one's tasks. That is deliberate, not a shortcut: a
`TaskStore` method that scanned by assignee across the whole table would have
to re-derive case reachability itself (a firm boundary, `sees_every_case`,
per-case linkage) or risk handing back a task from a case the caller cannot
open. Walking cases already proven reachable means there is nothing left to
re-derive — see TaskStore's own docstring in core/ports.py.
"""

from __future__ import annotations

import logging
from datetime import UTC, datetime

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import Accessor
from insolvia_core.access_log import record_access
from insolvia_core.cases import MAX_LIST_LIMIT, Case
from insolvia_core.errors import FieldValidationError, NotFoundError, ValidationError
from insolvia_core.firms import ADD_EDIT, TASKS, VIEW_ONLY
from insolvia_core.ports import AccessLog, CaseStore, FirmStore, TaskStore
from insolvia_core.tasks import (
    UNSET,
    apply_task_changes,
    create_task,
    parse_task_creation,
    parse_task_update,
    task_json,
)

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

blueprint = Blueprint("tasks", __name__)

# A task is a handful of short fields — see core/tasks.py's MAX_SUBJECT and
# MAX_DESCRIPTION — so 16 KiB is generous headroom over anything genuine and
# still cheap to reject before JSON parsing.
MAX_REQUEST_BYTES = 16 * 1024


def _stores() -> tuple[CaseStore, TaskStore, FirmStore, AccessLog]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.task_store is None
        or deps.firm_store is None
        or deps.access_log is None
    ):
        raise RuntimeError(
            "case store, task store, firm store and access log are not composed"
        )
    return deps.case_store, deps.task_store, deps.firm_store, deps.access_log


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 16 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _reachable_case_or_404(
    case_store: CaseStore, accessor: Accessor, case_id: str, action: str
) -> Case:
    """Resolve the case first, and record the attempt either way — the exact
    pattern api/routes/case_entities.py's `_reachable_case_or_404` states, and
    the reason it is repeated here rather than imported: sharing a helper
    across two route modules would couple this module's access logging to
    case_entities' collection-agnostic one."""
    _, _, _, access_log = _stores()
    case = case_store.get(case_id, accessor=accessor)
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action=action,
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        raise NotFoundError("case not found")
    return case


def _validate_assignee(
    firm_store: FirmStore, accessor: Accessor, subject: str | None
) -> None:
    """THE SUBJECT MUST BE SOMEBODY ACTIVE IN THE CALLER'S FIRM, checked here
    against the firm store — the same rule api/routes/cases.py's
    `assign_case_route` states for linking a colleague to a case, applied to
    assigning one a task. A 400 rather than that route's 404: here the subject
    is a request BODY field, not a path segment naming another resource, so
    the anti-oracle argument for hiding it behind a 404 does not apply — the
    caller already knows their own firm's roster.
    """
    if subject is None:
        return
    colleague = firm_store.get_user(accessor.firm_id, subject)
    if colleague is None or colleague.status != "active":
        raise FieldValidationError(
            {"assigneeSubject": "Must be an active user of your firm."}
        )


@blueprint.post("/v1/cases/<case_id>/tasks")
@require_auth
@requires(TASKS, ADD_EDIT)
def create_task_route(case_id: str) -> ResponseReturnValue:
    """Add one task to a case. The server mints the id and returns the stored
    record."""
    case_store, task_store, firm_store, _ = _stores()
    accessor = current_accessor()

    # Body BEFORE ownership, the same deliberate inversion case_entities.py's
    # create route states: a 400 says "your JSON is wrong", not "that case
    # exists".
    draft = parse_task_creation(_json_body())
    _reachable_case_or_404(case_store, accessor, case_id, "case.update")
    _validate_assignee(firm_store, accessor, draft.assignee_subject)

    task = create_task(draft, case_id=case_id, created_by=accessor.subject)
    task_store.create(task)
    logger.info("task created", extra={"case_id": case_id, "id": task.id})
    return jsonify(task_json(task, today=datetime.now(UTC).date())), 201


@blueprint.get("/v1/cases/<case_id>/tasks")
@require_auth
@requires(TASKS, VIEW_ONLY)
def list_tasks_route(case_id: str) -> ResponseReturnValue:
    """Every task of one case, in the order they were added."""
    case_store, task_store, _, _ = _stores()
    accessor = current_accessor()

    _reachable_case_or_404(case_store, accessor, case_id, "case.read")
    today = datetime.now(UTC).date()
    tasks = task_store.list_for_case(case_id)
    return jsonify({"tasks": [task_json(t, today=today) for t in tasks]}), 200


@blueprint.get("/v1/cases/<case_id>/tasks/<task_id>")
@require_auth
@requires(TASKS, VIEW_ONLY)
def get_task_route(case_id: str, task_id: str) -> ResponseReturnValue:
    case_store, task_store, _, _ = _stores()
    accessor = current_accessor()

    _reachable_case_or_404(case_store, accessor, case_id, "case.read")
    task = task_store.get(case_id, task_id)
    if task is None:
        raise NotFoundError("task not found")
    return jsonify(task_json(task, today=datetime.now(UTC).date())), 200


@blueprint.patch("/v1/cases/<case_id>/tasks/<task_id>")
@require_auth
@requires(TASKS, ADD_EDIT)
def update_task_route(case_id: str, task_id: str) -> ResponseReturnValue:
    """Edit, reassign, complete or reopen a task — one PATCH body for all
    four, since a case-overview row does them as one edit. See
    core/tasks.TaskChanges and `apply_task_changes` for how `done` and
    `completedAt` move together.
    """
    case_store, task_store, firm_store, _ = _stores()
    accessor = current_accessor()

    changes = parse_task_update(_json_body())
    _reachable_case_or_404(case_store, accessor, case_id, "case.update")

    stored = task_store.get(case_id, task_id)
    if stored is None:
        raise NotFoundError("task not found")

    new_assignee = changes.assignee_subject
    if new_assignee is not UNSET and new_assignee is not None:
        assert isinstance(new_assignee, str)  # narrowed by the UNSET/None checks above
        _validate_assignee(firm_store, accessor, new_assignee)

    updated = apply_task_changes(stored, changes)
    written = task_store.update(updated)
    if written is None:
        # Deleted while this request was in flight — the same 404 a foreign
        # id gets, rather than resurrecting the record.
        raise NotFoundError("task not found")
    logger.info("task updated", extra={"case_id": case_id, "id": task_id})
    return jsonify(task_json(written, today=datetime.now(UTC).date())), 200


@blueprint.delete("/v1/cases/<case_id>/tasks/<task_id>")
@require_auth
@requires(TASKS, ADD_EDIT)
def delete_task_route(case_id: str, task_id: str) -> ResponseReturnValue:
    case_store, task_store, _, _ = _stores()
    accessor = current_accessor()

    _reachable_case_or_404(case_store, accessor, case_id, "case.update")
    if not task_store.delete(case_id, task_id):
        raise NotFoundError("task not found")
    logger.info("task deleted", extra={"case_id": case_id, "id": task_id})
    return "", 204


@blueprint.get("/v1/me/tasks")
@require_auth
@requires(TASKS, VIEW_ONLY)
def list_my_tasks_route() -> ResponseReturnValue:
    """Every open-or-not task assigned to the caller, across every case in
    their firm they can reach — the module docstring explains why this walks
    `CaseStore.list_for_accessor` rather than a cross-case store query.

    Sorted by due date (soonest first, undated tasks last), tie-broken by
    creation order — a worklist reads soonest-due-first; a plain per-case
    list (`GET /v1/cases/<id>/tasks`) stays creation-order because that is the
    order a schedule was built in, a different question from what to do next.
    """
    case_store, task_store, _, _ = _stores()
    accessor = current_accessor()

    reachable_case_ids: list[str] = []
    cursor: str | None = None
    while True:
        page = case_store.list_for_accessor(
            accessor, limit=MAX_LIST_LIMIT, cursor=cursor
        )
        reachable_case_ids.extend(case.id for case in page.cases)
        cursor = page.next_cursor
        if cursor is None:
            break

    today = datetime.now(UTC).date()
    mine = [
        task
        for case_id in reachable_case_ids
        for task in task_store.list_for_case(case_id)
        if task.assignee_subject == accessor.subject
    ]
    mine.sort(key=lambda t: (t.due_date is None, t.due_date or "", t.created_at, t.id))
    return jsonify({"tasks": [task_json(t, today=today) for t in mine]}), 200
