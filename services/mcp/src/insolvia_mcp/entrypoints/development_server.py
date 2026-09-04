from __future__ import annotations

from insolvia_core.adapters.aws.access_log import DynamoDbAccessLog
from insolvia_core.adapters.aws.case_entity_store import DynamoDbCaseEntityStore
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.aws.document_store import DynamoDbDocumentStore
from insolvia_core.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_core.adapters.aws.jwks_provider import CognitoJwksProvider
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.document_store import MemoryDocumentStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.ports import (
    AccessLog,
    CaseEntityStore,
    CaseStore,
    DebtorStore,
    DocumentStore,
    FirmStore,
    JwksProvider,
)

from insolvia_mcp.adapters.aws.candidate_store import DynamoDbCandidateStore
from insolvia_mcp.adapters.memory.candidate_store import MemoryCandidateStore
from insolvia_mcp.api.dependencies import McpDependencies
from insolvia_mcp.api.server import create_asgi_app
from insolvia_mcp.core.config import load_config
from insolvia_mcp.core.logging import configure_logging
from insolvia_mcp.core.ports import CandidateStore

config = load_config()
if config.environment != "local":
    raise RuntimeError("the development server requires INSOLVIA_ENV=local")

configure_logging()

# Adapter composition, mirroring the API's development server: with this
# machine's real stores named (services/mcp/.env, written from the same
# per-developer dev environment scripts/dev-aws-setup.sh provisions) the AWS
# adapters run against them — no emulator, the dev table IS the database —
# and the bare dev server falls back to in-memory wholesale. The GROUP moves
# together, for the API dev server's reason: a real case store beside an
# in-memory candidate store is a half-state nobody can debug.
case_store: CaseStore
case_entity_store: CaseEntityStore
debtor_store: DebtorStore
document_store: DocumentStore
candidate_store: CandidateStore
access_log: AccessLog
firm_store: FirmStore
if (
    config.case_table_name
    and config.case_access_log_table_name
    and config.firm_table_name
):
    case_store = DynamoDbCaseStore(config.case_table_name)
    case_entity_store = DynamoDbCaseEntityStore(config.case_table_name)
    debtor_store = DynamoDbDebtorStore(config.case_table_name)
    document_store = DynamoDbDocumentStore(config.case_table_name)
    candidate_store = DynamoDbCandidateStore(config.case_table_name)
    access_log = DynamoDbAccessLog(config.case_access_log_table_name)
    firm_store = DynamoDbFirmStore(config.firm_table_name)
else:
    case_store = MemoryCaseStore()
    case_entity_store = MemoryCaseEntityStore()
    debtor_store = MemoryDebtorStore()
    document_store = MemoryDocumentStore()
    candidate_store = MemoryCandidateStore()
    access_log = MemoryAccessLog()
    # Empty at every start: a signed-in developer with no firm is exactly the
    # "authenticated but not provisioned" case whoami exists to report, and
    # inventing a firm here would hide it until staging.
    firm_store = MemoryFirmStore()

# Auth against this machine's own Cognito pool when AUTH_ISSUER_URL and
# AUTH_CLIENT_IDS are set. Unset, the provider is simply absent and every
# call answers 401 — the bare dev server still serves the protected-resource
# metadata, and auth fails CLOSED rather than waving requests through. This
# is the ONE entrypoint that tolerates missing auth config; mcp_lambda.py
# refuses to boot without it.
jwks_provider: JwksProvider | None = None
if config.auth_issuer_url and config.auth_client_ids:
    jwks_provider = CognitoJwksProvider(config.auth_issuer_url)

app = create_asgi_app(
    McpDependencies(
        config=config,
        case_store=case_store,
        case_entity_store=case_entity_store,
        debtor_store=debtor_store,
        document_store=document_store,
        candidate_store=candidate_store,
        access_log=access_log,
        firm_store=firm_store,
        jwks_provider=jwks_provider,
    )
)
