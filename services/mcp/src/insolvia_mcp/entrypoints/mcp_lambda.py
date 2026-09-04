from __future__ import annotations

from insolvia_core.adapters.aws.access_log import DynamoDbAccessLog
from insolvia_core.adapters.aws.case_entity_store import DynamoDbCaseEntityStore
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.aws.document_store import DynamoDbDocumentStore
from insolvia_core.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_core.adapters.aws.jwks_provider import CognitoJwksProvider
from mangum import Mangum

from insolvia_mcp.adapters.aws.candidate_store import DynamoDbCandidateStore
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
if not config.auth_issuer_url or not config.auth_client_id:
    raise RuntimeError(
        "AUTH_ISSUER_URL and AUTH_CLIENT_ID must be set for the Lambda entrypoint"
    )

# The pair rule services/api states: serving case data while recording nobody
# reading it is the one failure mode this pair exists to prevent.
if not config.case_table_name or not config.case_access_log_table_name:
    raise RuntimeError(
        "CASE_TABLE_NAME and CASE_ACCESS_LOG_TABLE_NAME must be set "
        "for the Lambda entrypoint"
    )

if not config.firm_table_name:
    raise RuntimeError("FIRM_TABLE_NAME must be set for the Lambda entrypoint")

app = create_asgi_app(
    McpDependencies(
        config=config,
        # The SAME tables the API composes — this is a second surface over
        # the same stores (ADR 0016), with its own execution role scoped to
        # what this service exports.
        case_store=DynamoDbCaseStore(config.case_table_name),
        case_entity_store=DynamoDbCaseEntityStore(config.case_table_name),
        debtor_store=DynamoDbDebtorStore(config.case_table_name),
        document_store=DynamoDbDocumentStore(config.case_table_name),
        candidate_store=DynamoDbCandidateStore(config.case_table_name),
        access_log=DynamoDbAccessLog(config.case_access_log_table_name),
        firm_store=DynamoDbFirmStore(config.firm_table_name),
        # Constructed at cold start so its key cache lives for the
        # container's lifetime; nothing is fetched until the first token.
        jwks_provider=CognitoJwksProvider(config.auth_issuer_url),
    )
)

# lifespan="on": the streamable-HTTP app's lifespan runs the SDK's session
# manager, and tool calls fail without it — "auto" would silently swallow a
# lifespan failure and serve a broken endpoint.
handler = Mangum(app, lifespan="on")
