from __future__ import annotations

from insolvia_core.adapters.aws.jwks_provider import CognitoJwksProvider
from insolvia_core.adapters.aws.user_directory import CognitoUserDirectory
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_core.adapters.postgres.firm_store import PostgresFirmStore
from insolvia_core.auth import GOOGLE_ISSUERS, google_jwks_url
from insolvia_core.ports import JwksProvider, UserDirectory

from insolvia_admin.adapters.aws.audit_log import DynamoDbAuditLog
from insolvia_admin.adapters.memory.audit_log import MemoryAuditLog
from insolvia_admin.api.app_factory import create_app
from insolvia_admin.api.dependencies import AdminDependencies
from insolvia_admin.core.audit import AuditLog
from insolvia_admin.core.config import load_config
from insolvia_admin.core.logging import configure_logging

config = load_config()
if config.environment != "local":
    raise RuntimeError("the development server requires INSOLVIA_ENV=local")

configure_logging()

# Adapter composition, mirroring the tenant API's dev server. The firm store
# is this machine's Docker Postgres (ADR 0030; load_config() refused to start
# without DATABASE_URL) — the same database the local API reads. The audit
# table is still DynamoDB until it moves (#623): this machine's real table
# when ADMIN_AUDIT_TABLE_NAME is set, in-memory otherwise. Local data is
# throwaway, so an in-memory audit row beside a local firm is acceptable here
# and nowhere else; api_lambda.py requires both.
assert config.database_url is not None
firm_store = PostgresFirmStore(config.database_url)
audit_log: AuditLog
if config.admin_audit_table_name:
    audit_log = DynamoDbAuditLog(config.admin_audit_table_name)
else:
    audit_log = MemoryAuditLog()

# Its own condition, like the API dev server's: provisioning is the one
# operation that reaches Cognito, and with a real pool named the invite email
# flow is testable end to end on a laptop. Unset means the in-memory
# directory, which mints subjects and sends nothing.
user_directory: UserDirectory
if config.firm_user_pool_id:
    user_directory = CognitoUserDirectory(config.firm_user_pool_id)
else:
    user_directory = MemoryUserDirectory()

# Staff auth needs only the client id: Google's issuer and keys are
# constants. Unset, the provider is composed anyway and google_settings_or_
# raise fails closed with NOT_CONFIGURED — every staff route 401s, the
# public /health keeps working.
jwks_provider: JwksProvider = CognitoJwksProvider(
    GOOGLE_ISSUERS[0], jwks_url=google_jwks_url()
)

app = create_app(
    AdminDependencies(
        config=config,
        jwks_provider=jwks_provider,
        firm_store=firm_store,
        user_directory=user_directory,
        audit_log=audit_log,
    )
)
