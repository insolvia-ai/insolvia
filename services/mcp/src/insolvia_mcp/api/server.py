"""The MCP server: eight tools, one Streamable HTTP endpoint.

Built on the official MCP Python SDK rather than hand-rolled JSON-RPC
(ADR 0016 §1): the SDK owns protocol-version negotiation, the 2026-07-28
stateless shape, header/body validation (Mcp-Method / Mcp-Name, error
-32020), and era compatibility for older harnesses. What this module owns is
the surface: registration of the eight tools of mcp-surface.md, per-call
accessor resolution, and the result/error envelope (api/results.py).

The tool list is STATIC — the same eight for every authenticated session
(list-visible, call-denied; mcp-surface.md § Permission gates). Nothing here
filters the list by permissions, because permissions are revocable mid-hour
and a filtered list would be stale the moment it mattered; a call the
caller's permissions do not admit answers `permission_denied` instead.
"""

# The camelCase tool arguments below are the WIRE property names — the SDK
# derives each tool's inputSchema from its Python signature, and the schema's
# property names are the contract mcp-surface.md publishes (caseId,
# entityType, recordId, candidateIds). Renaming them to satisfy N803 would
# rename the surface.
# ruff: noqa: N803

from __future__ import annotations

from typing import Annotated, Any
from urllib.parse import urlparse

from insolvia_core.access import Accessor
from insolvia_core.errors import ForbiddenError
from mcp.server import MCPServer
from mcp.server.auth.middleware.auth_context import get_access_token
from mcp.server.auth.settings import AuthSettings
from mcp.server.transport_security import TransportSecuritySettings
from mcp.types import CallToolResult, ToolAnnotations
from pydantic import AnyHttpUrl
from starlette.applications import Starlette

from insolvia_mcp.api.auth import CognitoTokenVerifier, resolve_accessor
from insolvia_mcp.api.dependencies import McpDependencies
from insolvia_mcp.api.results import guarded, success
from insolvia_mcp.core.candidates import PROPOSABLE_ENTITY_TYPES
from insolvia_mcp.core.tools import (
    ENTITY_TYPES,
    CaseTools,
    CheckProposalsResult,
    GetCaseRecordResult,
    GetCaseResult,
    ListCaseRecordsResult,
    ListCasesResult,
    ProposeCaseRecordsResult,
    WhoamiResult,
    WithdrawProposalResult,
    whoami,
)

# mcp-surface.md § Limits: request body ≤ 256 KiB — the debtor route's
# ceiling, because proposals carry entity-sized payloads.
MAX_REQUEST_BYTES = 256 * 1024

_READ_ONLY = ToolAnnotations(read_only_hint=True)
# Nothing on this surface is destructive — a proposal is additive and a
# withdrawal retracts the caller's own pending row — so no tool carries
# destructiveHint (mcp-surface.md § Protocol posture).
_WRITE = ToolAnnotations(read_only_hint=False, destructive_hint=False)

_ENTITY_TYPE_DOC = "One of: " + ", ".join(ENTITY_TYPES) + "."


def _accessor_or_refuse(deps: McpDependencies) -> Accessor:
    """Resolve the verified token's subject to a firm accessor, per call.

    Raises the same one-message ForbiddenError the API's current_accessor
    raises: "you were disabled", "you were never added" and "your firm is
    suspended" are the same instruction to the caller.
    """
    token = get_access_token()
    if token is None or not token.subject:
        # The auth middleware refuses unauthenticated requests before any
        # tool runs, so a missing token here is a composition bug, not a
        # caller error — surfaced as `internal` by the guard.
        raise RuntimeError("tool invoked without an authenticated token")
    accessor = resolve_accessor(deps.firm_store, token.subject)
    if accessor is None:
        raise ForbiddenError("your account is not active in a firm")
    return accessor


def create_mcp_server(deps: McpDependencies) -> MCPServer:
    tools = CaseTools(
        case_store=deps.case_store,
        case_entity_store=deps.case_entity_store,
        debtor_store=deps.debtor_store,
        document_store=deps.document_store,
        candidate_store=deps.candidate_store,
        access_log=deps.access_log,
    )

    config = deps.config
    # The metadata endpoints need an issuer to advertise even on a bare local
    # server with no pool configured; verification still fails closed there
    # (api/auth.py), so this default can only ever 401 harder, never admit.
    issuer = config.auth_issuer_url or "http://127.0.0.1/auth-not-configured"

    server = MCPServer(
        "insolvia",
        title="Insolvia case management",
        instructions=(
            "Bankruptcy case preparation for the signed-in attorney's firm. "
            "Reads return live case data; writes land as candidate records a "
            "human reviews before anything becomes case data. Start with "
            "whoami, find matters with list_cases, and poll check_proposals "
            "for review outcomes."
        ),
        token_verifier=CognitoTokenVerifier(
            issuer_url=config.auth_issuer_url,
            client_ids=config.auth_client_ids,
            jwks_provider=deps.jwks_provider,
        ),
        auth=AuthSettings(
            issuer_url=AnyHttpUrl(issuer),
            resource_server_url=AnyHttpUrl(config.resource_url),
            # Cognito app clients mint access tokens whose scopes are the
            # pool's own; per-feature authorization deliberately lives in the
            # firm store (ADR 0009), not in scopes, so none are required here.
            required_scopes=None,
        ),
    )

    @server.tool(
        name="whoami",
        title="Who am I",
        annotations=_READ_ONLY,
        description=(
            "The caller's firm, display name, and per-feature permissions — "
            "or the fact that they have no firm. The /v1/me of this surface."
        ),
    )
    @guarded("whoami")
    def whoami_tool() -> Annotated[CallToolResult, WhoamiResult]:
        token = get_access_token()
        if token is None or not token.subject:
            raise RuntimeError("tool invoked without an authenticated token")
        accessor = resolve_accessor(deps.firm_store, token.subject)
        return success(whoami(accessor))

    @server.tool(
        name="list_cases",
        title="List cases",
        annotations=_READ_ONLY,
        description=(
            "The cases the caller may see, newest first, paginated. "
            "Optional status filter: intake, ready_to_file, or filed."
        ),
    )
    @guarded("list_cases")
    def list_cases_tool(
        status: str | None = None,
        limit: int | None = None,
        cursor: str | None = None,
    ) -> Annotated[CallToolResult, ListCasesResult]:
        accessor = _accessor_or_refuse(deps)
        return success(
            tools.list_cases(accessor, status=status, limit=limit, cursor=cursor)
        )

    @server.tool(
        name="get_case",
        title="Get case",
        annotations=_READ_ONLY,
        description="One case plus per-entity-type record counts.",
    )
    @guarded("get_case")
    def get_case_tool(caseId: str) -> Annotated[CallToolResult, GetCaseResult]:
        accessor = _accessor_or_refuse(deps)
        return success(tools.get_case(accessor, case_id=caseId))

    @server.tool(
        name="list_case_records",
        title="List case records",
        annotations=_READ_ONLY,
        description=(
            "One entity type's records within one case, paginated. "
            f"entityType: {_ENTITY_TYPE_DOC}"
        ),
    )
    @guarded("list_case_records")
    def list_case_records_tool(
        caseId: str,
        entityType: str,
        limit: int | None = None,
        cursor: str | None = None,
    ) -> Annotated[CallToolResult, ListCaseRecordsResult]:
        accessor = _accessor_or_refuse(deps)
        return success(
            tools.list_case_records(
                accessor,
                case_id=caseId,
                entity_type=entityType,
                limit=limit,
                cursor=cursor,
            )
        )

    @server.tool(
        name="get_case_record",
        title="Get case record",
        annotations=_READ_ONLY,
        description=f"One record by id. entityType: {_ENTITY_TYPE_DOC}",
    )
    @guarded("get_case_record")
    def get_case_record_tool(
        caseId: str, entityType: str, recordId: str
    ) -> Annotated[CallToolResult, GetCaseRecordResult]:
        accessor = _accessor_or_refuse(deps)
        return success(
            tools.get_case_record(
                accessor, case_id=caseId, entity_type=entityType, record_id=recordId
            )
        )

    @server.tool(
        name="propose_case_records",
        title="Propose case records",
        annotations=_WRITE,
        description=(
            "Write a batch of CANDIDATE records for human review — nothing "
            "becomes case data until a person accepts it in the app. 1-25 "
            "proposals per call, each {entityType, payload, externalRef?, "
            "note?}; payload mirrors the target entity's wire shape. "
            "Proposable entity types: " + ", ".join(PROPOSABLE_ENTITY_TYPES) + "."
        ),
    )
    @guarded("propose_case_records")
    def propose_case_records_tool(
        caseId: str, proposals: list[dict[str, Any]]
    ) -> Annotated[CallToolResult, ProposeCaseRecordsResult]:
        accessor = _accessor_or_refuse(deps)
        token = get_access_token()
        client_id = token.client_id if token is not None else ""
        return success(
            tools.propose_case_records(
                accessor, case_id=caseId, proposals=proposals, client_id=client_id
            )
        )

    @server.tool(
        name="check_proposals",
        title="Check proposals",
        annotations=_READ_ONLY,
        description=(
            "The review status of this surface's candidates: pending, "
            "accepted, corrected (with the human's correctedPayload), "
            "rejected, or withdrawn — plus the resulting record id once "
            "accepted. Poll this; review happens in the app, often hours "
            "after the proposing session ends."
        ),
    )
    @guarded("check_proposals")
    def check_proposals_tool(
        caseId: str,
        candidateIds: list[str] | None = None,
        status: str | None = None,
        limit: int | None = None,
        cursor: str | None = None,
    ) -> Annotated[CallToolResult, CheckProposalsResult]:
        accessor = _accessor_or_refuse(deps)
        return success(
            tools.check_proposals(
                accessor,
                case_id=caseId,
                candidate_ids=candidateIds,
                status=status,
                limit=limit,
                cursor=cursor,
            )
        )

    @server.tool(
        name="withdraw_proposal",
        title="Withdraw proposal",
        annotations=_WRITE,
        description=(
            "Retract the caller's own still-pending candidate so it does not "
            "sit in a reviewer's queue. Only the proposer may withdraw, and "
            "only while pending."
        ),
    )
    @guarded("withdraw_proposal")
    def withdraw_proposal_tool(
        caseId: str, candidateId: str
    ) -> Annotated[CallToolResult, WithdrawProposalResult]:
        accessor = _accessor_or_refuse(deps)
        return success(
            tools.withdraw_proposal(accessor, case_id=caseId, candidate_id=candidateId)
        )

    return server


def create_asgi_app(deps: McpDependencies) -> Starlette:
    """The Streamable HTTP app, stateless and single-JSON-response.

    Revision 2026-07-28 removed protocol-level sessions and the standalone
    GET stream, so a fully stateless single-JSON-response server is
    spec-conformant — exactly the shape Lambda wants: our tools never need a
    mid-call SSE stream, every call is a bounded read or a bounded write
    (mcp-surface.md § Protocol posture). The SDK still bridges older
    initialize-era harnesses on the same endpoint.
    """
    server = create_mcp_server(deps)
    resource_host = urlparse(deps.config.resource_url).netloc
    allowed_hosts = [resource_host]
    if deps.config.environment == "local":
        # The development server binds loopback on whatever port dev-up
        # chose; a deployed environment answers exactly its custom domain.
        allowed_hosts += ["127.0.0.1:*", "localhost:*", "127.0.0.1", "localhost"]
    return server.streamable_http_app(
        json_response=True,
        stateless_http=True,
        max_request_body_size=MAX_REQUEST_BYTES,
        # The spec's DNS-rebinding MUST: validate Origin (absent is fine —
        # harnesses are not browsers; present and unlisted is a 403) and pin
        # the Host header to this environment's own hostname.
        transport_security=TransportSecuritySettings(
            enable_dns_rebinding_protection=True,
            allowed_hosts=allowed_hosts,
            allowed_origins=[],
        ),
    )
