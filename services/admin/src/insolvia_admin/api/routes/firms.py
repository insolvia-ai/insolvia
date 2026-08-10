"""Cross-tenant firm administration (#212) — the provisioning surface #178
asked for instead of a shell script.

FIRM IDS APPEAR IN THESE URLS, which ADR 0009 forbade the tenant API — and
ADR 0011 records why the rule does not transfer: that rule's threat model was
a FIRM USER naming somebody else's firm, where the id would be a scope claim
the caller chose. A staff principal is cross-tenant by definition;
authorization here is "is this a verified Workspace identity" (settled in
@require_staff before any handler runs), the firm id is the OBJECT of the
operation rather than a scope, and every mutation writes an audit row naming
the id it acted on.

EVERY MUTATION AUDITS — that is #178's hard requirement ("record who
provisioned what"), and the write happens after the mutation succeeds: the
audit table records what HAPPENED, and the firm item itself is the record of
what exists. A failed mutation raises before reaching the audit call, and the
error path is the log line, not a row.
"""

from __future__ import annotations

from collections.abc import Mapping

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.errors import FieldValidationError, NotFoundError
from insolvia_core.firms import (
    FIRM_STATUSES,
    create_firm,
    create_firm_user,
    firm_json,
    firm_user_json,
    parse_firm_creation,
    parse_firm_user_creation,
    set_firm_status,
)
from insolvia_core.ports import FirmStore, UserDirectory

from insolvia_admin.api.auth import current_staff, require_staff
from insolvia_admin.api.dependencies import dependencies
from insolvia_admin.core.audit import AuditLog, record_event

blueprint = Blueprint("firms", __name__)


def _store() -> FirmStore:
    return dependencies().firm_store


def _directory() -> UserDirectory:
    return dependencies().user_directory


def _audit() -> AuditLog:
    return dependencies().audit_log


def _body() -> Mapping[str, object]:
    payload = request.get_json(silent=True)
    if not isinstance(payload, Mapping):
        raise FieldValidationError({"body": "A JSON object body is required."})
    return payload


@blueprint.post("/v1/firms")
@require_staff
def provision_firm_route() -> ResponseReturnValue:
    """Provision a firm and its first administrator — #178's whole subject.

    Body: {"name": ..., "admin": {"email": ..., "displayName": ...}}.

    The first administrator is an ADMIN BY CONSTRUCTION, not by request:
    `isAdmin` is forced true whatever the body says, because a firm whose
    first user cannot administer it is bricked at birth (the API refuses the
    edit that would fix it — ADR 0009's last-admin rule — and self-signup is
    off). Role defaults to attorney and is accepted from the body; the
    permission axes beyond that are the firm's own business later.

    ORDER: pool account first, then rows, then audit — the same recovery
    story as the tenant API's invite route. A duplicate address dies as a 409
    before any row exists; a row failure strands only a pool account, which
    is a handled state everywhere (no accessor resolves for it).
    """
    payload = _body()
    firm_draft = parse_firm_creation(payload)

    admin_payload = payload.get("admin")
    if not isinstance(admin_payload, Mapping):
        raise FieldValidationError(
            {"admin": "A firm needs its first administrator: {email, displayName}."}
        )
    admin_draft = parse_firm_user_creation(
        {
            "email": admin_payload.get("email"),
            "displayName": admin_payload.get("displayName"),
            "role": admin_payload.get("role", "attorney"),
            "isAdmin": True,
        }
    )

    staff = current_staff()

    subject = _directory().create_user(admin_draft.email)

    firm = create_firm(
        firm_draft,
        created_by=staff.subject,
        created_by_email=staff.email,
    )
    _store().create_firm(firm)

    admin_user = create_firm_user(admin_draft, firm_id=firm.id, subject=subject)
    _store().add_user(admin_user)

    _audit().record(
        record_event(
            firm_id=firm.id,
            action="firm.provision",
            principal=staff.subject,
            principal_email=staff.email,
            detail=f"{firm.name} · first administrator {admin_user.email}",
        )
    )

    return jsonify({"firm": firm_json(firm), "admin": firm_user_json(admin_user)}), 201


@blueprint.get("/v1/firms")
@require_staff
def list_firms_route() -> ResponseReturnValue:
    """Every firm, with a seat count. The count is len(list_users) per firm —
    honest about its price at tens-of-firms scale, and the revisit trigger
    lives on the FirmStore.list_firms port, not here."""
    store = _store()
    return jsonify(
        {
            "firms": [
                {**firm_json(firm), "userCount": len(store.list_users(firm.id))}
                for firm in store.list_firms()
            ]
        }
    )


@blueprint.get("/v1/firms/<firm_id>")
@require_staff
def get_firm_route(firm_id: str) -> ResponseReturnValue:
    firm = _store().get_firm(firm_id)
    if firm is None:
        raise NotFoundError("no such firm")
    return jsonify({**firm_json(firm), "userCount": len(_store().list_users(firm.id))})


@blueprint.patch("/v1/firms/<firm_id>")
@require_staff
def update_firm_status_route(firm_id: str) -> ResponseReturnValue:
    """Suspend or reactivate. `status` is the ONLY writable field here — the
    firm's name belongs to the firm's own admins (issue #217), and keeping
    this route single-purpose is what keeps its audit row unambiguous.

    Enforcement is immediate and free: the tenant API re-reads the firm on
    every authenticated request precisely so a suspended firm is actually
    suspended (ADR 0009), so there is no cache to bust and no propagation to
    wait out.
    """
    payload = _body()
    status = payload.get("status")
    if not isinstance(status, str) or status not in FIRM_STATUSES:
        raise FieldValidationError(
            {"status": "Status must be one of " + ", ".join(FIRM_STATUSES) + "."}
        )

    store = _store()
    firm = store.get_firm(firm_id)
    if firm is None:
        raise NotFoundError("no such firm")

    updated = store.update_firm(set_firm_status(firm, status))
    if updated is None:
        # Deleted between the read and the write; the same 404 an unknown id
        # gets, and update_firm's condition is what stopped a resurrection.
        raise NotFoundError("no such firm")

    staff = current_staff()
    _audit().record(
        record_event(
            firm_id=firm.id,
            action="firm.suspend" if status == "suspended" else "firm.reactivate",
            principal=staff.subject,
            principal_email=staff.email,
        )
    )

    return jsonify(firm_json(updated))


@blueprint.get("/v1/firms/<firm_id>/users")
@require_staff
def list_firm_users_route(firm_id: str) -> ResponseReturnValue:
    """A firm's staff list, admin-shaped (the full firm_user_json — this
    caller administers firms, which is exactly the reader that JSON's
    docstring admits)."""
    store = _store()
    if store.get_firm(firm_id) is None:
        raise NotFoundError("no such firm")
    return jsonify(
        {"users": [firm_user_json(user) for user in store.list_users(firm_id)]}
    )


@blueprint.post("/v1/firms/<firm_id>/users/<subject>/resend-invite")
@require_staff
def resend_invite_route(firm_id: str, subject: str) -> ResponseReturnValue:
    """Re-send a stranded invitation with a fresh temporary password.

    The user is resolved FIRM-SCOPED (get_user, both halves of the key), so a
    subject from another firm answers the same 404 an unknown one does —
    cross-tenant reach is this service's job, but every read still names the
    firm it believes it is touching.

    409 for a user who has already signed in: Cognito refuses RESEND for
    CONFIRMED accounts, and the forgot-password flow is that user's way back
    in. The port's docstring owns the mapping.
    """
    store = _store()
    if store.get_firm(firm_id) is None:
        raise NotFoundError("no such firm")
    user = store.get_user(firm_id, subject)
    if user is None:
        raise NotFoundError("no such user in that firm")

    _directory().resend_invite(user.email)

    staff = current_staff()
    _audit().record(
        record_event(
            firm_id=firm_id,
            action="invite.resend",
            principal=staff.subject,
            principal_email=staff.email,
            detail=user.email,
        )
    )

    return "", 204
