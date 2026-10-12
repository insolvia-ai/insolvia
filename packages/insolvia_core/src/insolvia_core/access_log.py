"""Who read or changed which case (issue 8.3) — and, since ADR 0022, which
firm client.

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
from typing import Final

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
#
# taxid.read (issue 13.12 / #382) is the row docs/reference/case-data-model.md
# promised the moment tax identifiers were stored: "the full value behind an
# explicit read that writes an audit record". It is the ONLY action that
# carries the two extra members below — which debtor (`filing_role`) and why
# (`purpose`: the form being printed) — because "someone opened the case" and
# "someone decrypted the debtor's Social Security number to print B121" are
# the two disclosures this table most needs to keep apart. Written by exactly
# one function, insolvia_core.tax_ids.read_tax_id, and never by a route.
#
# The client portal (ADR 0023) adds four, in two pairs:
#   - client.invite / client.revoke — a FIRM USER inviting (or re-inviting,
#     which is the same act) a debtor to this case, or withdrawing them. The
#     principal is the firm user. `roles` records which debtor(s) the binding
#     answers for — so "the firm chose one login for both spouses" is on the
#     record, as the ADR requires — and appears on these two rows only.
#   - portal.read / portal.answer — a CLIENT acting through the portal, the
#     principal being the client's own subject. A refused portal request (a
#     revoked binding, a suspended firm) is a `denied` row of whichever the
#     request would have been: it is what a person whose access was withdrawn
#     trying anyway looks like.
#
# The firm's client directory (ADR 0022) adds three more, and they are the
# first rows NOT keyed by a case: client.create / client.read / client.update
# name a `firm_clients.FirmClient`, a firm-scoped person with no case to be
# logged under — so the row's subject is `CLIENT#<client_id>` rather than
# `CASE#<case_id>` (see `AccessEvent.subject_key`). Same table, same
# PutItem-only grant. A single-record read is logged, because a client record
# is PII; the directory LIST is not, matching `GET /v1/cases`. An archive is a
# client.update — a status write, not a verb of its own. These share the
# `client.` prefix with the portal's client.invite / client.revoke, which are
# about a portal BINDING and stay keyed by the case they bind to.
#
# client.merge (ADR 0022's PR 7) is written TWICE per merge, once under each
# client's subject key — the merged one and the survivor — because "what
# happened to this client record" must be answerable from either row. A
# refused merge is a `denied` row under whichever id could not be reached.
#
# case.delete (issue 14.3 / #355) EXTENDS the tuple for document.delete's
# reason: deleting a matter makes the whole file — its debtors, schedules and
# documents — unreachable, and folding that into case.update would make it
# indistinguishable from a status edit. Archiving and restoring stay
# case.update: the case is still the firm's working record either way.
# Copying a case is a case.read of the source and a case.create of the copy.
#
# The credential vault (ADR 0024, guardrail 3) adds three, and they are the
# second family NOT keyed by a case: `credential.enrol`, `credential.revoke`
# and `credential.open` name an attorney's CM/ECF credential, so the row's
# subject is `CREDENTIAL#<credential_id>` and "who has used this login" is
# one partition read. The principal is the attorney — on enrol and revoke
# because they are the only person who may, on open because the worker acts
# on their per-filing approval (packet assembly's "recorded against the
# preparer whose accept caused the run"). `credential.open` carries two
# extra members: `purpose` (sign-in, or the re-check before the final
# submit) and `filing_id`, the filing it was opened for — ADR 0024's "an
# application row naming the attorney, the filing, and the purpose".
# Written by exactly one module, insolvia_core.filing_credentials. No
# credential.read: the status view opens nothing and is the attorney
# looking at their own settings, as GET /v1/me is.
#
# The written authorization (ADR 0024, guardrail 2) adds two more, the third
# family not keyed by a case: `authorization.sign` and
# `authorization.withdraw`, subject `AUTHORIZATION#<authorization_id>` —
# the signature's own id, whose history item holds the version and digest.
# The principal is the attorney, the only person who may do either. A
# withdrawal also writes one `credential.revoke` row per credential it
# destroys, so "who ended this login" reads the same either way it ended.
# Written by insolvia_core.filing_authorization (sign) and
# insolvia_core.filing_credentials (withdraw), and nowhere else.
#
# The per-filing approval (ADR 0024, guardrail 1) adds three, keyed by the
# CASE again — an approval is an act on one matter, and "who approved filing
# this case, and what became of it" is one partition read: `filing.approve`,
# `filing.void` and `filing.consume`. Each carries `filing_id` — the approval's
# own filing id, the same member `credential.open` carries, so the approval,
# its consumption and every open of the attorney's login for it join on one
# value. `purpose` says why on the two that need it: on `filing.void` the
# reason (`changed`, `superseded`, `cancelled`, `enqueue_failed`), on a
# denied `filing.approve` the refusal. The principal is the attorney on
# approve and consume (the worker consumes on their approval, as it opens
# their login), and whoever's act caused a void. Written by services/api's
# core/filing_approval.py, and nowhere else.
#
# The filing worker (ADR 0024 PR 7) adds two, keyed by the case and carrying
# `filing_id`, principal the approving attorney (the worker acts on their
# approval): `filing.submit` — the final submit, `purpose` its result
# (`submitted`, or `outcome_unknown:<reason>` when nobody can know what the
# court did) — and `filing.hand_back`, a run stopped before the final submit,
# `purpose` the reason. Its reads of the case record for the approval's
# digest are ordinary `case.read` rows with `purpose` `filing_digest`.
# Written by services/filing's core/worker.py, and nowhere else. Since ADR
# 0024 PR 8 a `filing.submit` with `purpose` `case_filed` also records the
# capture that filed the CASE (`filed` when the case was already filed by
# hand and only the record moved).
#
# Filed-state capture (ADR 0024 PR 8) adds one: `filing.resolve`, the
# attorney's answer to a hand-back or an unknown outcome — keyed by the case,
# carrying `filing_id`, principal the attorney, `purpose` the outcome
# (`filed` / `not_filed`) or, denied, the refusal. Written by services/api's
# core/filing_outcome.py, and nowhere else.
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
    "taxid.read",
    "client.invite",
    "client.revoke",
    "portal.read",
    "portal.answer",
    "client.create",
    "client.read",
    "client.update",
    "client.merge",
    "case.delete",
    "credential.enrol",
    "credential.revoke",
    "credential.open",
    "authorization.sign",
    "authorization.withdraw",
    "filing.approve",
    "filing.void",
    "filing.consume",
    "filing.submit",
    "filing.hand_back",
    "filing.resolve",
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


# The subject-key prefixes. A row is about exactly one of these.
CASE_SUBJECT: Final = "CASE"
CLIENT_SUBJECT: Final = "CLIENT"
CREDENTIAL_SUBJECT: Final = "CREDENTIAL"
AUTHORIZATION_SUBJECT: Final = "AUTHORIZATION"


@dataclass(frozen=True)
class AccessEvent:
    # WHAT was accessed, as the table's partition key: `CASE#<case_id>` for
    # every row until ADR 0022, `CLIENT#<client_id>` for the client.create /
    # read / update rows. `case_id` and `client_id` below read it back.
    subject_key: str
    principal: str
    action: str
    outcome: str
    recorded_at: str
    event_id: str
    # Only `taxid.read` sets these (see ACTIONS): the debtor whose identifier
    # was opened, and the form it was opened for. `credential.open` sets
    # `purpose` too — why the credential was opened. Absent on every other row,
    # and absent means absent — the item omits them rather than storing a
    # null, so the log's older rows and the new ones read the same.
    filing_role: str | None = None
    purpose: str | None = None
    # Only `client.invite` / `client.revoke` set this: the binding's filing
    # roles, canonical order, comma-joined (`debtor_1,debtor_2`).
    roles: str | None = None
    # `credential.open` and the three `filing.*` rows set this: the filing
    # the row is about (ADR 0024).
    filing_id: str | None = None

    def _id_under(self, prefix: str) -> str | None:
        head, _, rest = self.subject_key.partition("#")
        return rest if head == prefix else None

    @property
    def case_id(self) -> str | None:
        """The case this row is about, or None on a client row."""
        return self._id_under(CASE_SUBJECT)

    @property
    def client_id(self) -> str | None:
        """The firm client this row is about, or None on a case row."""
        return self._id_under(CLIENT_SUBJECT)

    @property
    def credential_id(self) -> str | None:
        """The filing credential this row is about (ADR 0024), or None."""
        return self._id_under(CREDENTIAL_SUBJECT)

    @property
    def authorization_id(self) -> str | None:
        """The filing authorization this row is about (ADR 0024), or None."""
        return self._id_under(AUTHORIZATION_SUBJECT)


def record_access(
    *,
    case_id: str | None = None,
    client_id: str | None = None,
    credential_id: str | None = None,
    authorization_id: str | None = None,
    principal: str,
    action: str,
    outcome: str = "allowed",
    filing_role: str | None = None,
    purpose: str | None = None,
    roles: tuple[str, ...] | None = None,
    filing_id: str | None = None,
) -> AccessEvent:
    """One access-log row about exactly one subject — a case (`case_id`), a
    firm client (`client_id`), a filing credential (`credential_id`) or a filing
    authorization (`authorization_id`).
    Naming more than one, or none, is a programming error: a row about two
    things answers neither "who saw this case" nor "who saw this client"."""
    subjects = [
        (prefix, value)
        for prefix, value in (
            (CASE_SUBJECT, case_id),
            (CLIENT_SUBJECT, client_id),
            (CREDENTIAL_SUBJECT, credential_id),
            (AUTHORIZATION_SUBJECT, authorization_id),
        )
        if value is not None
    ]
    if len(subjects) != 1:
        raise ValueError(
            "an access event names exactly one of case_id, client_id, "
            "credential_id, authorization_id"
        )
    prefix, value = subjects[0]
    subject_key = f"{prefix}#{value}"
    recorded_at = (
        datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    )
    return AccessEvent(
        subject_key=subject_key,
        principal=principal,
        action=action,
        outcome=outcome,
        recorded_at=recorded_at,
        event_id=str(uuid.uuid4()),
        filing_role=filing_role,
        purpose=purpose,
        roles=",".join(roles) if roles else None,
        filing_id=filing_id,
    )


def access_item(event: AccessEvent) -> dict[str, str]:
    """The stored item shape, shared by both AccessLog implementations.

    PK  CASE#<case_id>                 keyed by what was accessed, because
        | CLIENT#<client_id>           "who saw this file" (or this person)
    SK  <recordedAt>#<eventId>         is the question actually asked

    `caseId` or `clientId` repeats the id beside the key, whichever the row
    is about. No expiry attribute — see the note above. `filingRole` and
    `purpose` appear only on the rows that carry them (a `taxid.read`).
    """
    item = {
        "PK": event.subject_key,
        "SK": f"{event.recorded_at}#{event.event_id}",
        "eventId": event.event_id,
        "principal": event.principal,
        "action": event.action,
        "outcome": event.outcome,
        "recordedAt": event.recorded_at,
    }
    if event.case_id is not None:
        item["caseId"] = event.case_id
    if event.client_id is not None:
        item["clientId"] = event.client_id
    if event.credential_id is not None:
        item["credentialId"] = event.credential_id
    if event.authorization_id is not None:
        item["authorizationId"] = event.authorization_id
    if event.filing_role is not None:
        item["filingRole"] = event.filing_role
    if event.purpose is not None:
        item["purpose"] = event.purpose
    if event.roles is not None:
        item["roles"] = event.roles
    if event.filing_id is not None:
        item["filingId"] = event.filing_id
    return item
