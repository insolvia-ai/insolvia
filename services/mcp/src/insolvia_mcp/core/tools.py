"""The eight tools' domain logic (docs/reference/mcp-surface.md).

Everything protocol-shaped — JSON-RPC, structuredContent, the error-code
mapping — lives in the api layer; this module is the surface's MEANING: which
store answers each tool, which permission gates it, and what every refusal is.
It raises `insolvia_core.errors` exactly as the API's core does, so the
docstring on each error class governs both surfaces.

Three inherited rules shape every method (mcp-surface.md § inherited):

- The firm is never an argument. Every method takes a resolved `Accessor`,
  and the per-call resolution happens above (api/auth.py) — never cached,
  failing closed.
- Permission checks run HERE, below auth, failing closed — the tool-layer
  equivalent of the API's `@requires`. A tool the caller may not use is
  listed but refuses (`permission_denied`); another firm's caseId answers
  `not_found`, indistinguishable from a case that does not exist.
- There is no method that writes a case record. The write half of the surface
  is candidates (insolvia_core.candidates), and confirm-before-entry holds
  structurally because the code path does not exist.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Final, NotRequired, TypedDict

from insolvia_core.access import Accessor
from insolvia_core.access_log import record_access
from insolvia_core.candidates import (
    STATUSES as CANDIDATE_STATUSES,
)
from insolvia_core.candidates import (
    CandidateOrigin,
    candidate_review_json,
    create_candidate,
    parse_proposals,
    withdraw,
)
from insolvia_core.case_collections import COLLECTIONS
from insolvia_core.case_entities import entity_json
from insolvia_core.cases import (
    STATUSES as CASE_STATUSES,
)
from insolvia_core.cases import (
    Case,
    case_json,
    decode_cursor,
    encode_cursor,
)
from insolvia_core.debtors import debtor_json
from insolvia_core.documents import document_json
from insolvia_core.errors import (
    ConflictError,
    ForbiddenError,
    NotFoundError,
    ValidationError,
)
from insolvia_core.firm_clients import FirmClient, firm_client_json
from insolvia_core.firms import (
    ADD_EDIT,
    CASES,
    CLIENTS,
    DOCUMENTS,
    FEATURES,
    INTAKE,
    VIEW_ONLY,
    full_name,
    permission_for,
)
from insolvia_core.ports import (
    AccessLog,
    CandidateStore,
    CaseEntityStore,
    CaseStore,
    DebtorStore,
    DocumentStore,
    FirmStore,
)

# mcp-surface.md § Pagination: identical numbers to the API's contract, but
# the surface's stated default is 25 — an agent's context window is the
# budget, where the app's screen was the API's.
MAX_LIMIT: Final = 100
DEFAULT_LIMIT: Final = 25

# The entity types the generic record tools serve: every registered generic
# collection, plus the non-generic case children. The enum is derived from
# the registry, never written out — "the server publishes only the types its
# store actually implements", and a new collection registered in
# insolvia_core is a new enum value here without a code change.
#
# `clients` (ADR 0022) is the one type that is not in the case's partition:
# a case's clients are the FIRM clients its debtors are linked to
# (`Debtor.client_id`), read from the firm table. It is still reached only
# through a case the caller may see — a firm-wide client list is not a tool
# in v1 — and it is read-only: a client proposal needs a review path that
# writes the firm table, which the candidate review does not have yet.
DEBTORS: Final = "debtors"
DOCUMENTS_TYPE: Final = "documents"
CLIENTS_TYPE: Final = "clients"
ENTITY_TYPES: Final = (*COLLECTIONS, DEBTORS, DOCUMENTS_TYPE, CLIENTS_TYPE)


def feature_for_entity_type(entity_type: str) -> str:
    """The per-entity gate map (mcp-surface.md § Permission gates), mirroring
    the API's routes: `documents` is the documents feature, `clients` the
    clients feature (`/v1/firm/clients`' gate — the first type not under
    intake), and everything else in a case's partition is intake."""
    if entity_type == DOCUMENTS_TYPE:
        return DOCUMENTS
    if entity_type == CLIENTS_TYPE:
        return CLIENTS
    return INTAKE


class FirmSummary(TypedDict):
    id: str
    name: str


class WhoamiResult(TypedDict):
    firm: FirmSummary | None
    displayName: str
    isAdmin: bool
    accessAllCases: bool
    permissions: dict[str, str]


class ListCasesResult(TypedDict):
    cases: list[dict[str, Any]]
    nextCursor: NotRequired[str]


class GetCaseResult(TypedDict):
    case: dict[str, Any]
    recordCounts: dict[str, int]


class ListCaseRecordsResult(TypedDict):
    records: list[dict[str, Any]]
    nextCursor: NotRequired[str]


class GetCaseRecordResult(TypedDict):
    record: dict[str, Any]


class ProposedCandidate(TypedDict):
    candidateId: str
    entityType: str
    status: str


class ProposeCaseRecordsResult(TypedDict):
    candidates: list[ProposedCandidate]


class CheckProposalsResult(TypedDict):
    candidates: list[dict[str, Any]]
    nextCursor: NotRequired[str]


class WithdrawProposalResult(TypedDict):
    candidateId: str
    status: str


def whoami(accessor: Accessor | None) -> WhoamiResult:
    """The `/v1/me` of this surface: authenticated-only, and the ONE tool that
    resolves without requiring a firm — it reports the firm, or reports its
    absence, so an agent whose user was never provisioned gets a fact instead
    of a refusal it cannot interpret."""
    if accessor is None:
        return WhoamiResult(
            firm=None,
            displayName="",
            isAdmin=False,
            accessAllCases=False,
            permissions={},
        )
    return WhoamiResult(
        firm=FirmSummary(id=accessor.firm.id, name=accessor.firm.name),
        displayName=full_name(accessor.user),
        isAdmin=accessor.user.is_admin,
        accessAllCases=accessor.user.access_all_cases,
        permissions={
            feature: permission_for(accessor.user, feature) for feature in FEATURES
        },
    )


def parse_limit(value: object) -> int:
    if value is None:
        return DEFAULT_LIMIT
    if isinstance(value, bool) or not isinstance(value, int):
        raise ValidationError("limit must be a number")
    if value < 1 or value > MAX_LIMIT:
        raise ValidationError(f"limit must be between 1 and {MAX_LIMIT}")
    return value


def _offset_binding(case_id: str, listing: str) -> str:
    """What an offset cursor is bound to. The binding string does the same
    job the by-firm/by-assignee tag does for case cursors: a cursor replayed
    against a different case or a different entity type answers
    `validation_failed` rather than silently paging the wrong list."""
    return f"mcp-offset:{case_id}:{listing}"


def _paginate(
    records: list[dict[str, Any]],
    *,
    limit: int,
    cursor: str | None,
    binding: str,
) -> tuple[list[dict[str, Any]], str | None]:
    """Offset pagination over an in-memory listing.

    The underlying ports return a case's WHOLE collection (their documented
    contract — the API renders whole schedules), so pagination here is a
    presentation of that answer, not a second query. Cursors reuse the case
    cursor codec: opaque, validated, bound to the listing that minted them.
    """
    offset = 0
    if cursor is not None:
        key = decode_cursor(cursor, index=binding)
        raw = key.get("offset", "")
        if not raw.isdigit():
            raise ValidationError("cursor is not valid")
        offset = int(raw)
    page = records[offset : offset + limit]
    next_cursor: str | None = None
    if offset + limit < len(records):
        next_cursor = encode_cursor({"offset": str(offset + limit)}, index=binding)
    return page, next_cursor


@dataclass(frozen=True)
class CaseTools:
    """The seven case tools, over the ports an entrypoint composed.

    A frozen dataclass rather than free functions so the api layer registers
    bound methods and the tests compose the whole surface in one line —
    the same role ApiDependencies plays for the Flask app.
    """

    case_store: CaseStore
    case_entity_store: CaseEntityStore
    debtor_store: DebtorStore
    document_store: DocumentStore
    candidate_store: CandidateStore
    access_log: AccessLog
    # The firm table, for the one entity type that lives there: a case's
    # clients (ADR 0022). Read by key only — `get_client` — so the MCP
    # role's existing GetItem grant on the firm table is all it needs.
    firm_store: FirmStore

    # ── shared plumbing ──────────────────────────────────────────────

    def _require(self, accessor: Accessor, feature: str, level: str) -> None:
        """The tool-layer `@requires`: refuse with the caller's-own-account
        error (`permission_denied`), never a 404 — mcp-surface.md's
        list-visible, call-denied rule."""
        if not accessor.may(feature, level):
            raise ForbiddenError("your firm has not granted you access to this")

    def _reachable_case(self, accessor: Accessor, case_id: str, action: str) -> Case:
        """Resolve the case first, and record the attempt either way — the
        same seam services/api's `_reachable_case_or_404` is, because this is
        the only authorisation check there is: every child store takes no
        accessor and enforces nothing."""
        if not isinstance(case_id, str) or not case_id:
            raise ValidationError("caseId is required")
        case = self.case_store.get(case_id, accessor=accessor)
        self.access_log.record(
            record_access(
                case_id=case_id,
                principal=accessor.subject,
                action=action,
                outcome="allowed" if case is not None else "denied",
            )
        )
        if case is None:
            # Identical to a case that does not exist — the anti-oracle 404,
            # carried over to the tool vocabulary as `not_found`.
            raise NotFoundError("case not found")
        return case

    def _case_clients(self, case: Case) -> list[FirmClient]:
        """The firm clients `case`'s debtors are linked to, in filing-role
        order, each once — a joint case is two clients, and one client never
        holds two roles on a case (ADR 0022's joint-case rule), but a
        duplicate is dropped here rather than trusted away.

        Resolved in the CASE's firm, which is the caller's (the case was
        reachable). A linked id that no longer resolves is skipped, not an
        error: the debtor's copy is still the case's data, and the tool
        answers what exists. Archived clients are included — archiving is
        the firm being done with a person, not the case forgetting who its
        debtor is.
        """
        clients: list[FirmClient] = []
        seen: set[str] = set()
        for debtor in self.debtor_store.list_for_case(case.id):
            if debtor.client_id is None or debtor.client_id in seen:
                continue
            seen.add(debtor.client_id)
            client = self.firm_store.get_client(case.firm_id, debtor.client_id)
            if client is not None:
                clients.append(client)
        return clients

    def _entity_kind_records(
        self, case: Case, entity_type: str
    ) -> list[dict[str, Any]]:
        """One entity type's records, serialized in the API's wire shape so
        the two surfaces cannot drift. A client serializes as
        `firm_client_json` — the tax id as `tax_id_last_four` only, never
        the ref and never the value."""
        case_id = case.id
        if entity_type == CLIENTS_TYPE:
            return [firm_client_json(client) for client in self._case_clients(case)]
        if entity_type == DEBTORS:
            return [
                debtor_json(debtor)
                for debtor in self.debtor_store.list_for_case(case_id)
            ]
        if entity_type == DOCUMENTS_TYPE:
            return [
                document_json(document)
                for document in self.document_store.list_for_case(case_id)
            ]
        kind = COLLECTIONS[entity_type]
        return [
            entity_json(entity)
            for entity in self.case_entity_store.list_for_case(case_id, kind)
        ]

    def _validated_entity_type(self, value: object) -> str:
        if value not in ENTITY_TYPES:
            raise ValidationError(
                "entityType must be one of: " + ", ".join(ENTITY_TYPES)
            )
        return str(value)

    # ── the read tools ───────────────────────────────────────────────

    def list_cases(
        self,
        accessor: Accessor,
        *,
        status: object = None,
        limit: object = None,
        cursor: object = None,
    ) -> ListCasesResult:
        self._require(accessor, CASES, VIEW_ONLY)
        if status is not None and status not in CASE_STATUSES:
            raise ValidationError("status must be one of: " + ", ".join(CASE_STATUSES))
        if cursor is not None and not isinstance(cursor, str):
            raise ValidationError("cursor is not valid")
        page = self.case_store.list_for_accessor(
            accessor, limit=parse_limit(limit), cursor=cursor
        )
        cases = [
            case_json(case)
            for case in page.cases
            # The status filter is applied WITHIN the page rather than pushed
            # into the store: the listing indexes sort by creation time, not
            # status, and a filtered page that silently skipped ahead would
            # need the store to grow a third index. A short page with a
            # nextCursor is the honest answer.
            if status is None or case.status == status
        ]
        result = ListCasesResult(cases=cases)
        if page.next_cursor is not None:
            result["nextCursor"] = page.next_cursor
        return result

    def get_case(self, accessor: Accessor, *, case_id: str) -> GetCaseResult:
        self._require(accessor, CASES, VIEW_ONLY)
        case = self._reachable_case(accessor, case_id, "case.read")
        # One listing per entity type — a dozen keyed queries against one
        # partition. Honest cost, stated: the ports expose per-type listings
        # because that is the dominant access pattern; a single whole-partition
        # read would be a new port method nothing else needs yet.
        counts = {
            entity_type: len(self._entity_kind_records(case, entity_type))
            for entity_type in ENTITY_TYPES
        }
        return GetCaseResult(case=case_json(case), recordCounts=counts)

    def list_case_records(
        self,
        accessor: Accessor,
        *,
        case_id: str,
        entity_type: str,
        limit: object = None,
        cursor: object = None,
    ) -> ListCaseRecordsResult:
        entity_type = self._validated_entity_type(entity_type)
        self._require(accessor, feature_for_entity_type(entity_type), VIEW_ONLY)
        page_limit = parse_limit(limit)
        if cursor is not None and not isinstance(cursor, str):
            raise ValidationError("cursor is not valid")
        action = "document.read" if entity_type == DOCUMENTS_TYPE else "case.read"
        case = self._reachable_case(accessor, case_id, action)
        # Not logged per client: a listing is not, matching the API's
        # `GET /v1/firm/clients` — the case.read row above records it.
        records = self._entity_kind_records(case, entity_type)
        page, next_cursor = _paginate(
            records,
            limit=page_limit,
            cursor=cursor,
            binding=_offset_binding(case_id, f"records:{entity_type}"),
        )
        result = ListCaseRecordsResult(records=page)
        if next_cursor is not None:
            result["nextCursor"] = next_cursor
        return result

    def get_case_record(
        self,
        accessor: Accessor,
        *,
        case_id: str,
        entity_type: str,
        record_id: str,
    ) -> GetCaseRecordResult:
        entity_type = self._validated_entity_type(entity_type)
        self._require(accessor, feature_for_entity_type(entity_type), VIEW_ONLY)
        if not isinstance(record_id, str) or not record_id:
            raise ValidationError("recordId is required")
        action = "document.read" if entity_type == DOCUMENTS_TYPE else "case.read"
        case = self._reachable_case(accessor, case_id, action)
        record = self._find_record(case, entity_type, record_id, accessor)
        if record is None:
            # A record id from another case does not resolve (case_id is half
            # every store key), so this is the same 404 a foreign id gets.
            raise NotFoundError("record not found")
        return GetCaseRecordResult(record=record)

    def _find_record(
        self, case: Case, entity_type: str, record_id: str, accessor: Accessor
    ) -> dict[str, Any] | None:
        case_id = case.id
        if entity_type == CLIENTS_TYPE:
            # Only a client this case's debtors name resolves — an id from
            # the same firm's directory that this case does not link answers
            # the same not_found as one from another firm. A single-record
            # read is PII with its own subject, so it is logged under
            # CLIENT#<id> as the API's `GET /v1/firm/clients/<id>` is;
            # refused ones `denied`.
            client = next(
                (c for c in self._case_clients(case) if c.id == record_id), None
            )
            self.access_log.record(
                record_access(
                    client_id=record_id,
                    principal=accessor.subject,
                    action="client.read",
                    outcome="allowed" if client is not None else "denied",
                )
            )
            return firm_client_json(client) if client is not None else None
        if entity_type == DEBTORS:
            # Debtors are keyed by filing role, not id — the one type where a
            # by-id read is a scan of a ≤3-item listing.
            for debtor in self.debtor_store.list_for_case(case_id):
                if debtor.id == record_id:
                    return debtor_json(debtor)
            return None
        if entity_type == DOCUMENTS_TYPE:
            document = self.document_store.get(case_id, record_id)
            return document_json(document) if document is not None else None
        kind = COLLECTIONS[entity_type]
        entity = self.case_entity_store.get(case_id, kind, record_id)
        return entity_json(entity) if entity is not None else None

    # ── the candidate tools ──────────────────────────────────────────

    def propose_case_records(
        self,
        accessor: Accessor,
        *,
        case_id: str,
        proposals: object,
        client_id: str,
    ) -> ProposeCaseRecordsResult:
        self._require(accessor, INTAKE, ADD_EDIT)
        # Validate the WHOLE batch before resolving the case, so a malformed
        # request answers validation_failed without an access-log row saying
        # somebody aimed machinery at the case.
        drafts = parse_proposals(proposals)
        self._reachable_case(accessor, case_id, "candidate.propose")
        origin = CandidateOrigin(
            channel="mcp",
            # Both halves from the VERIFIED TOKEN, never an argument — the
            # attribution rule insolvia_core.candidates states.
            client_id=client_id,
            subject=accessor.subject,
        )
        stored: list[ProposedCandidate] = []
        for draft in drafts:
            candidate = create_candidate(draft, case_id=case_id, origin=origin)
            self.candidate_store.create(candidate)
            stored.append(
                ProposedCandidate(
                    candidateId=candidate.id,
                    entityType=candidate.entity_type,
                    status=candidate.status,
                )
            )
        return ProposeCaseRecordsResult(candidates=stored)

    def check_proposals(
        self,
        accessor: Accessor,
        *,
        case_id: str,
        candidate_ids: object = None,
        status: object = None,
        limit: object = None,
        cursor: object = None,
    ) -> CheckProposalsResult:
        self._require(accessor, INTAKE, VIEW_ONLY)
        if status is not None and status not in CANDIDATE_STATUSES:
            raise ValidationError(
                "status must be one of: " + ", ".join(CANDIDATE_STATUSES)
            )
        wanted: tuple[str, ...] | None = None
        if candidate_ids is not None:
            if not isinstance(candidate_ids, list | tuple) or not all(
                isinstance(value, str) for value in candidate_ids
            ):
                raise ValidationError("candidateIds must be an array of strings")
            wanted = tuple(candidate_ids)
        page_limit = parse_limit(limit)
        if cursor is not None and not isinstance(cursor, str):
            raise ValidationError("cursor is not valid")
        self._reachable_case(accessor, case_id, "case.read")
        candidates = [
            candidate_review_json(candidate)
            for candidate in self.candidate_store.list_for_case(case_id)
            if (wanted is None or candidate.id in wanted)
            and (status is None or candidate.status == status)
        ]
        page, next_cursor = _paginate(
            candidates,
            limit=page_limit,
            cursor=cursor,
            binding=_offset_binding(case_id, "candidates"),
        )
        result = CheckProposalsResult(candidates=page)
        if next_cursor is not None:
            result["nextCursor"] = next_cursor
        return result

    def withdraw_proposal(
        self, accessor: Accessor, *, case_id: str, candidate_id: str
    ) -> WithdrawProposalResult:
        self._require(accessor, INTAKE, ADD_EDIT)
        if not isinstance(candidate_id, str) or not candidate_id:
            raise ValidationError("candidateId is required")
        self._reachable_case(accessor, case_id, "candidate.withdraw")
        candidate = self.candidate_store.get(case_id, candidate_id)
        if candidate is None:
            raise NotFoundError("candidate not found")
        withdrawn = withdraw(candidate, subject=accessor.subject)
        # Conditional on the stored status still being pending: a withdrawal
        # racing the reviewer's acceptance loses, and the caller hears the
        # same ConflictError a sequential attempt would.
        stored = self.candidate_store.update(withdrawn, expected_status="pending")
        if stored is None:
            raise ConflictError("candidate has already been reviewed")
        return WithdrawProposalResult(candidateId=stored.id, status=stored.status)
