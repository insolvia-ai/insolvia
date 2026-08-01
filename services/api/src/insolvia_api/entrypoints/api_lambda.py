from __future__ import annotations

from asgiref.wsgi import WsgiToAsgi
from mangum import Mangum

from insolvia_api.adapters.aws.jwks_provider import CognitoJwksProvider
from insolvia_api.adapters.aws.mailer_client import SigV4MailerClient
from insolvia_api.adapters.aws.waitlist_store import DynamoDbWaitlistStore
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.logging import configure_logging
from insolvia_api.core.ports import Mailer

configure_logging()

config = load_config()
if not config.waitlist_table_name:
    raise RuntimeError("WAITLIST_TABLE_NAME must be set for the Lambda entrypoint")

# Hard-required, exactly like WAITLIST_TABLE_NAME and unlike MAILER_API_URL
# (issue #79). There is no sensible degraded mode for auth: booting without a
# verifier would 401 every protected route, which looks like a client bug and
# is discovered by users. Refusing to boot is discovered by the deploy.
# Both parameters are published by infra/envs/<env>/main.tf as
# /insolvia/<env>/api/auth-issuer-url and .../auth-client-id, which the
# deploy workflow derives into AUTH_ISSUER_URL / AUTH_CLIENT_ID.
if not config.auth_issuer_url or not config.auth_client_id:
    raise RuntimeError(
        "AUTH_ISSUER_URL and AUTH_CLIENT_ID must be set for the Lambda entrypoint"
    )

# AWS adapters are composed here, mirroring mailer's api_lambda entrypoint.
# mailer_api_url is unset only if the mailer's SSM param hasn't been deployed
# yet in this environment — fall back to the in-memory client rather than
# fail the whole Lambda, mirroring the waitlist store's fallback shape.
mailer: Mailer
if config.mailer_api_url:
    mailer = SigV4MailerClient(config.mailer_api_url)
else:
    mailer = InMemoryMailerClient()

app = create_app(
    ApiDependencies(
        config=config,
        waitlist_store=DynamoDbWaitlistStore(config.waitlist_table_name),
        mailer=mailer,
        # Constructed here, at cold start, so its key cache lives for the
        # container's lifetime rather than one request. Nothing is fetched
        # until the first token arrives — an unauthenticated request path
        # never touches the network.
        jwks_provider=CognitoJwksProvider(config.auth_issuer_url),
    )
)
handler = Mangum(WsgiToAsgi(app), lifespan="off")  # type: ignore[no-untyped-call]
