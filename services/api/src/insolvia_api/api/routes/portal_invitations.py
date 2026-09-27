"""Inviting a debtor to the client portal — the FIRM side (ADR 0023).

    GET    /v1/cases/<case_id>/portal/clients                   who is invited
    POST   /v1/cases/<case_id>/portal/invitation                invite someone
    POST   /v1/cases/<case_id>/portal/clients/<subject>/resend  re-send it
    DELETE /v1/cases/<case_id>/portal/clients/<subject>         revoke access

Staff routes, authenticated as staff (`@require_auth`), gated on the
`client_portal` feature — hidden by default for every role, so today an
admin holds it and anyone else once an admin grants it. The case comes from
the path and is resolved through `CaseStore.get(accessor=...)` FIRST: a
case the caller may not see is a 404 here exactly as everywhere else, and
only a visible case reaches the binding store.

## Two messages, one purpose each

`POST .../invitation` calls `AdminCreateUser` — the one Cognito action this
service's role holds — and Cognito's own mail carries the temporary password,
which nothing here ever sees. Then the mailer carries the CONTEXT: which firm,
what to do, the portal URL, and no secret (core/mail.client_invitation_email).
Expiry is the pool's 7-day temporary-password validity; re-sending is the same
action with `MessageAction=RESEND`.

## An address Cognito already knows

`UsernameExistsException` (our ConflictError) is resolved through OUR prior
binding by email, in this firm — a refiled case re-binds the same person.
An address with no binding of ours is a 409, never a guess: it may be a
colleague's staff account, or another firm's client, and neither is ours to
attach to this case.

## The order, and the failure in the middle

Role conflicts are checked BEFORE the account is minted (a pre-flight plan),
so the common refusal — "someone already answers for debtor_2" — costs no
Cognito account. What remains is the race the store's transaction refuses,
which leaves a pool user with no binding: a state every portal route already
answers 403 for, and which a retry of this request resolves through the
by-email lookup. The other order has no such story: a binding keyed on a
subject that does not exist is invisible to everyone.
"""

from __future__ import annotations

import contextlib
import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import Accessor
from insolvia_core.access_log import record_access
from insolvia_core.clients import (
    INVITED,
    ClientBinding,
    binding_json,
    create_binding,
    parse_invitation,
    plan_binding,
    revoke,
)
from insolvia_core.errors import ConflictError, NotFoundError, ValidationError
from insolvia_core.firms import ADD_EDIT, CLIENT_PORTAL, VIEW_ONLY
from insolvia_core.ports import AccessLog, ClientBindingStore, UserDirectory

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.mail import client_invitation_email, links_for

logger = logging.getLogger(__name__)

blueprint = Blueprint("portal_invitations", __name__)

MAX_REQUEST_BYTES = 4 * 1024


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 4 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _bindings() -> ClientBindingStore:
    store = dependencies().client_binding_store
    if store is None:
        raise RuntimeError("client binding store is not composed")
    return store


def _access_log() -> AccessLog:
    log = dependencies().access_log
    if log is None:
        raise RuntimeError("access log is not composed")
    return log


def _user_directory() -> UserDirectory:
    directory = dependencies().user_directory
    if directory is None:
        raise RuntimeError("user directory is not composed")
    return directory


def _visible_case(case_id: str, accessor: Accessor, *, action: str) -> None:
    """404 unless the caller may see the case — recording the refusal, as the
    case routes do, because walking case ids is what the log is for."""
    store = dependencies().case_store
    if store is None:
        raise RuntimeError("case store is not composed")
    if store.get(case_id, accessor=accessor) is None:
        _access_log().record(
            record_access(
                case_id=case_id,
                principal=accessor.subject,
                action=action,
                outcome="denied",
            )
        )
        raise NotFoundError("case not found")


def _binding_on_case(case_id: str, subject: str, accessor: Accessor) -> ClientBinding:
    """The binding for `subject`, if it is on THIS case of the caller's firm.
    Any other answer is the same 404 — a subject on another case is not this
    route's to act on."""
    binding = _bindings().get(accessor.firm_id, subject)
    if binding is None or binding.case_id != case_id:
        raise NotFoundError("client not found on this case")
    return binding


def _send_context_mail(binding: ClientBinding, accessor: Accessor) -> None:
    """The secret-free half of the invitation. A failure here is logged, not
    raised: the binding is written and Cognito's own mail has gone, so the
    client can already sign in — and the resend route sends this again."""
    config = dependencies().config
    email = client_invitation_email(
        binding.email,
        links=links_for(config.marketing_origin),
        firm_name=accessor.firm.name,
        portal_url=f"{config.app_origin.rstrip('/')}/portal",
        recipient_name=binding.display_name,
    )
    try:
        dependencies().mailer.send(
            email,
            # Stable per binding and per send-time, so a Lambda retry of this
            # one request dedupes at the mailer while a deliberate re-send
            # (a new `updatedAt`) is a new message.
            idempotency_key=(
                f"client-invitation:{binding.case_id}:{binding.subject}:"
                f"{binding.updated_at}"
            ),
        )
    except Exception:
        logger.warning("client invitation mail not sent")


@blueprint.get("/v1/cases/<case_id>/portal/clients")
@require_auth
@requires(CLIENT_PORTAL, VIEW_ONLY)
def list_portal_clients_route(case_id: str) -> ResponseReturnValue:
    """Everyone ever invited to this case's portal, revoked included —
    oldest first. Recorded as a `case.read`."""
    accessor = current_accessor()
    _visible_case(case_id, accessor, action="case.read")
    _access_log().record(
        record_access(case_id=case_id, principal=accessor.subject, action="case.read")
    )
    clients = _bindings().list_for_case(case_id)
    return jsonify({"clients": [binding_json(b) for b in clients]}), 200


@blueprint.post("/v1/cases/<case_id>/portal/invitation")
@require_auth
@requires(CLIENT_PORTAL, ADD_EDIT)
def invite_portal_client_route(case_id: str) -> ResponseReturnValue:
    """Invite a debtor: `{email, displayName, roles?}` — `roles` defaults to
    `["debtor_1"]`; `["debtor_1", "debtor_2"]` is the firm's explicit choice
    of one login for both spouses, and the access-log row records it."""
    accessor = current_accessor()
    _visible_case(case_id, accessor, action="client.invite")
    draft = parse_invitation(_json_body())
    bindings = _bindings()

    # PRE-FLIGHT, before an account exists: would these roles empty another
    # client's binding? Planned against a subject-less draft, which no
    # existing binding can equal.
    on_case = bindings.list_for_case(case_id)
    plan_binding(
        create_binding(
            draft,
            firm_id=accessor.firm_id,
            case_id=case_id,
            subject="",
            invited_by=accessor.subject,
        ),
        previous=None,
        on_case=on_case,
    )

    try:
        subject = _user_directory().create_user(draft.email)
    except ConflictError:
        prior = bindings.find_by_email(accessor.firm_id, draft.email)
        if prior is None:
            raise ConflictError(
                "that email address already has an Insolvia account that is not "
                "one of your firm's clients"
            ) from None
        subject = prior.subject

    binding = create_binding(
        draft,
        firm_id=accessor.firm_id,
        case_id=case_id,
        subject=subject,
        invited_by=accessor.subject,
    )
    narrowed = plan_binding(
        binding,
        previous=bindings.get(accessor.firm_id, subject),
        on_case=on_case,
    )
    bindings.bind(binding, narrowed=narrowed)

    _access_log().record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="client.invite",
            roles=binding.roles,
        )
    )
    _send_context_mail(binding, accessor)
    # No address, no subject, no name: that SOME client was invited is all a
    # request log needs.
    logger.info("portal client invited", extra={"narrowed": len(narrowed)})
    return jsonify(binding_json(binding)), 201


@blueprint.post("/v1/cases/<case_id>/portal/clients/<subject>/resend")
@require_auth
@requires(CLIENT_PORTAL, ADD_EDIT)
def resend_portal_invitation_route(case_id: str, subject: str) -> ResponseReturnValue:
    """Re-send an invitation that has not been used yet.

    Cognito re-sends its password mail (`MessageAction=RESEND`, a fresh
    temporary password) — unless the account has already chosen a password,
    which is the refiled-case shape: then only our context mail goes, and it
    says to use the existing password. A client who has already used the
    portal (`active`) has nothing to be re-invited to: 409."""
    accessor = current_accessor()
    _visible_case(case_id, accessor, action="client.invite")
    binding = _binding_on_case(case_id, subject, accessor)
    if binding.status != INVITED:
        raise ConflictError(
            "only an invitation that has not been used yet can be re-sent"
        )
    # A ConflictError is a CONFIRMED account — a password of its own already.
    with contextlib.suppress(ConflictError):
        _user_directory().resend_invite(binding.email)

    _access_log().record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="client.invite",
            roles=binding.roles,
        )
    )
    _send_context_mail(binding, accessor)
    logger.info("portal invitation re-sent")
    return "", 204


@blueprint.delete("/v1/cases/<case_id>/portal/clients/<subject>")
@require_auth
@requires(CLIENT_PORTAL, ADD_EDIT)
def revoke_portal_client_route(case_id: str, subject: str) -> ResponseReturnValue:
    """Withdraw a client's access. The binding flips to `revoked` and its
    roles are released; the client's next request answers 403 with a
    `denied` row, bounded by their 1-hour access token. The Cognito account
    survives — this service holds no AdminDeleteUser — and the revoked row
    is kept as the record that access was once granted."""
    accessor = current_accessor()
    _visible_case(case_id, accessor, action="client.revoke")
    binding = _binding_on_case(case_id, subject, accessor)
    written = _bindings().update(revoke(binding))
    if written is None:
        raise NotFoundError("client not found on this case")

    _access_log().record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="client.revoke",
            roles=written.roles,
        )
    )
    logger.info("portal client revoked")
    return jsonify(binding_json(written)), 200
