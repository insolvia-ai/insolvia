"""The provisioning record — who did what to which firm (#178, #212).

The requirement #178 stated outright: "it must record who provisioned what."
Same posture as the tenant API's case access log, and for the same reason:
per ADR 0001 the execution role is the only principal AWS ever sees, so
CloudTrail can say `insolvia-<env>-admin-api-role` touched the firm table and can
never say which staff member. The verified staff identity exists only inside
the request, which makes this service the only thing that can write it down.

The table's IAM grant is PutItem and nothing else (infra/modules/
admin_service), so this service can append an entry and can never read, amend
or delete one. An audit log its own subject can rewrite is not evidence —
which is also why the portal's provenance DISPLAY reads the created_by fields
on the firm item, never this log.

The item shape lives here, not in the adapter, following firms.firm_item's
rule — one owner per shape — even though this table has a single writer
today: the day it grows a reader (an audit screen), the shape must already
have one home.
"""

from __future__ import annotations

import uuid
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import Protocol

# What happened. Coarse on purpose — this records that an administrative act
# occurred and by whom, not a diff; the firm item itself carries the state.
ACTIONS = (
    "firm.provision",
    "firm.suspend",
    "firm.reactivate",
    "invite.resend",
)


@dataclass(frozen=True)
class AdminEvent:
    firm_id: str
    action: str
    # Google `sub` and verified email of the staff caller — identity and
    # display. The sub is the stable one; the email is what a human reads.
    principal: str
    principal_email: str
    # What the action named, for the rows where the firm item alone cannot
    # answer: the firm's name at provision time, the address an invite went
    # to. Empty for actions the firm id fully describes.
    detail: str
    recorded_at: str
    event_id: str


def record_event(
    *,
    firm_id: str,
    action: str,
    principal: str,
    principal_email: str,
    detail: str = "",
) -> AdminEvent:
    if action not in ACTIONS:
        raise ValueError(f"unknown admin action: {action!r}")
    return AdminEvent(
        firm_id=firm_id,
        action=action,
        principal=principal,
        principal_email=principal_email,
        detail=detail,
        recorded_at=datetime.now(UTC)
        .isoformat(timespec="milliseconds")
        .replace("+00:00", "Z"),
        event_id=str(uuid.uuid4()),
    )


def event_item(event: AdminEvent) -> dict[str, str]:
    """The exact stored item shape.

    PK  FIRM#<id>                    one partition per firm, so "everything
    SK  <recordedAt>#<eventId>       that happened to this firm" is one Query
                                     in time order; the uuid suffix keeps two
                                     same-millisecond events from colliding.
    """
    return {
        "PK": f"FIRM#{event.firm_id}",
        "SK": f"{event.recorded_at}#{event.event_id}",
        "firmId": event.firm_id,
        "action": event.action,
        "principal": event.principal,
        "principalEmail": event.principal_email,
        "detail": event.detail,
        "recordedAt": event.recorded_at,
        "eventId": event.event_id,
    }


class AuditLog(Protocol):
    """The port. Write-only by design, on both sides of the boundary: no read
    method here, PutItem alone in IAM. Implemented by adapters/aws/audit_log
    (DynamoDB) and adapters/memory/audit_log (tests and the dev server)."""

    def record(self, event: AdminEvent) -> None: ...
