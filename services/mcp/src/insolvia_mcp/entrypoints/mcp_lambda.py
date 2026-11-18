from __future__ import annotations

from insolvia_core.adapters.aws.jwks_provider import CognitoJwksProvider
from insolvia_core.adapters.postgres.access_log import PostgresAccessLog
from insolvia_core.adapters.postgres.candidate_store import PostgresCandidateStore
from insolvia_core.adapters.postgres.case_entity_store import PostgresCaseEntityStore
from insolvia_core.adapters.postgres.case_store import PostgresCaseStore
from insolvia_core.adapters.postgres.debtor_store import PostgresDebtorStore
from insolvia_core.adapters.postgres.document_store import PostgresDocumentStore
from insolvia_core.adapters.postgres.firm_store import PostgresFirmStore
from mangum import Mangum

from insolvia_mcp.api.dependencies import McpDependencies
from insolvia_mcp.api.server import create_asgi_app
from insolvia_mcp.core.config import load_config
from insolvia_mcp.core.logging import configure_logging

configure_logging()

config = load_config()

# Hard-required, all of it, and refusal is the point: this service has no
# public tools and no degraded mode. Booting without auth would 401 every
# call (which looks like a harness bug and is discovered by attorneys);
# booting without a store would 500 the tools that need it. Refusing to boot
# is discovered by the deploy. The parameters are published by
# infra/envs/<env>/main.tf as /insolvia/<env>/mcp/*, which the deploy
# workflow derives into these environment variables — the same shape as the
# API's namespace.
if not config.auth_issuer_url or not config.auth_client_ids:
    raise RuntimeError(
        "AUTH_ISSUER_URL and AUTH_CLIENT_IDS must be set for the Lambda entrypoint"
    )

if not config.case_table_name:
    raise RuntimeError("CASE_TABLE_NAME must be set for the Lambda entrypoint")

# load_config() already refused to start without DATABASE_URL (ADR 0030):
# the firm store lives there.
assert config.database_url is not None

app = create_asgi_app(
    McpDependencies(
        config=config,
        # The SAME tables the API composes — this is a second surface over
        # the same stores (ADR 0016), with its own execution role scoped to
        # what this service exports.
        case_store=PostgresCaseStore(config.database_url),
        case_entity_store=PostgresCaseEntityStore(config.database_url),
        debtor_store=PostgresDebtorStore(config.database_url),
        document_store=PostgresDocumentStore(config.database_url),
        candidate_store=PostgresCandidateStore(config.database_url),
        # Append-only: this role may INSERT and nothing else (migration 0003).
        access_log=PostgresAccessLog(config.database_url),
        firm_store=PostgresFirmStore(config.database_url),
        # Constructed at cold start so its key cache lives for the
        # container's lifetime; nothing is fetched until the first token.
        jwks_provider=CognitoJwksProvider(config.auth_issuer_url),
    )
)

# lifespan="on": the streamable-HTTP app's lifespan runs the SDK's session
# manager, and tool calls fail without it — "auto" would silently swallow a
# lifespan failure and serve a broken endpoint. Mangum runs that lifespan
# around EVERY invocation, not once per container, which is why the app
# rebuilds its transport at each startup (api/server.py,
# StreamableHttpApp) — the SDK forbids running one session manager
# twice, and a warm container's second request used to 500 on exactly that.
# tests/unit/test_lambda_handler.py invokes this object twice to hold it.
handler = Mangum(app, lifespan="on")
