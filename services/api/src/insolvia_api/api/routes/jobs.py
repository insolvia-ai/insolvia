"""Pipeline job endpoints (ADR 0018, issue #271).

The API's half of the async pipeline: accept a job, read its status. The
worker Lambda beside this service does the work; per ADR 0001 the client
never sees the queue — this status read is the only window it gets.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.firms import ADD_EDIT, CASES, VIEW_ONLY
from insolvia_core.ports import AccessLog, CaseStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.extraction import EXTRACTABLE_DOCUMENT_KINDS
from insolvia_api.core.jobs import (
    DOCUMENT_SCOPED_KINDS,
    JobFailure,
    fail,
    find_active,
    job_json,
    new_job,
    parse_job_acceptance,
)
from insolvia_api.core.ports import JobQueue, JobStore

logger = logging.getLogger(__name__)

blueprint = Blueprint("jobs", __name__)

# An accept body is one short field. Anything larger is a mistake or an
# attack, and rejecting it before JSON parsing keeps both cheap.
MAX_REQUEST_BYTES = 16 * 1024


def _stores() -> tuple[CaseStore, JobStore, AccessLog]:
    """The stores every job route needs, or a loud failure.

    Optional on ApiDependencies so the existing public-route tests can build
    one without them; in a deployed environment they are always present
    (entrypoints/api_lambda.py refuses to boot without the case table both
    ride on). Reaching this branch is a composition bug, so it raises rather
    than degrading.
    """
    deps = dependencies()
    if deps.case_store is None or deps.job_store is None or deps.access_log is None:
        raise RuntimeError("case store, job store and access log are not composed")
    return deps.case_store, deps.job_store, deps.access_log


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 16 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


@blueprint.post("/v1/cases/<case_id>/jobs")
@require_auth
@requires(CASES, ADD_EDIT)
def accept_job_route(case_id: str) -> ResponseReturnValue:
    """Accept a pipeline job against this case.

    Always 202 with the job record that is now in flight — which is a FRESH
    record, or the already-active one of the same kind. One active job per
    (case, kind) is the idempotency rule: a client that cannot tell whether
    its first request landed re-POSTs and gets the same job back rather than
    a duplicate pipeline run. The two outcomes are deliberately not
    distinguished by status code; either way, "this work is in process" is
    the whole answer, and the record's own fields carry the rest.

    The case is resolved through CaseStore first, which applies the whole
    access rule — same as every sibling child-resource route.
    """
    deps = dependencies()
    if deps.job_queue is None:
        # A real deployed state, not a composition bug: the api image can
        # roll out ahead of the infra that creates the queue (see
        # ApiDependencies). 503 says "not yet", which is true; accepting and
        # never running would be a lie the status read repeats forever.
        return jsonify(
            {
                "error": "PipelineUnavailable",
                "message": "job pipeline is not available in this deployment",
            }
        ), 503
    queue: JobQueue = deps.job_queue

    case_store, job_store, access_log = _stores()
    kind, document_id = parse_job_acceptance(_json_body())
    accessor = current_accessor()

    case = case_store.get(case_id, accessor=accessor)
    if case is None:
        raise NotFoundError("case not found")

    if kind in DOCUMENT_SCOPED_KINDS:
        # Resolve the named document against the ALREADY-AUTHORISED case —
        # `case_id` is half the document store's key, so another case's
        # document id answers the same 404 a missing one does. Refusing an
        # unextractable kind here (rather than letting the worker fail the
        # job) turns a doomed accept into an immediate, fixable 400.
        document_store = dependencies().document_store
        if document_store is None:
            raise RuntimeError("document store is not composed")
        document = document_store.get(case.id, document_id or "")
        if document is None:
            raise NotFoundError("document not found")
        if document.kind not in EXTRACTABLE_DOCUMENT_KINDS:
            raise ValidationError(
                "extraction reads: " + ", ".join(EXTRACTABLE_DOCUMENT_KINDS)
            )

    existing = find_active(
        job_store.list_for_case(case.id), kind, document_id=document_id
    )
    if existing is not None:
        # The no-op repeat is not access-logged: the first accept was, and a
        # row per retry would record the client's network conditions, not a
        # new decision by a person.
        return jsonify(job_json(existing)), 202

    job = new_job(
        kind, case_id=case.id, created_by=accessor.subject, document_id=document_id
    )
    job_store.create(job)
    try:
        queue.enqueue(job)
    except Exception:
        # The row exists but nothing will deliver it — if it stayed `queued`
        # it would also block every future accept of this kind via the
        # idempotency rule. Mark it failed (best-effort) and let the 500
        # propagate; a retry then starts clean with a fresh job.
        job_store.update(
            fail(
                job,
                JobFailure(
                    category="enqueue_failed",
                    message="The job could not be handed to the pipeline.",
                ),
            ),
            expected_status="queued",
        )
        raise

    access_log.record(
        record_access(case_id=case.id, principal=accessor.subject, action="job.accept")
    )
    # GLBA: ids and the kind, nothing about the case's contents. The kind is
    # our own enum, not user input.
    logger.info(
        "job accepted", extra={"case_id": case.id, "job_id": job.id, "kind": kind}
    )
    return jsonify(job_json(job)), 202


@blueprint.get("/v1/cases/<case_id>/jobs/<job_id>")
@require_auth
@requires(CASES, VIEW_ONLY)
def get_job_route(case_id: str, job_id: str) -> ResponseReturnValue:
    """One job's status, if the caller may see its case.

    Another firm's case, the caller's own unlinked case, and a case that does
    not exist all answer the same 404 — and so does a job id from another
    case, because case_id is half the key (core/ports.JobStore). Deliberately
    not access-logged: the access log answers "who saw this file", and a
    status poll reads pipeline state, not case data — logging it would bury
    the real reads under one row per poll.
    """
    case_store, job_store, _ = _stores()
    accessor = current_accessor()

    if case_store.get(case_id, accessor=accessor) is None:
        raise NotFoundError("case not found")
    job = job_store.get(case_id, job_id)
    if job is None:
        raise NotFoundError("job not found")
    return jsonify(job_json(job)), 200
