"""Who read or changed which case (issue 8.3).

The log that answers the question CloudTrail structurally cannot. Per
docs/adr/0001 the API's execution role is the only principal AWS ever sees,
so every CloudTrail data event on the case table names
`insolvia-api-<env>-role` and never the person behind the request. The
signed-in identity exists only inside the request, which makes this service
the only thing that can write it down.

Reads are recorded, not just writes. "Who changed this" is already answered,
and better, by the provenance fields on the record itself; "who saw this" has
no other source, and it is the question a client actually asks.

The table's IAM grant is PutItem and nothing else (infra/modules/case_store),
so this service can append an entry and can never read, amend or delete one.
That is deliberate: an audit log its own subject can rewrite is not evidence.
"""

from __future__ import annotations

import uuid
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta

# What happened. Kept coarse — this records that an access occurred and by
# whom, not a diff. Reconstructing what changed is the case record's job.
ACTIONS = ("case.create", "case.read", "case.update")

# Whether the caller got the data. A denied read is the more interesting row
# of the two: it is what someone probing for other people's cases looks like.
OUTCOMES = ("allowed", "denied")

# Kept in step with the table's TTL attribute. The number is a compliance
# decision the regulatory register owns rather than an engineering one; this
# constant is where to change it once that decision exists.
RETENTION_DAYS = 2555


@dataclass(frozen=True)
class AccessEvent:
    case_id: str
    principal: str
    action: str
    outcome: str
    recorded_at: str
    event_id: str


def record_access(
    *, case_id: str, principal: str, action: str, outcome: str = "allowed"
) -> AccessEvent:
    recorded_at = (
        datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    )
    return AccessEvent(
        case_id=case_id,
        principal=principal,
        action=action,
        outcome=outcome,
        recorded_at=recorded_at,
        event_id=str(uuid.uuid4()),
    )


def access_item(event: AccessEvent) -> dict[str, str | int]:
    """The stored item shape, shared by both AccessLog implementations.

      PK  CASE#<case_id>                 keyed by case, because "who saw this
      SK  <recordedAt>#<eventId>         file" is the question actually asked

    `expiresAt` is the table's TTL attribute and must be an epoch-seconds
    NUMBER — DynamoDB silently ignores a TTL attribute of any other type,
    which would look exactly like retention working.
    """
    expires_at = datetime.now(UTC) + timedelta(days=RETENTION_DAYS)
    return {
        "PK": f"CASE#{event.case_id}",
        "SK": f"{event.recorded_at}#{event.event_id}",
        "eventId": event.event_id,
        "caseId": event.case_id,
        "principal": event.principal,
        "action": event.action,
        "outcome": event.outcome,
        "recordedAt": event.recorded_at,
        "expiresAt": int(expires_at.timestamp()),
    }
