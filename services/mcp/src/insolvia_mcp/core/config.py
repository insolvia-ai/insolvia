from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass

from insolvia_core.errors import ValidationError

SERVICE_NAME = "insolvia-mcp"

ENVIRONMENTS = ("local", "staging", "production")

# Where the MCP endpoint lives per environment. This is the CANONICAL RESOURCE
# URI (RFC 8707 / RFC 9728): the string in the protected-resource metadata,
# the `resource` parameter harnesses send, and the URL a directory listing
# names — one string, everywhere, which is why the service has its own
# hostname (ADR 0016). `local` is the development server's default bind.
_RESOURCE_URLS: dict[str, str] = {
    "production": "https://mcp.insolvia.ai/mcp",
    "staging": "https://staging-mcp.insolvia.ai/mcp",
    "local": "http://127.0.0.1:8788/mcp",
}


@dataclass(frozen=True)
class AppConfig:
    """The service configuration, parsed and validated once at composition time.

    load_config is the only real constructor — the field defaults exist so
    tests can build a local config tersely, and they match INSOLVIA_ENV=local.
    Everything is read in load_config; nothing else in the package touches
    os.environ.
    """

    environment: str
    case_table_name: str | None = None
    case_access_log_table_name: str | None = None
    firm_table_name: str | None = None
    auth_issuer_url: str | None = None
    auth_client_ids: tuple[str, ...] = ()
    resource_url: str = _RESOURCE_URLS["local"]


def load_config(environ: Mapping[str, str] | None = None) -> AppConfig:
    """Build the configuration from the environment.

    INSOLVIA_ENV (local|staging|production) defaults to "local". The three
    table names are the SAME tables the API composes — this service is a
    second surface over the same stores (ADR 0016), so the values are the
    same SSM parameters re-derived by the deploy workflow:
    CASE_TABLE_NAME, CASE_ACCESS_LOG_TABLE_NAME, FIRM_TABLE_NAME. Unset means
    the in-memory stores, which only unit tests and the bare development
    server use; local dev names this machine's real per-developer tables
    (scripts/dev-aws-setup.sh), exactly as services/api does — no emulator.

    AUTH_ISSUER_URL and AUTH_CLIENT_IDS are the Cognito pool's OIDC issuer
    and the comma-separated allowlist of MCP app client ids an access token
    may name — one pre-registered client per harness (issue #261; Cognito
    has no dynamic client registration), each a Terraform resource in
    infra/modules/auth. The set is DELIBERATELY DISJOINT from the app's
    client id (ADR 0016): each service verifies exactly its own clients,
    which is the audience check Cognito's aud-less access tokens can't carry
    — an app token presented here fails closed. Unset follows the API's
    rule: there is no degraded mode where a protected tool stops checking;
    the entrypoint refuses to boot without both, and the bare development
    server answers 401 on everything.

    MCP_RESOURCE_URL overrides the canonical resource URI, which only local
    dev needs (a non-default port); staging and prod take the constant for
    their environment so the metadata cannot drift from the hostname.
    """
    source = os.environ if environ is None else environ
    environment = source.get("INSOLVIA_ENV", "local")
    if environment not in ENVIRONMENTS:
        raise ValidationError(
            f"INSOLVIA_ENV must be one of {', '.join(ENVIRONMENTS)}, "
            f"got {environment!r}"
        )
    return AppConfig(
        environment=environment,
        case_table_name=source.get("CASE_TABLE_NAME") or None,
        case_access_log_table_name=source.get("CASE_ACCESS_LOG_TABLE_NAME") or None,
        firm_table_name=source.get("FIRM_TABLE_NAME") or None,
        auth_issuer_url=source.get("AUTH_ISSUER_URL") or None,
        auth_client_ids=_client_ids(source.get("AUTH_CLIENT_IDS")),
        resource_url=source.get("MCP_RESOURCE_URL") or _RESOURCE_URLS[environment],
    )


def _client_ids(raw: str | None) -> tuple[str, ...]:
    """A comma-separated allowlist, whitespace-tolerant, empties dropped —
    so a trailing comma in an SSM parameter cannot smuggle an empty string
    into the allowlist (the verifier would refuse it anyway, but a malformed
    allowlist should read as "not configured", not "configured oddly")."""
    if not raw:
        return ()
    return tuple(value.strip() for value in raw.split(",") if value.strip())
