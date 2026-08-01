from __future__ import annotations

from insolvia_api.adapters.aws.jwks_provider import CognitoJwksProvider
from insolvia_api.adapters.aws.waitlist_store import DynamoDbWaitlistStore
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.logging import configure_logging
from insolvia_api.core.ports import JwksProvider, WaitlistStore

config = load_config()
if config.environment != "local":
    raise RuntimeError("the development server requires INSOLVIA_ENV=local")

configure_logging()

# Adapter composition, mirroring mailer's development server. With
# WAITLIST_TABLE_NAME set (the compose stack / dev-aws layer — this machine's
# real per-developer table) the real DynamoDB adapter runs; unset, the bare
# dev server falls back to the in-memory store (echo=True logs each
# submission so local marketing-site dev can see them arrive).
waitlist_store: WaitlistStore
if config.waitlist_table_name:
    waitlist_store = DynamoDbWaitlistStore(config.waitlist_table_name)
else:
    waitlist_store = MemoryWaitlistStore(echo=True)

# The plain development server never sends real mail — mirroring the memory
# waitlist store, this is local-only and never composed in a deployed
# environment (adapters/aws/mailer_client.py's SigV4MailerClient is).
mailer = InMemoryMailerClient()

# Auth against this machine's own Cognito pool when AUTH_ISSUER_URL and
# AUTH_CLIENT_ID are set (infra/envs/dev publishes both; export them into
# services/api/.env to use them). Unset, the provider is simply absent and
# every protected route answers 401 — the bare dev server keeps working for
# the public routes, and auth fails CLOSED rather than waving requests
# through. This is the ONE entrypoint that tolerates missing auth config;
# api_lambda.py refuses to boot without it.
jwks_provider: JwksProvider | None = None
if config.auth_issuer_url and config.auth_client_id:
    jwks_provider = CognitoJwksProvider(config.auth_issuer_url)

app = create_app(
    ApiDependencies(
        config=config,
        waitlist_store=waitlist_store,
        mailer=mailer,
        jwks_provider=jwks_provider,
    )
)
