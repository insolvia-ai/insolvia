"""Events at case scope and at firm scope, and the deadline engine's hook
(issue 14.6 / #358).

    /v1/cases/<case_id>/events[/<event_id>]     a case's events
    /v1/firm/events[/<event_id>]                the firm's own (case-less) events

Both surfaces are gated by the `events` feature — `view_only` reads,
`add_edit` writes — and a case event goes through the case lookup that is
the only authorisation there is, exactly as every case child route does. A
firm event is scoped to the caller's resolved firm; there is no firm id in
any URL, for routes/firm.py's reason.

Two verbs beyond the usual four: PATCH takes `{"dismissed": bool}` and is
the ONE edit a generated deadline admits; PUT on a generated event is a
400, because its dates are the rule's and moving one by hand would put a
date on the calendar that the law does not support. Change the case's
anchors instead and the engine regenerates.

`regenerate_deadlines` is that engine's hook, called by the case PATCH
route whenever a change touches an anchor. It lives here rather than in
routes/cases.py so that module's diff stays a call, and it is deliberately
idempotent: regenerating against unchanged anchors writes nothing.
"""

from __future__ import annotations

import logging
from datetime import date

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import Accessor
from insolvia_core.access_log import record_access
from insolvia_core.cases import Case
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.firms import ADD_EDIT, EVENTS, VIEW_ONLY
from insolvia_core.ports import AccessLog, CaseStore, FirmStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core import deadline_rules, legal_holidays
from insolvia_api.core.deadlines import GeneratedDeadline, generate, reconcile
from insolvia_api.core.events import (
    Event,
    EventDraft,
    EventScope,
    create_event,
    event_json,
    parse_dismissal,
    parse_event,
    replace_event,
    set_dismissed,
)
from insolvia_api.core.ports import EventStore

logger = logging.getLogger(__name__)

blueprint = Blueprint("events", __name__)

# A title, a description box, a location and a list of subjects.
MAX_REQUEST_BYTES = 32 * 1024


def _event_store() -> EventStore:
    deps = dependencies()
    if deps.event_store is None:
        raise RuntimeError("event store is not composed")
    return deps.event_store


def _case_stores() -> tuple[CaseStore, AccessLog]:
    deps = dependencies()
    if deps.case_store is None or deps.access_log is None:
        raise RuntimeError("case store and access log are not composed")
    return deps.case_store, deps.access_log


def _firm_store() -> FirmStore:
    deps = dependencies()
    if deps.firm_store is None:
        raise RuntimeError("firm store is not composed")
    return deps.firm_store


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 32 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _reachable_case_or_404(accessor: Accessor, case_id: str, action: str) -> Case:
    """The case, if this accessor may see it — recorded either way, like
    every case child route. 404 for another firm's, an unlinked one, and a
    missing one alike (core/errors.py's anti-oracle rule)."""
    case_store, access_log = _case_stores()
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


def _check_attendees(accessor: Accessor, draft: EventDraft) -> None:
    """Every attendee must be somebody in the caller's firm. A 400 keyed on
    the attendee, not a 404: the subject came from the firm directory the
    caller can already read, so there is nothing to hide, and the client
    needs to know which entry to drop."""
    if not draft.attendees:
        return
    members = {u.subject for u in _firm_store().list_users(accessor.firm_id)}
    for index, subject in enumerate(draft.attendees):
        if subject not in members:
            raise ValidationError(f"attendees[{index}] is not a member of your firm")


def _event_or_404(scope: EventScope, event_id: str) -> Event:
    event = _event_store().get(scope, event_id)
    if event is None:
        raise NotFoundError("event not found")
    return event


def _log(message: str, event: Event) -> None:
    # GLBA: ids only — never a title, a location, or who is attending.
    logger.info(message, extra={"case_id": event.case_id, "id": event.id})


# ── The deadline engine's hook ──────────────────────────────────


def regenerate_deadlines(case: Case, *, actor: str) -> int:
    """Make the case's generated events agree with its anchors and chapter.

    Returns how many writes it made. Pure over the stores: reads the case's
    events, diffs against `generate`, writes the difference. A case with no
    filed date ends up with no generated events — including after a filed
    date is cleared, which is the "regenerate when the anchors change" rule
    working in reverse.
    """
    store = _event_store()
    case_store, _ = _case_stores()
    scope = EventScope(case.firm_id, case.id)
    existing = store.list_for_scope(scope)
    generated: tuple[GeneratedDeadline, ...] = ()
    if case.filed_at is not None:
        filed = date.fromisoformat(case.filed_at)
        generated = generate(
            case, deadline_rules.resolve(filed), legal_holidays.resolve(filed)
        )
    attendees = tuple(a.subject for a in case_store.assignees(case.id))
    plan = reconcile(case, generated, existing, attendees=attendees, actor=actor)
    for event in plan.create:
        store.create(event)
    for event in plan.put:
        store.put(event)
    for event in plan.delete:
        store.delete(scope, event.id)
    written = len(plan.create) + len(plan.put) + len(plan.delete)
    if written:
        logger.info(
            "case deadlines regenerated", extra={"case_id": case.id, "writes": written}
        )
    return written


# ── Case scope ──────────────────────────────────────────────────


@blueprint.get("/v1/cases/<case_id>/events")
@require_auth
@requires(EVENTS, VIEW_ONLY)
def list_case_events_route(case_id: str) -> ResponseReturnValue:
    """A case's events — hand-made and generated, dismissed included (the
    overview shows a dismissed deadline struck through rather than gone,
    so a dismissal can be undone), in start order."""
    accessor = current_accessor()
    case = _reachable_case_or_404(accessor, case_id, "case.read")
    events = _event_store().list_for_scope(EventScope(case.firm_id, case.id))
    return jsonify({"events": [event_json(e) for e in events]}), 200


@blueprint.post("/v1/cases/<case_id>/events")
@require_auth
@requires(EVENTS, ADD_EDIT)
def create_case_event_route(case_id: str) -> ResponseReturnValue:
    accessor = current_accessor()
    draft = parse_event(_json_body())
    case = _reachable_case_or_404(accessor, case_id, "case.update")
    _check_attendees(accessor, draft)
    event = create_event(
        draft, scope=EventScope(case.firm_id, case.id), created_by=accessor.subject
    )
    _event_store().create(event)
    _log("case event created", event)
    return jsonify(event_json(event)), 201


@blueprint.get("/v1/cases/<case_id>/events/<event_id>")
@require_auth
@requires(EVENTS, VIEW_ONLY)
def get_case_event_route(case_id: str, event_id: str) -> ResponseReturnValue:
    accessor = current_accessor()
    case = _reachable_case_or_404(accessor, case_id, "case.read")
    event = _event_or_404(EventScope(case.firm_id, case.id), event_id)
    return jsonify(event_json(event)), 200


@blueprint.put("/v1/cases/<case_id>/events/<event_id>")
@require_auth
@requires(EVENTS, ADD_EDIT)
def put_case_event_route(case_id: str, event_id: str) -> ResponseReturnValue:
    """Replace a hand-made event whole. A generated one answers 400 — see
    the module docstring."""
    accessor = current_accessor()
    draft = parse_event(_json_body())
    case = _reachable_case_or_404(accessor, case_id, "case.update")
    _check_attendees(accessor, draft)
    scope = EventScope(case.firm_id, case.id)
    updated = replace_event(_event_or_404(scope, event_id), draft)
    if not _event_store().put(updated):
        raise NotFoundError("event not found")
    _log("case event updated", updated)
    return jsonify(event_json(updated)), 200


@blueprint.patch("/v1/cases/<case_id>/events/<event_id>")
@require_auth
@requires(EVENTS, ADD_EDIT)
def dismiss_case_event_route(case_id: str, event_id: str) -> ResponseReturnValue:
    """`{"dismissed": true|false}` — the one edit a generated deadline
    admits, and allowed on a hand-made event too so the client has one
    control for both."""
    accessor = current_accessor()
    dismissed = parse_dismissal(_json_body())
    case = _reachable_case_or_404(accessor, case_id, "case.update")
    scope = EventScope(case.firm_id, case.id)
    updated = set_dismissed(_event_or_404(scope, event_id), dismissed)
    if not _event_store().put(updated):
        raise NotFoundError("event not found")
    _log("case event dismissal changed", updated)
    return jsonify(event_json(updated)), 200


@blueprint.delete("/v1/cases/<case_id>/events/<event_id>")
@require_auth
@requires(EVENTS, ADD_EDIT)
def delete_case_event_route(case_id: str, event_id: str) -> ResponseReturnValue:
    """Remove a hand-made event. A generated one is refused: the engine
    would only write it back on the next regeneration — dismiss it."""
    accessor = current_accessor()
    case = _reachable_case_or_404(accessor, case_id, "case.update")
    scope = EventScope(case.firm_id, case.id)
    event = _event_or_404(scope, event_id)
    if event.generated:
        raise ValidationError("a generated deadline cannot be deleted — dismiss it")
    if not _event_store().delete(scope, event_id):
        raise NotFoundError("event not found")
    _log("case event deleted", event)
    return "", 204


# ── Firm scope ──────────────────────────────────────────────────


@blueprint.get("/v1/firm/events")
@require_auth
@requires(EVENTS, VIEW_ONLY)
def list_firm_events_route() -> ResponseReturnValue:
    accessor = current_accessor()
    events = _event_store().list_for_scope(EventScope(accessor.firm_id))
    return jsonify({"events": [event_json(e) for e in events]}), 200


@blueprint.post("/v1/firm/events")
@require_auth
@requires(EVENTS, ADD_EDIT)
def create_firm_event_route() -> ResponseReturnValue:
    accessor = current_accessor()
    draft = parse_event(_json_body())
    _check_attendees(accessor, draft)
    event = create_event(
        draft, scope=EventScope(accessor.firm_id), created_by=accessor.subject
    )
    _event_store().create(event)
    _log("firm event created", event)
    return jsonify(event_json(event)), 201


@blueprint.get("/v1/firm/events/<event_id>")
@require_auth
@requires(EVENTS, VIEW_ONLY)
def get_firm_event_route(event_id: str) -> ResponseReturnValue:
    accessor = current_accessor()
    event = _event_or_404(EventScope(accessor.firm_id), event_id)
    return jsonify(event_json(event)), 200


@blueprint.put("/v1/firm/events/<event_id>")
@require_auth
@requires(EVENTS, ADD_EDIT)
def put_firm_event_route(event_id: str) -> ResponseReturnValue:
    accessor = current_accessor()
    draft = parse_event(_json_body())
    _check_attendees(accessor, draft)
    scope = EventScope(accessor.firm_id)
    updated = replace_event(_event_or_404(scope, event_id), draft)
    if not _event_store().put(updated):
        raise NotFoundError("event not found")
    _log("firm event updated", updated)
    return jsonify(event_json(updated)), 200


@blueprint.patch("/v1/firm/events/<event_id>")
@require_auth
@requires(EVENTS, ADD_EDIT)
def dismiss_firm_event_route(event_id: str) -> ResponseReturnValue:
    accessor = current_accessor()
    dismissed = parse_dismissal(_json_body())
    scope = EventScope(accessor.firm_id)
    updated = set_dismissed(_event_or_404(scope, event_id), dismissed)
    if not _event_store().put(updated):
        raise NotFoundError("event not found")
    _log("firm event dismissal changed", updated)
    return jsonify(event_json(updated)), 200


@blueprint.delete("/v1/firm/events/<event_id>")
@require_auth
@requires(EVENTS, ADD_EDIT)
def delete_firm_event_route(event_id: str) -> ResponseReturnValue:
    accessor = current_accessor()
    scope = EventScope(accessor.firm_id)
    event = _event_or_404(scope, event_id)
    if not _event_store().delete(scope, event_id):
        raise NotFoundError("event not found")
    _log("firm event deleted", event)
    return "", 204
