from __future__ import annotations

from asgiref.wsgi import WsgiToAsgi
from insolvia_core.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_core.adapters.aws.jwks_provider import CognitoJwksProvider
from insolvia_core.adapters.aws.user_directory import CognitoUserDirectory
from insolvia_core.auth import GOOGLE_ISSUERS, google_jwks_url
from mangum import Mangum

from insolvia_admin.adapters.aws.audit_log import DynamoDbAuditLog
from insolvia_admin.api.app_factory import create_app
from insolvia_admin.api.dependencies import AdminDependencies
from insolvia_admin.core.config import load_config
from insolvia_admin.core.logging import configure_logging

configure_logging()

config = load_config()

# EVERYTHING is hard-required here, unlike the tenant API's mailer seam:
# this service has no degraded mode worth shipping. A deployment that cannot
# verify staff tokens, reach the firm table, mint pool accounts, or write its
# audit row is not an admin service with a feature missing — it is one that
# either refuses every request (confusing) or performs unaudited mutations
# (forbidden by #178's whole premise). Refusing to boot is discovered by the
# deploy; the alternatives are discovered by an operator mid-provision.
for name, value in (
    ("FIRM_TABLE_NAME", config.firm_table_name),
    ("FIRM_USER_POOL_ID", config.firm_user_pool_id),
    ("ADMIN_AUDIT_TABLE_NAME", config.admin_audit_table_name),
    ("GOOGLE_CLIENT_ID", config.google_client_id),
):
    if not value:
        raise RuntimeError(f"{name} must be set for the admin Lambda entrypoint")

assert config.firm_table_name is not None
assert config.firm_user_pool_id is not None
assert config.admin_audit_table_name is not None

# THE TRUST BOUNDARY, in one constructor call: the JWKS provider is pointed
# at GOOGLE's keys, so the only tokens that can ever verify here are ones
# Google signed — a firm user's Cognito token has no key in this set before
# its claims are even read. (issuer_url is Google's canonical form; the
# explicit jwks_url is required because Google's keys do not live at
# <issuer>/.well-known/jwks.json — the adapter explains.)
jwks_provider = CognitoJwksProvider(GOOGLE_ISSUERS[0], jwks_url=google_jwks_url())

app = create_app(
    AdminDependencies(
        config=config,
        jwks_provider=jwks_provider,
        firm_store=DynamoDbFirmStore(config.firm_table_name),
        user_directory=CognitoUserDirectory(config.firm_user_pool_id),
        audit_log=DynamoDbAuditLog(config.admin_audit_table_name),
    )
)

handler = Mangum(WsgiToAsgi(app), lifespan="off")  # type: ignore[no-untyped-call]
