from __future__ import annotations

from asgiref.wsgi import WsgiToAsgi
from mangum import Mangum

from insolvia_api.adapters.aws.access_log import DynamoDbAccessLog
from insolvia_api.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_api.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_api.adapters.aws.document_store import DynamoDbDocumentStore
from insolvia_api.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_api.adapters.aws.jwks_provider import CognitoJwksProvider
from insolvia_api.adapters.aws.mailer_client import SigV4MailerClient
from insolvia_api.adapters.aws.user_directory import CognitoUserDirectory
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

# Hard-required together, and together on purpose (issue 8.3). Booting with a
# case store but no access log would serve case data while recording nobody
# reading it, which is the one failure mode this pair exists to prevent — and
# it would be invisible, because every route would still answer 200.
if not config.case_table_name or not config.case_access_log_table_name:
    raise RuntimeError(
        "CASE_TABLE_NAME and CASE_ACCESS_LOG_TABLE_NAME must be set "
        "for the Lambda entrypoint"
    )

# Hard-required, and of everything on this list it is the one with no partial
# failure at all. Without the firm table nothing can establish which firm a
# signed-in person belongs to, so every authenticated route is unreachable —
# not degraded, unreachable. infra/envs/<env>/main.tf publishes the name as
# /insolvia/<env>/api/firm-table-name.
if not config.firm_table_name:
    raise RuntimeError("FIRM_TABLE_NAME must be set for the Lambda entrypoint")

# Hard-required, and it is the pool this deployment already verifies tokens
# against — published alongside the issuer as /insolvia/<env>/api/auth-user-pool-id.
# Booting without it would leave POST /v1/firm/users answering 500, which a
# firm admin discovers while trying to add their first colleague.
if not config.auth_user_pool_id:
    raise RuntimeError("AUTH_USER_POOL_ID must be set for the Lambda entrypoint")
# Hard-required for the same reason, and separately so the message names the
# one thing that is missing (issue 8.6). Booting without it would leave every
# document route answering 500 through _stores() — discovered by a user
# uploading a bank statement. Refusing to boot is discovered by the deploy.
# infra/envs/<env>/main.tf publishes the name as
# /insolvia/<env>/api/case-document-bucket, which the deploy workflow derives
# into CASE_DOCUMENT_BUCKET along with the rest of that namespace.
if not config.case_document_bucket:
    raise RuntimeError("CASE_DOCUMENT_BUCKET must be set for the Lambda entrypoint")

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
        case_store=DynamoDbCaseStore(config.case_table_name),
        access_log=DynamoDbAccessLog(config.case_access_log_table_name),
        # Its own table, not the case table — read on every authenticated
        # request, with a grant of its own (infra/modules/firm_store).
        firm_store=DynamoDbFirmStore(config.firm_table_name),
        user_directory=CognitoUserDirectory(config.auth_user_pool_id),
        # The document ROW lives in the case table — same table, same
        # partition, no second table — while the bytes live in the bucket.
        document_store=DynamoDbDocumentStore(config.case_table_name),
        document_blobs=S3DocumentBlobStore(config.case_document_bucket),
    )
)
handler = Mangum(WsgiToAsgi(app), lifespan="off")  # type: ignore[no-untyped-call]
