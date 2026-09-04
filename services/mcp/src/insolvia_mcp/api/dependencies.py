from __future__ import annotations

from dataclasses import dataclass

from insolvia_core.ports import (
    AccessLog,
    CandidateStore,
    CaseEntityStore,
    CaseStore,
    DebtorStore,
    DocumentStore,
    FirmStore,
    JwksProvider,
)

from insolvia_mcp.core.config import AppConfig


@dataclass(frozen=True)
class McpDependencies:
    """Everything the MCP surface needs, composed by an entrypoint.

    Unlike the API's ApiDependencies, nothing here is Optional except the
    JWKS provider: this service has NO public tools and no degraded mode —
    every field is load-bearing on every call, so an entrypoint that cannot
    compose one should refuse to exist rather than serve a surface that
    half-works. The bare development server composes the in-memory set.

    `jwks_provider` stays Optional for exactly the API's reason: absent means
    "this deployment cannot verify tokens", which fails CLOSED — the token
    verifier answers None and every request 401s. The Lambda entrypoint
    refuses to boot without one.
    """

    config: AppConfig
    case_store: CaseStore
    case_entity_store: CaseEntityStore
    debtor_store: DebtorStore
    document_store: DocumentStore
    candidate_store: CandidateStore
    access_log: AccessLog
    firm_store: FirmStore
    jwks_provider: JwksProvider | None = None
