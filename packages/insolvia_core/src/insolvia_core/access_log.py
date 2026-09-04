"""Who read or changed which case (issue 8.3).

The log that answers the question CloudTrail structurally cannot. Per
docs/adr/0001 the API's execution role is the only principal AWS ever sees,
so every CloudTrail data event on the case table names
`insolvia-<env>-api-role` and never the person behind the request. The
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
from datetime import UTC, datetime

# What happened. Kept coarse — this records that an access occurred and by
# whom, not a diff. Reconstructing what changed is the case record's job.
#
# The `document.*` verbs (issue 8.6) EXTEND the tuple rather than reusing
# case.read/case.update, and the coarseness rule above is what argues for it
# rather than against it. Coarse means "no diffs", not "one row looks like
# every other row": these four are the events where the actual bytes of a
# client's tax return are handed out or destroyed, and folding them into
# case.read would make a download indistinguishable from opening the case list.
# The question this table exists to answer is "who saw this file", and under
# the reused verb the answer for the file that matters most would be "someone
# looked at the case, possibly".
#
# document.download is separate from document.read for the same reason at
# smaller scale: reading the list learns what documents exist, and downloading
# is the one that produces a decryptable copy outside this account. They are
# different disclosures and should not share a row.
#
# job.accept (ADR 0018) EXTENDS the tuple for the same reason the document
# verbs did: accepting a pipeline job sets machinery in motion that will read
# the whole case file (packet assembly renders every schedule; AI review
# reads the assembled petition), and folding that into case.update would make
# "someone started a full-file process" indistinguishable from a district
# edit. The worker's own reads are the accepting user's delegated access —
# recording them per-read is 9.6/9.7's obligation when those workers arrive;
# the skeleton's echo worker reads nothing.
# candidate.propose / candidate.withdraw (issue #262) EXTEND the tuple the
# way job.accept did: an agent proposing records through the MCP surface is
# not a case write — candidates live outside the case — but it is machinery
# aimed at one, and folding it into case.read would make "an agent queued
# twenty records for review" indistinguishable from opening the case.
ACTIONS = (
    "case.create",
    "case.read",
    "case.update",
    "document.create",
    "document.read",
    "document.download",
    "document.delete",
    "job.accept",
    "candidate.propose",
    "candidate.withdraw",
)

# Whether the caller got the data. A denied read is the more interesting row
# of the two: it is what someone probing for other people's cases looks like.
OUTCOMES = ("allowed", "denied")

# NOTHING EXPIRES. This module used to stamp an `expiresAt` on every row for a
# TTL the table does not have and cannot be given — enabling TTL on a table
# encrypted under the case key needs kms:Decrypt by the caller, which the
# deploy role is explicitly denied (infra/modules/case_store/main.tf spells out
# why that deny stays). Retention is a compliance decision the regulatory
# register owns; until it is made, rows are kept.
#
# The stamp was inert regardless, and worth recording as the reason not to
# reintroduce one speculatively: it was written as `expiresAt` while the table
# was configured for `expires_at`, so even where TTL was enabled — a developer's
# own dev env, applied by a principal the deny does not name — nothing was ever
# going to expire. It read exactly like working retention.


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


def access_item(event: AccessEvent) -> dict[str, str]:
    """The stored item shape, shared by both AccessLog implementations.

    PK  CASE#<case_id>                 keyed by case, because "who saw this
    SK  <recordedAt>#<eventId>         file" is the question actually asked

    No expiry attribute — see the note above.
    """
    return {
        "PK": f"CASE#{event.case_id}",
        "SK": f"{event.recorded_at}#{event.event_id}",
        "eventId": event.event_id,
        "caseId": event.case_id,
        "principal": event.principal,
        "action": event.action,
        "outcome": event.outcome,
        "recordedAt": event.recorded_at,
    }
