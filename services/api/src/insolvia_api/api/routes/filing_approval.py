"""The attorney's per-filing approval (ADR 0024, guardrail 1) under
`/v1/cases/<id>/filing-approval` — core/filing_approval.py is the rule.

    GET     what would be approved (the filing set, its checklist, the fee
            handling, the digest over all of it) and the case's current
            approval — VOIDED, recorded, if the filing set has changed since
    POST    approve: `{digest, credential_id?}`, the digest the client
            rendered. 201 with the same view; the one producer of a filing
            job
    DELETE  cancel a pending approval before the filing worker uses it

and, under `/v1/cases/<id>/filings/<filing_id>/resolution` (ADR 0024 PR 8):

    POST    the attorney's answer to a hand-back or an unknown outcome —
            `{outcome: "filed" | "not_filed", docket_checked: true,
            case_number?, filed_at?, confirmation_document_id?}`;
            core/filing_outcome.py is the rule. 200 with the same view.

The view carries `filing` — the current approval's filing record, as the
worker left it (`insolvia_core.filings.filing_json`) — once the worker has
claimed it: its state, the court's confirmation, the hand-back note, the
receipt document and the resolution.

THE GATES, in the order they run: `@require_auth`; `cases` view (the case is
resolved under the caller's accessor, an undistinguishing 404 otherwise, the
read recorded); `electronic_filing` — hidden for every role until an admin
grants it, so a paralegal is refused here before anything else (403); then,
for POST, everything `approve_filing` refuses — a sign-in older than
APPROVAL_SIGN_IN_MAX_AGE_SECONDS (403 ReauthenticationRequired, checked here
AND in the domain function), not an attorney (403), a filing set with a
blocker (409 FilingSetNotReady, with the blocker ids), a digest other than
today's (409), no current authorization or no openable court login of the
caller's own for this court (403), a filing already in flight (409). No
attorney id appears in any URL or body: the approver is the token's
subject, and the credential is looked up in the approver's own partition —
"only the credential's owner may approve" by construction.

WHAT IS SHOWN IS WHAT IS APPROVED. The client renders `basis` and posts its
`digest` back; the route recomputes the basis from the stores and refuses a
mismatch, so an approval can never be recorded for something the attorney
was not looking at (the authorization text's own pattern).

Responses are `Cache-Control: no-store` — an approval is an instruction to
file, and nothing between here and the browser should keep one.
"""

from __future__ import annotations

import logging
import time
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import Final

from flask import Blueprint, Response, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.auth import ReauthenticationRequiredError
from insolvia_core.cases import Case
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.filings import filing_json
from insolvia_core.firms import ADD_EDIT, CASES, ELECTRONIC_FILING, VIEW_ONLY
from insolvia_core.ports import (
    AccessLog,
    CaseEntityStore,
    CaseStore,
    DebtorStore,
    FilingStore,
)

from insolvia_api.api.auth import (
    current_accessor,
    current_principal,
    require_auth,
    require_fresh_sign_in,
    requires,
)
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.filing_approval import (
    APPROVAL_SIGN_IN_MAX_AGE_SECONDS,
    ApprovalBasis,
    FilingApproval,
    FilingQueueUnavailableError,
    FilingSetNotReadyError,
    approval_json,
    approve_filing,
    basis_for_case,
    basis_json,
    cancel_approval,
    current_approval,
)
from insolvia_api.core.filing_outcome import (
    ResolutionRefusedError,
    parse_resolution,
    resolve_filing,
)
from insolvia_api.core.packet_assembly import CaseData
from insolvia_api.core.ports import FilingApprovalStore, PacketStore

logger = logging.getLogger(__name__)

blueprint = Blueprint("filing_approval", __name__)

# The resolution route's clock — a seam so the unit tier can stand at a
# far-future date the fixtures name (the lease, the petition-date window).
resolution_clock: Callable[[], float] = time.time

# A digest and maybe a credential id. The cap refuses anything else first.
MAX_REQUEST_BYTES: Final = 1024


@blueprint.errorhandler(FilingSetNotReadyError)
def _not_ready(error: FilingSetNotReadyError) -> ResponseReturnValue:
    return (
        jsonify(
            {
                "error": "FilingSetNotReady",
                "message": str(error),
                "blockers": list(error.blockers),
            }
        ),
        409,
    )


@blueprint.errorhandler(ResolutionRefusedError)
def _resolution_refused(error: ResolutionRefusedError) -> ResponseReturnValue:
    return (
        jsonify(
            {
                "error": "ResolutionRefused",
                "message": str(error),
                "reason": error.reason,
            }
        ),
        409,
    )


@blueprint.errorhandler(FilingQueueUnavailableError)
def _queue_unavailable(error: FilingQueueUnavailableError) -> ResponseReturnValue:
    logger.error("filing queue send failed; the approval was voided")
    return jsonify({"error": "FilingQueueUnavailable", "message": str(error)}), 503


@dataclass(frozen=True)
class _Stores:
    case_store: CaseStore
    debtor_store: DebtorStore
    entity_store: CaseEntityStore
    packet_store: PacketStore
    approvals: FilingApprovalStore
    filings: FilingStore
    access_log: AccessLog


def _stores() -> _Stores:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.debtor_store is None
        or deps.case_entity_store is None
        or deps.packet_store is None
        or deps.access_log is None
        or deps.filing_approval_store is None
        or deps.filing_store is None
    ):
        raise RuntimeError(
            "case, debtor, entity, packet, approval and filing stores and the"
            " access log are not composed"
        )
    return _Stores(
        case_store=deps.case_store,
        debtor_store=deps.debtor_store,
        entity_store=deps.case_entity_store,
        packet_store=deps.packet_store,
        approvals=deps.filing_approval_store,
        filings=deps.filing_store,
        access_log=deps.access_log,
    )


def _no_store(response: Response, status: int = 200) -> Response:
    response.headers["Cache-Control"] = "no-store"
    response.status_code = status
    return response


def _case(deps: _Stores, case_id: str) -> Case:
    accessor = current_accessor()
    case = deps.case_store.get(case_id, accessor=accessor)
    deps.access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="case.read",
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        raise NotFoundError("case not found")
    return case


def _basis(deps: _Stores, case: Case) -> tuple[CaseData, ApprovalBasis]:
    return basis_for_case(
        case,
        debtor_store=deps.debtor_store,
        entity_store=deps.entity_store,
        packet_store=deps.packet_store,
        as_of=datetime.now(UTC).date(),
    )


def _view(
    deps: _Stores,
    basis: ApprovalBasis,
    approval: FilingApproval | None,
    *,
    now: float,
) -> dict[str, object]:
    body: dict[str, object] = {"basis": basis_json(basis)}
    if approval is not None:
        body["approval"] = approval_json(approval, now=now)
        filing = deps.filings.get(approval.case_id, approval.filing_id)
        if filing is not None:
            body["filing"] = filing_json(filing)
    return body


@blueprint.get("/v1/cases/<case_id>/filing-approval")
@require_auth
@requires(CASES, VIEW_ONLY)
@requires(ELECTRONIC_FILING, VIEW_ONLY)
def read_filing_approval(case_id: str) -> ResponseReturnValue:
    deps = _stores()
    case = _case(deps, case_id)
    _, basis = _basis(deps, case)
    now = time.time()
    approval = current_approval(
        case.id,
        basis=basis,
        principal=current_accessor().subject,
        now=now,
        approvals=deps.approvals,
        access_log=deps.access_log,
    )
    return _no_store(jsonify(_view(deps, basis, approval, now=now)))


@blueprint.post("/v1/cases/<case_id>/filing-approval")
@require_auth
@requires(CASES, VIEW_ONLY)
@requires(ELECTRONIC_FILING, ADD_EDIT)
def approve_filing_route(case_id: str) -> ResponseReturnValue:
    deps = _stores()
    vault = dependencies()
    if (
        vault.filing_credential_store is None
        or vault.filing_authorization_store is None
    ):
        raise RuntimeError("the filing-credential vault is not composed")
    if vault.filing_queue is None:
        # The image ahead of the infra (dependencies.py): refuse before
        # anything is recorded.
        return _no_store(
            jsonify(
                {
                    "error": "FilingQueueUnavailable",
                    "message": "Filing is not configured in this environment.",
                }
            ),
            503,
        )
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 1 KiB")
    payload = request.get_json(silent=True)
    case = _case(deps, case_id)
    accessor = current_accessor()
    data, basis = _basis(deps, case)
    now = time.time()
    try:
        # The route's own check, before the domain function repeats it — a
        # stale session is refused before the body is even parsed.
        require_fresh_sign_in(APPROVAL_SIGN_IN_MAX_AGE_SECONDS)
    except ReauthenticationRequiredError:
        deps.access_log.record(
            record_access(
                case_id=case.id,
                principal=accessor.subject,
                action="filing.approve",
                outcome="denied",
                purpose="reauthentication_required",
            )
        )
        raise
    approval = approve_filing(
        payload,
        data=data,
        basis=basis,
        firm_id=accessor.firm_id,
        attorney_id=accessor.subject,
        role=accessor.user.role,
        authenticated_at=current_principal().authenticated_at,
        now=now,
        credentials=vault.filing_credential_store,
        authorizations=vault.filing_authorization_store,
        approvals=deps.approvals,
        filings=deps.filings,
        queue=vault.filing_queue,
        access_log=deps.access_log,
    )
    # Metadata only: that an approval happened, never who or which case.
    logger.info("filing approved and queued")
    return _no_store(jsonify(_view(deps, basis, approval, now=now)), 201)


@blueprint.delete("/v1/cases/<case_id>/filing-approval")
@require_auth
@requires(CASES, VIEW_ONLY)
@requires(ELECTRONIC_FILING, VIEW_ONLY)
def cancel_filing_approval(case_id: str) -> ResponseReturnValue:
    """Cancel the pending approval. VIEW_ONLY, withdraw's reasoning: it only
    ever takes authority away, so anyone at the firm who can see the case
    and the feature may stop a filing — and no fresh sign-in is asked."""
    deps = _stores()
    case = _case(deps, case_id)
    _, basis = _basis(deps, case)
    now = time.time()
    voided = cancel_approval(
        case.id,
        principal=current_accessor().subject,
        now=now,
        approvals=deps.approvals,
        access_log=deps.access_log,
    )
    logger.info("filing approval cancelled")
    return _no_store(jsonify(_view(deps, basis, voided, now=now)))


@blueprint.post("/v1/cases/<case_id>/filings/<filing_id>/resolution")
@require_auth
@requires(CASES, ADD_EDIT)
@requires(ELECTRONIC_FILING, ADD_EDIT)
def resolve_filing_route(case_id: str, filing_id: str) -> ResponseReturnValue:
    """Record what the court's docket shows for a handed-back or
    unknown-outcome filing (ADR 0024 PR 8). `cases` add/edit, because a
    `filed` outcome moves the case through its lifecycle as a PATCH would;
    the attorney check (only the filing's own) is the domain function's.
    No fresh sign-in: nothing here reaches a court (core/filing_outcome.py
    says why that is safe)."""
    deps = _stores()
    documents = dependencies().document_store
    if documents is None:
        raise RuntimeError("the document store is not composed")
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 1 KiB")
    case = _case(deps, case_id)
    filing = deps.filings.get(case.id, filing_id)
    if filing is None:
        raise NotFoundError("filing not found")
    resolution = parse_resolution(request.get_json(silent=True))
    accessor = current_accessor()
    now = resolution_clock()
    resolve_filing(
        resolution,
        filing=filing,
        case=case,
        principal=accessor.subject,
        now=now,
        filings=deps.filings,
        documents=documents,
        access_log=deps.access_log,
    )
    # Metadata only: that a resolution happened and which way, never who.
    logger.info("filing resolved: %s", resolution.outcome)
    fresh = deps.case_store.get(case.id, accessor=accessor) or case
    _, basis = _basis(deps, fresh)
    approval = current_approval(
        fresh.id,
        basis=basis,
        principal=accessor.subject,
        now=now,
        approvals=deps.approvals,
        access_log=deps.access_log,
    )
    return _no_store(jsonify(_view(deps, basis, approval, now=now)))
