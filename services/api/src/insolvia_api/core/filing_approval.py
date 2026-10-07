"""The attorney's per-filing approval (ADR 0024 build PR 6, guardrail 1).

"The attorney approves each filing, in Insolvia, re-authenticating at
approval time. Nothing is submitted without that per-filing act. The approval
is made by the attorney whose credential will be used — never a paralegal, a
firm administrator or Insolvia staff — after a fresh sign-in ... It is bound
to a digest of exactly what will be filed ... Any change to any of those
after approval voids it. It is single-use and expires."

This module is that act, and the only producer of a filing job: approving
writes the approval record and hands the filing queue ONE message, built by
`filing_job_message` from the approval alone (ids, never contents). Nothing
else in the service composes a send to that queue — `tests/unit/
test_filing_approval.py` reads every call site to keep it so.

## What the digest covers — "exactly what the attorney was shown"

The authorization the attorney signed (filing_authorization, clause 2) says
the approval covers "the court and division, every document, the case-
opening data, the docket order and the fee handling". `approval_basis`
writes those down as ONE canonical JSON document, and the digest is the
SHA-256 of its bytes:

- `court`     the case's court and division codes, and the court-registry
              release the filing set was judged against (a registry
              correction can change the order, the names or the fee rule);
- `packet`    the filing-set packet's id, zip digest and size — a
              re-assembly is a new packet, so it voids even when the bytes
              come out identical;
- `documents` every document in DOCKET ORDER, each with its position, key,
              upload file name, handling (filed, own event, restricted, not
              filed) and source, and for a packet document the file's own
              SHA-256, size and page count (`PacketPart`, measured by the
              assembly worker). The order is the list order, so a reordering
              is a different digest;
- `record`    the case-opening data, and the record every document was
              rendered from: the case's chapter, court, division, district
              and exemption election; every debtor's body, tax-id reference
              (kind, last four, the sealed item's ref — never the number) and
              update time; and every case collection's records, body and all.
              Any edit to the case after approval — a debtor's address, a
              creditor, a schedule line — is a different digest, whether or
              not anybody re-assembled the packet;
- `fee`       the fee handling as it stands today: Insolvia stores and
              enters no card details, so the filing is handed back at the
              court's payment step (ADR 0024, "Filing fees are an open
              question"), with the court's fee rule as the registry records
              it.

Canonical means: `json.dumps(sort_keys=True, separators=(",", ":"),
ensure_ascii=False)`, UTF-8, dates in ISO form, decimals as their exact
string — the same document always gives the same bytes, and the scheme
name (`SCHEME`) is the first thing in it, so a later change to what is
covered is a new scheme rather than a silent re-reading of old digests.

The document is hashed and DISCARDED. It holds debtor PII, so it is never
stored and never returned; what is stored is the digest, and what the
attorney is shown (`basis_json`) is the court, the documents with their
sizes and digests, the checklist and the fee handling.

## The approval's life

`pending` → `consumed` (once, by the filing worker — `consume_approval`) or
`voided` (`changed`, `superseded`, `cancelled`, `enqueue_failed`). A pending
approval past `expires_at` reads as `expired` and can never be consumed.

- SINGLE-USE: consuming is ONE conditional write — status still `pending`,
  digest still the one approved, not yet expired. A second consume, a
  redelivered job, two workers racing: exactly one write succeeds.
- VOIDED ON ANY CHANGE: the consumer recomputes the digest from the stores
  at that moment and the write is conditional on it matching; a mismatch
  voids the approval (`changed`) and nothing is filed. The status read does
  the same comparison, so the approval screen says "voided — the filing set
  changed" as soon as anybody looks.
- ONE LIVE APPROVAL PER CASE: a case carries a pointer to its current
  approval, and a new approval moves it in the same transaction that voids
  the pending one it replaces (`superseded`). Approving while the current
  approval is CONSUMED is refused: a filing is in flight, and a second
  approval would be a second filing (PR 7/8 decide when a hand-back frees
  the case again).
- EXPIRES after `APPROVAL_TTL_SECONDS` (one hour; see the constant).

Every approve, void and consume writes an access row keyed by the case
(`filing.approve` / `filing.void` / `filing.consume`, with the filing id) —
including a refused approve, whose `purpose` names the refusal.
"""

from __future__ import annotations

import hashlib
import json
import uuid
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, fields, replace
from datetime import UTC, date, datetime
from decimal import Decimal
from typing import TYPE_CHECKING, Any, Final, Literal

from insolvia_core import courts
from insolvia_core.access_log import record_access
from insolvia_core.auth import require_recent_authentication
from insolvia_core.case_entities import CaseEntity, entity_body
from insolvia_core.cases import is_filed, partition_key
from insolvia_core.debtors import debtor_body
from insolvia_core.errors import ConflictError, ForbiddenError, ValidationError
from insolvia_core.fields import prune_body
from insolvia_core.filing_authorization import is_current
from insolvia_core.filing_credentials import (
    AuthorizationRequiredError,
    FilingCredential,
    is_openable,
)

from .filing_set import FilingDocument, FilingSet, build_filing_set
from .packet_assembly import CaseData, read_case_data
from .packets import Packet, PacketPart

if TYPE_CHECKING:
    from insolvia_core.cases import Case
    from insolvia_core.ports import (
        AccessLog,
        CaseEntityStore,
        DebtorStore,
        FilingAuthorizationStore,
        FilingCredentialStore,
    )

    from .ports import FilingApprovalStore, FilingQueue, PacketStore

# What the digest covers, by name — see the module docstring. A change to
# `approval_basis`'s document is a new scheme.
SCHEME: Final = "insolvia-filing-approval/1"

# THE FRESH SIGN-IN: how old the token's `auth_time` may be when the
# attorney presses Approve. Five minutes, guardrail 2's window and for its
# reason: read the filing set, press "Sign in again", type the password (and
# answer MFA), land back on the screen, press Approve. The reading happens
# BEFORE the sign-in, so a long review does not eat the window; and it is
# far shorter than any session the app holds, so a browser left signed in
# cannot approve for its owner.
APPROVAL_SIGN_IN_MAX_AGE_SECONDS: Final = 300

# THE APPROVAL'S LIFETIME: one hour from approval to the worker's consume.
# Long enough for the filing worker to pick the job up and, if an attempt
# dies before it claims anything, for SQS to redeliver it up to twice (the
# queue's visibility timeout is 15 minutes, its maxReceiveCount 3 —
# infra/modules/filing_queue). Short enough that an approval means "file
# this now": a backlog drained hours later — a worker outage, or, until
# ADR 0024 PR 7, the absence of any worker — files nothing on stale
# consent, and the attorney's filing goes in on the day they approved it
# rather than the next. The court-side clock (pay.gov lockouts, a 24-hour
# settle rule) is the worker's to respect once it has the approval; this
# one only bounds how long consent waits.
APPROVAL_TTL_SECONDS: Final = 3600

# Today the fee step is always handed back (module docstring, `fee`).
FEE_HANDLING: Final = "hand_back_at_payment"

ApprovalStatus = Literal["pending", "consumed", "voided"]
EffectiveStatus = Literal["pending", "consumed", "voided", "expired"]
VoidReason = Literal["changed", "superseded", "cancelled", "enqueue_failed"]
VOID_REASONS: Final = ("changed", "superseded", "cancelled", "enqueue_failed")

# The filing job's wire contract (`filing_job_message`).
FILING_JOB_KIND: Final = "filing.submit"
FILING_JOB_VERSION: Final = 1
FILING_JOB_KEYS: Final = frozenset(
    {"kind", "version", "approval_id", "filing_id", "case_id"}
)

_CASE_DATA_SCALARS: Final = frozenset({"case", "debtors", "tax_ids"})


# ── Errors ──────────────────────────────────────────────────────


class ApprovalNotPermittedError(ForbiddenError):
    """The caller may not approve this filing: not an attorney, or no
    enrolled, openable court login of their own for the case's court. A
    403, a fact about the caller's own account."""


class FilingSetNotReadyError(ConflictError):
    """The filing set has a blocking check outstanding (`blockers`)."""

    def __init__(self, blockers: Sequence[str]) -> None:
        super().__init__(
            "The filing set is not ready to approve: " + ", ".join(blockers) + "."
        )
        self.blockers = tuple(blockers)


class FilingQueueUnavailableError(Exception):
    """The approval could not hand its job to the filing queue. The approval
    is voided (`enqueue_failed`) before this is raised, so nothing is left
    pending that no worker would ever see; the route answers 503."""


class ApprovalUnavailableError(Exception):
    """There is no approval the filing worker may act on: unknown, already
    consumed, voided, expired, or voided just now because the filing set
    changed (`reason`). The worker drops the job — never a retry."""

    def __init__(self, reason: str) -> None:
        super().__init__(reason)
        self.reason = reason


# ── The basis and its digest ────────────────────────────────────


@dataclass(frozen=True)
class FeeHandling:
    handling: str
    deadline: str | None
    note: str
    verified: bool


@dataclass(frozen=True)
class ApprovalBasis:
    """What an approval would be bound to, computed from the stores now."""

    digest: str
    filing_set: FilingSet
    fee: FeeHandling
    # Checklist ids (and the two approval-only checks, `filed` and
    # `file_digests`) that must clear before anything can be approved.
    blockers: tuple[str, ...]

    @property
    def ready(self) -> bool:
        return not self.blockers


def _canonical_default(value: object) -> object:
    if isinstance(value, datetime | date):
        return value.isoformat()
    if isinstance(value, Decimal):
        return str(value)
    if isinstance(value, frozenset | set):
        return sorted(value, key=str)
    raise TypeError(f"no canonical form for {type(value).__name__}")


def canonical_bytes(document: Mapping[str, object]) -> bytes:
    """THE canonical encoding — the module docstring's definition."""
    return json.dumps(
        document,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
        default=_canonical_default,
    ).encode("utf-8")


def digest_of(document: Mapping[str, object]) -> str:
    return hashlib.sha256(canonical_bytes(document)).hexdigest()


def _part_document(part: PacketPart | None) -> dict[str, object] | None:
    if part is None:
        return None
    return {
        "name": part.name,
        "sha256": part.sha256,
        "byteSize": part.byte_size,
        "pageCount": part.page_count,
    }


def _documents(documents: Sequence[FilingDocument]) -> list[dict[str, object]]:
    return [
        {
            "position": position,
            "key": document.key,
            "fileName": document.file_name,
            "handling": document.handling,
            "source": document.source,
            "file": _part_document(document.part),
        }
        for position, document in enumerate(documents, start=1)
    ]


def _entities(entities: Sequence[CaseEntity[Any]]) -> list[dict[str, object]]:
    return [
        {
            "id": entity.id,
            "updatedAt": entity.updated_at,
            "amended": entity.amended,
            "body": entity_body(entity),
        }
        for entity in entities
    ]


def _record(data: CaseData) -> dict[str, object]:
    case = data.case
    record: dict[str, object] = {
        "case": {
            "id": case.id,
            "firmId": case.firm_id,
            "chapter": case.chapter,
            "court": case.court,
            "division": case.division,
            "district": case.district,
            "exemptionSet": case.exemption_set,
        },
        "debtors": [
            {
                "filingRole": debtor.filing_role,
                "updatedAt": debtor.updated_at,
                "body": prune_body(debtor_body(debtor)),
                "taxId": (
                    None
                    if debtor.tax_id is None
                    else {
                        "kind": debtor.tax_id.kind,
                        "lastFour": debtor.tax_id.last_four,
                        "ref": debtor.tax_id.ref,
                    }
                ),
            }
            for debtor in data.debtors
        ],
    }
    collections: dict[str, object] = {}
    for member in fields(CaseData):
        if member.name in _CASE_DATA_SCALARS:
            continue
        collections[member.name] = _entities(getattr(data, member.name))
    record["collections"] = collections
    return record


def _fee(district: courts.CourtDistrict | None) -> FeeHandling:
    rule = district.opening.fee_rule if district is not None else None
    value = rule.value if rule is not None else None
    return FeeHandling(
        handling=FEE_HANDLING,
        deadline=value.deadline if value is not None else None,
        note=value.note if value is not None else "",
        verified=rule.verified if rule is not None else False,
    )


def _packet_document(packet: Packet | None) -> dict[str, object] | None:
    if packet is None:
        return None
    return {"id": packet.id, "sha256": packet.sha256, "byteSize": packet.byte_size}


def basis_document(
    data: CaseData, filing_set: FilingSet, fee: FeeHandling
) -> dict[str, object]:
    """The document the digest is taken over (module docstring). Holds PII:
    hash it, never store or return it."""
    return {
        "scheme": SCHEME,
        "court": {
            "code": data.case.court,
            "division": data.case.division,
            "registryRelease": filing_set.registry_release,
        },
        "packet": _packet_document(filing_set.packet),
        "documents": _documents(filing_set.documents),
        "record": _record(data),
        "fee": {
            "handling": fee.handling,
            "deadline": fee.deadline,
            "note": fee.note,
            "verified": fee.verified,
        },
    }


def _blockers(data: CaseData, filing_set: FilingSet) -> tuple[str, ...]:
    """Every checklist item still `missing` — no court, a case-data gap, no
    filing-set packet, a file failing or not yet measured — plus two checks
    only an approval makes: the case is not already filed, and every packet
    file carries the digest the approval binds to (a packet assembled before
    PR 6 did not record one)."""
    blockers = [item.id for item in filing_set.checklist if item.status == "missing"]
    if is_filed(data.case.status):
        blockers.insert(0, "filed")
    if filing_set.packet is not None and any(
        document.source == "packet"
        and (document.part is None or document.part.sha256 is None)
        for document in filing_set.documents
    ):
        blockers.append("file_digests")
    return tuple(dict.fromkeys(blockers))


def approval_basis(
    data: CaseData,
    *,
    packets: Sequence[Packet],
    release: courts.CourtsRelease,
    as_of: date,
) -> ApprovalBasis:
    """The filing set as it stands, its fee handling, its blockers and the
    digest an approval would be bound to. Pure over its inputs; the approve
    route, the status read and the worker's consume all call this, so the
    three cannot disagree about what "the same filing" means."""
    filing_set = build_filing_set(data, packets=packets, release=release, as_of=as_of)
    district = release.district(data.case.court) if data.case.court else None
    fee = _fee(district)
    return ApprovalBasis(
        digest=digest_of(basis_document(data, filing_set, fee)),
        filing_set=filing_set,
        fee=fee,
        blockers=_blockers(data, filing_set),
    )


def basis_for_case(
    case: Case,
    *,
    debtor_store: DebtorStore,
    entity_store: CaseEntityStore,
    packet_store: PacketStore,
    as_of: date,
) -> tuple[CaseData, ApprovalBasis]:
    """`approval_basis` over the stores as they stand — the ONE composition
    of it. The approval routes call this, and so does the filing worker
    (services/filing) when it consumes the approval and again immediately
    before the final submit: the same reads, the same registry release for
    `as_of`, the same digest, or the worker would void approvals the API
    just made."""
    data = read_case_data(case, debtor_store=debtor_store, entity_store=entity_store)
    return data, approval_basis(
        data,
        packets=packet_store.list_for_case(case.id),
        release=courts.resolve(as_of),
        as_of=as_of,
    )


# ── The record ──────────────────────────────────────────────────


@dataclass(frozen=True)
class FilingApproval:
    """One approval of one filing set.

    `attorney_id` is the approver AND the owner of `credential_id` — the
    approval looks the credential up in the approver's own partition, so
    the two cannot differ. `authenticated_at` is the `auth_time` it was
    made under. Times: `approved_at` and the stamps are ISO strings (the
    record's style); `expires_at` is epoch seconds, because the consume's
    conditional write compares it with a number.
    """

    approval_id: str
    filing_id: str
    case_id: str
    firm_id: str
    attorney_id: str
    credential_id: str
    authorization_id: str
    digest: str
    packet_id: str
    court: str
    division: str
    registry_release: str
    approved_at: str
    authenticated_at: int
    expires_at: int
    status: ApprovalStatus = "pending"
    consumed_at: str | None = None
    voided_at: str | None = None
    void_reason: str | None = None


def effective_status(approval: FilingApproval, *, now: float) -> EffectiveStatus:
    if approval.status == "pending" and now >= approval.expires_at:
        return "expired"
    return approval.status


CURRENT_SORT_KEY: Final = "FILING_APPROVAL"
_SORT_PREFIX: Final = "FILING_APPROVAL#"


def sort_key(approval_id: str) -> str:
    return f"{_SORT_PREFIX}{approval_id}"


def approval_item(approval: FilingApproval) -> dict[str, object]:
    """The stored shape, shared by both stores.

    PK  CASE#<case_id>               the case's own partition — a child item
    SK  FILING_APPROVAL#<id>         like PACKET#/JOB#, under the API's
                                     existing case-table grant

    and beside them ONE pointer item, SK = FILING_APPROVAL, naming the
    case's current approval (`currentApprovalId`) — the row every new
    approval's transaction conditions on."""
    item: dict[str, object] = {
        "PK": partition_key(approval.case_id),
        "SK": sort_key(approval.approval_id),
        "approvalId": approval.approval_id,
        "filingId": approval.filing_id,
        "caseId": approval.case_id,
        "firmId": approval.firm_id,
        "attorneyId": approval.attorney_id,
        "credentialId": approval.credential_id,
        "authorizationId": approval.authorization_id,
        "digest": approval.digest,
        "packetId": approval.packet_id,
        "court": approval.court,
        "division": approval.division,
        "registryRelease": approval.registry_release,
        "approvedAt": approval.approved_at,
        "authTime": approval.authenticated_at,
        "expiresAt": approval.expires_at,
        "status": approval.status,
    }
    for key, value in (
        ("consumedAt", approval.consumed_at),
        ("voidedAt", approval.voided_at),
        ("voidReason", approval.void_reason),
    ):
        if value is not None:
            item[key] = value
    return item


def approval_from_item(item: Mapping[str, object]) -> FilingApproval:
    def text(key: str) -> str:
        value = item.get(key)
        if not isinstance(value, str) or not value:
            raise ValidationError(f"stored filing approval is missing {key}")
        return value

    def number(key: str) -> int:
        value = item.get(key)
        if isinstance(value, bool) or not isinstance(value, int | float | str):
            raise ValidationError(f"stored filing approval is missing {key}")
        return int(value)

    def optional(key: str) -> str | None:
        value = item.get(key)
        return value if isinstance(value, str) else None

    status = text("status")
    if status not in ("pending", "consumed", "voided"):
        raise ValidationError(f"stored filing approval has status {status!r}")
    return FilingApproval(
        approval_id=text("approvalId"),
        filing_id=text("filingId"),
        case_id=text("caseId"),
        firm_id=text("firmId"),
        attorney_id=text("attorneyId"),
        credential_id=text("credentialId"),
        authorization_id=text("authorizationId"),
        digest=text("digest"),
        packet_id=text("packetId"),
        court=text("court"),
        division=text("division"),
        registry_release=text("registryRelease"),
        approved_at=text("approvedAt"),
        authenticated_at=number("authTime"),
        expires_at=number("expiresAt"),
        status=status,  # type: ignore[arg-type]
        consumed_at=optional("consumedAt"),
        voided_at=optional("voidedAt"),
        void_reason=optional("voidReason"),
    )


def _iso(moment: float) -> str:
    return (
        datetime.fromtimestamp(moment, UTC)
        .isoformat(timespec="milliseconds")
        .replace("+00:00", "Z")
    )


# ── The filing job ──────────────────────────────────────────────


def filing_job_message(approval: FilingApproval) -> dict[str, object]:
    """THE filing job — what the queue carries, and all it carries: which
    approval, which filing, which case. The worker reads everything else
    (the credential reference, the digest, the documents) from the stores
    under its own role; a message is not a place a secret or a document can
    ride. `FILING_JOB_KEYS` pins the key set."""
    return {
        "kind": FILING_JOB_KIND,
        "version": FILING_JOB_VERSION,
        "approval_id": approval.approval_id,
        "filing_id": approval.filing_id,
        "case_id": approval.case_id,
    }


def parse_filing_job_message(body: str) -> dict[str, str]:
    """The consumer's half of the contract (PR 7's worker, and the dev
    proof today). Refuses anything but exactly the five keys."""
    try:
        raw = json.loads(body)
    except ValueError as error:
        raise ValidationError("a filing job is not JSON") from error
    if not isinstance(raw, dict) or set(raw) != FILING_JOB_KEYS:
        raise ValidationError("a filing job carries exactly its five keys")
    if raw["kind"] != FILING_JOB_KIND or raw["version"] != FILING_JOB_VERSION:
        raise ValidationError("not a filing job this consumer understands")
    return {key: str(raw[key]) for key in ("approval_id", "filing_id", "case_id")}


# ── Approve ─────────────────────────────────────────────────────


def _parse_approve(payload: object) -> tuple[str, str | None]:
    if not isinstance(payload, Mapping):
        raise ValidationError("request body must be a JSON object")
    digest = payload.get("digest")
    if not isinstance(digest, str) or len(digest) != 64:
        raise ValidationError(
            "digest is required: the digest of the filing set you were shown"
        )
    credential_id = payload.get("credential_id")
    if credential_id is not None and not isinstance(credential_id, str):
        raise ValidationError("credential_id must be a string")
    return digest, credential_id


def _credential_for(
    court: str,
    *,
    firm_id: str,
    attorney_id: str,
    credential_id: str | None,
    credentials: FilingCredentialStore,
    authorizations: FilingAuthorizationStore,
) -> tuple[FilingCredential, str]:
    """The caller's OWN court login that would file this case, and the
    authorization it stands on. Read in the caller's partition, so it is
    the caller's or nothing: "only the credential's owner may approve"."""
    authorization = authorizations.get_current(firm_id, attorney_id)
    if authorization is None or not is_current(
        authorization, firm_id=firm_id, attorney_id=attorney_id
    ):
        raise AuthorizationRequiredError(
            "Sign the filing authorization before approving a filing."
        )
    usable = [
        credential
        for credential in credentials.list_for_attorney(firm_id, attorney_id)
        if court in credential.courts and is_openable(credential, authorization)
    ]
    if credential_id is not None:
        usable = [c for c in usable if c.credential_id == credential_id]
    if not usable:
        raise ApprovalNotPermittedError(
            "You have no court login stored for this court. Only the attorney"
            " whose login would file the case may approve it."
        )
    if len(usable) > 1:
        raise ConflictError(
            "You have more than one login for this court — say which one files it."
        )
    return usable[0], authorization.authorization_id


def approve_filing(
    payload: object,
    *,
    data: CaseData,
    basis: ApprovalBasis,
    firm_id: str,
    attorney_id: str,
    role: str,
    authenticated_at: int | None,
    now: float,
    credentials: FilingCredentialStore,
    authorizations: FilingAuthorizationStore,
    approvals: FilingApprovalStore,
    queue: FilingQueue,
    access_log: AccessLog,
) -> FilingApproval:
    """Approve the filing set the caller was shown, and enqueue its one job.

    `basis` is `approval_basis` over the stores NOW; `payload` carries the
    digest the client rendered. Refuses, in order — each refusal a denied
    `filing.approve` row naming it:

    1. a sign-in older than APPROVAL_SIGN_IN_MAX_AGE_SECONDS (403
       ReauthenticationRequired) — checked here, not only in the route;
    2. a caller who is not an attorney (403) — guardrail 1's "never a
       paralegal, a firm administrator or Insolvia staff";
    3. a filing set with a blocker outstanding (409);
    4. a digest other than the one the stores give now (409): the screen
       was showing something else, so this is not what was approved;
    5. no current signed authorization (403), or no openable court login of
       the caller's own for the case's court (403);
    6. a case whose current approval is already consumed — a filing in
       flight (409).
    """
    case_id = data.case.id

    def refuse(reason: str) -> None:
        access_log.record(
            record_access(
                case_id=case_id,
                principal=attorney_id,
                action="filing.approve",
                outcome="denied",
                purpose=reason,
            )
        )

    try:
        fresh = require_recent_authentication(
            authenticated_at,
            now=now,
            max_age_seconds=APPROVAL_SIGN_IN_MAX_AGE_SECONDS,
        )
    except ForbiddenError:
        refuse("reauthentication_required")
        raise
    if role != "attorney":
        refuse("not_an_attorney")
        raise ApprovalNotPermittedError(
            "Only the attorney whose court login files the case may approve it."
        )
    shown_digest, credential_id = _parse_approve(payload)
    if basis.blockers:
        refuse("not_ready")
        raise FilingSetNotReadyError(basis.blockers)
    if shown_digest != basis.digest:
        refuse("changed_since_shown")
        raise ConflictError(
            "The filing set has changed since it was shown — review it again"
            " and approve what is there now."
        )
    try:
        credential, authorization_id = _credential_for(
            data.case.court or "",
            firm_id=firm_id,
            attorney_id=attorney_id,
            credential_id=credential_id,
            credentials=credentials,
            authorizations=authorizations,
        )
    except (ForbiddenError, ConflictError) as refusal:
        refuse(
            "authorization_required"
            if isinstance(refusal, AuthorizationRequiredError)
            else "no_credential"
            if isinstance(refusal, ForbiddenError)
            else "credential_ambiguous"
        )
        raise
    existing = approvals.current(case_id)
    if existing is not None and existing.status == "consumed":
        refuse("filing_in_flight")
        raise ConflictError(
            "This case's last approval is already being filed — a second"
            " approval would be a second filing."
        )

    packet = basis.filing_set.packet
    assert packet is not None  # a missing packet is a blocker above
    approval = FilingApproval(
        approval_id=str(uuid.uuid4()),
        filing_id=str(uuid.uuid4()),
        case_id=case_id,
        firm_id=firm_id,
        attorney_id=attorney_id,
        credential_id=credential.credential_id,
        authorization_id=authorization_id,
        digest=basis.digest,
        packet_id=packet.id,
        court=data.case.court or "",
        division=data.case.division or "",
        registry_release=basis.filing_set.registry_release,
        approved_at=_iso(now),
        authenticated_at=fresh,
        expires_at=int(now) + APPROVAL_TTL_SECONDS,
    )
    replaced = (
        existing if existing is not None and existing.status == "pending" else None
    )
    approvals.create(approval, replacing=existing, voided_at=_iso(now))
    access_log.record(
        record_access(
            case_id=case_id,
            principal=attorney_id,
            action="filing.approve",
            filing_id=approval.filing_id,
        )
    )
    if replaced is not None:
        access_log.record(
            record_access(
                case_id=case_id,
                principal=attorney_id,
                action="filing.void",
                purpose="superseded",
                filing_id=replaced.filing_id,
            )
        )
    try:
        queue.enqueue(approval)
    except Exception as error:
        void_approval(
            approval,
            reason="enqueue_failed",
            principal=attorney_id,
            now=now,
            approvals=approvals,
            access_log=access_log,
        )
        raise FilingQueueUnavailableError(
            "The filing could not be queued; the approval was voided."
        ) from error
    return approval


# ── Void, status, consume ───────────────────────────────────────


def void_approval(
    approval: FilingApproval,
    *,
    reason: VoidReason,
    principal: str,
    now: float,
    approvals: FilingApprovalStore,
    access_log: AccessLog,
) -> FilingApproval | None:
    """Void a PENDING approval. The voided record, or None when it was not
    pending any more (consumed or voided by somebody else first — the
    conditional write decides). Logged only when it happened."""
    voided_at = _iso(now)
    if not approvals.void(
        approval.case_id, approval.approval_id, reason=reason, voided_at=voided_at
    ):
        return None
    access_log.record(
        record_access(
            case_id=approval.case_id,
            principal=principal,
            action="filing.void",
            purpose=reason,
            filing_id=approval.filing_id,
        )
    )
    return replace(approval, status="voided", voided_at=voided_at, void_reason=reason)


def current_approval(
    case_id: str,
    *,
    basis: ApprovalBasis,
    principal: str,
    now: float,
    approvals: FilingApprovalStore,
    access_log: AccessLog,
) -> FilingApproval | None:
    """The case's current approval, as of now — and VOIDED here, recorded,
    if it is still pending but the filing set no longer has its digest. The
    approval screen reads this, so "anything changed" shows as voided the
    first time anybody looks, not only when the worker tries."""
    approval = approvals.current(case_id)
    if (
        approval is not None
        and effective_status(approval, now=now) == "pending"
        and approval.digest != basis.digest
    ):
        return void_approval(
            approval,
            reason="changed",
            principal=principal,
            now=now,
            approvals=approvals,
            access_log=access_log,
        ) or approvals.current(case_id)
    return approval


def cancel_approval(
    case_id: str,
    *,
    principal: str,
    now: float,
    approvals: FilingApprovalStore,
    access_log: AccessLog,
) -> FilingApproval:
    """Withdraw the case's pending approval before the worker consumes it.
    Like withdrawing the authorization, it only takes authority away, so it
    needs no fresh sign-in. ConflictError when nothing is pending."""
    approval = approvals.current(case_id)
    if approval is None or effective_status(approval, now=now) != "pending":
        raise ConflictError("There is no pending approval to cancel.")
    voided = void_approval(
        approval,
        reason="cancelled",
        principal=principal,
        now=now,
        approvals=approvals,
        access_log=access_log,
    )
    if voided is None:
        raise ConflictError("The approval was consumed or voided first.")
    return voided


def consume_approval(
    case_id: str,
    approval_id: str,
    *,
    basis: ApprovalBasis,
    now: float,
    approvals: FilingApprovalStore,
    access_log: AccessLog,
) -> FilingApproval:
    """THE WORKER'S ACT (ADR 0024 PR 7), and the one way an approval is used:
    once. `basis` is `approval_basis` recomputed from the stores now.

    Refuses (ApprovalUnavailableError, the job is dropped) an approval that
    is unknown, not pending, or expired; VOIDS one whose digest the filing
    set no longer has (`changed`) and refuses it; otherwise flips it to
    `consumed` with one conditional write — pending, same digest, unexpired
    — so of two consumers exactly one succeeds. Each outcome is a
    `filing.consume` (or `filing.void`) row against the approving attorney."""
    approval = approvals.get(case_id, approval_id)
    if approval is None:
        raise ApprovalUnavailableError("unknown")

    def refuse(reason: str) -> ApprovalUnavailableError:
        access_log.record(
            record_access(
                case_id=case_id,
                principal=approval.attorney_id,
                action="filing.consume",
                outcome="denied",
                purpose=reason,
                filing_id=approval.filing_id,
            )
        )
        return ApprovalUnavailableError(reason)

    status = effective_status(approval, now=now)
    if status != "pending":
        raise refuse(status)
    if approval.digest != basis.digest:
        void_approval(
            approval,
            reason="changed",
            principal=approval.attorney_id,
            now=now,
            approvals=approvals,
            access_log=access_log,
        )
        raise refuse("changed")
    consumed_at = _iso(now)
    if not approvals.consume(
        case_id,
        approval_id,
        digest=basis.digest,
        now=int(now),
        consumed_at=consumed_at,
    ):
        raise refuse("already_used")
    access_log.record(
        record_access(
            case_id=case_id,
            principal=approval.attorney_id,
            action="filing.consume",
            filing_id=approval.filing_id,
        )
    )
    return replace(approval, status="consumed", consumed_at=consumed_at)


# ── The wire shapes ─────────────────────────────────────────────


def approval_json(approval: FilingApproval, *, now: float) -> dict[str, object]:
    """What a client sees of an approval. Optional keys absent, never null."""
    body: dict[str, object] = {
        "id": approval.approval_id,
        "filingId": approval.filing_id,
        "status": effective_status(approval, now=now),
        "digest": approval.digest,
        "approvedBy": approval.attorney_id,
        "approvedAt": approval.approved_at,
        "expiresAt": _iso(approval.expires_at),
        "credentialId": approval.credential_id,
        "court": approval.court,
        "division": approval.division,
        "packetId": approval.packet_id,
    }
    for key, value in (
        ("consumedAt", approval.consumed_at),
        ("voidedAt", approval.voided_at),
        ("voidReason", approval.void_reason),
    ):
        if value is not None:
            body[key] = value
    return body


def _file_json(part: PacketPart | None) -> dict[str, object] | None:
    if part is None:
        return None
    body: dict[str, object] = {"byteSize": part.byte_size}
    if part.page_count is not None:
        body["pageCount"] = part.page_count
    if part.sha256 is not None:
        body["sha256"] = part.sha256
    return body


def basis_json(basis: ApprovalBasis) -> dict[str, object]:
    """WHAT THE ATTORNEY IS SHOWN: the court, the documents in docket order
    with their sizes and digests, the checklist, the fee handling, and the
    digest over all of it (plus the record behind it — which this view does
    not repeat: it is the case the attorney has been working in). Never the
    debtor's data, never a tax identifier."""
    filing_set = basis.filing_set
    documents: list[dict[str, object]] = []
    for position, document in enumerate(filing_set.documents, start=1):
        entry: dict[str, object] = {
            "position": position,
            "key": document.key,
            "title": document.title,
            "fileName": document.file_name,
            "source": document.source,
            "handling": document.handling,
        }
        file = _file_json(document.part)
        if file is not None:
            entry["file"] = file
        if document.note:
            entry["note"] = document.note
        documents.append(entry)
    checklist = [
        {
            "id": item.id,
            "status": item.status,
            "title": item.title,
            "detail": item.detail,
            **({"link": item.link} if item.link is not None else {}),
        }
        for item in filing_set.checklist
    ]
    fee: dict[str, object] = {
        "handling": basis.fee.handling,
        "verified": basis.fee.verified,
        "detail": "Insolvia stores and enters no card details: the filing"
        " stops at the court's payment step and is handed back to you to pay"
        " in your own CM/ECF session, within the court's window.",
    }
    if basis.fee.deadline is not None:
        fee["deadline"] = basis.fee.deadline
    body: dict[str, object] = {
        "scheme": SCHEME,
        "digest": basis.digest,
        "ready": basis.ready,
        "blockers": list(basis.blockers),
        "registryRelease": filing_set.registry_release,
        "documents": documents,
        "checklist": checklist,
        "fee": fee,
        "signInMaxAgeSeconds": APPROVAL_SIGN_IN_MAX_AGE_SECONDS,
        "approvalTtlSeconds": APPROVAL_TTL_SECONDS,
    }
    if filing_set.court_code is not None and filing_set.court_name is not None:
        court: dict[str, object] = {
            "code": filing_set.court_code,
            "name": filing_set.court_name,
        }
        if filing_set.division_name is not None:
            court["divisionName"] = filing_set.division_name
        body["court"] = court
    if filing_set.packet is not None:
        body["packet"] = {
            "id": filing_set.packet.id,
            "createdAt": filing_set.packet.created_at,
            "sha256": filing_set.packet.sha256,
            "byteSize": filing_set.packet.byte_size,
        }
    return body
