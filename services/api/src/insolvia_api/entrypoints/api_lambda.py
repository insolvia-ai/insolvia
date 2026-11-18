from __future__ import annotations

from asgiref.wsgi import WsgiToAsgi
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.adapters.aws.filing_credentials import (
    KmsCredentialSealer,
    filing_credentials_key_alias,
)
from insolvia_core.adapters.aws.jwks_provider import CognitoJwksProvider
from insolvia_core.adapters.aws.tax_id_cipher import KmsTaxIdCipher, case_key_alias
from insolvia_core.adapters.aws.user_directory import CognitoUserDirectory
from insolvia_core.adapters.postgres.access_log import PostgresAccessLog
from insolvia_core.adapters.postgres.candidate_store import PostgresCandidateStore
from insolvia_core.adapters.postgres.case_entity_store import PostgresCaseEntityStore
from insolvia_core.adapters.postgres.case_store import PostgresCaseStore
from insolvia_core.adapters.postgres.client_binding_store import (
    PostgresClientBindingStore,
)
from insolvia_core.adapters.postgres.debtor_store import PostgresDebtorStore
from insolvia_core.adapters.postgres.document_request_store import (
    PostgresDocumentRequestStore,
)
from insolvia_core.adapters.postgres.document_store import PostgresDocumentStore
from insolvia_core.adapters.postgres.filing_credentials import (
    PostgresFilingAuthorizationStore,
    PostgresFilingCredentialStore,
)
from insolvia_core.adapters.postgres.filing_store import PostgresFilingStore
from insolvia_core.adapters.postgres.firm_store import PostgresFirmStore
from insolvia_core.adapters.postgres.task_store import PostgresTaskStore
from insolvia_core.adapters.postgres.tax_id_store import PostgresTaxIdStore
from mangum import Mangum

from insolvia_api.adapters.aws.filing_queue import SqsFilingQueue
from insolvia_api.adapters.aws.job_queue import SqsJobQueue
from insolvia_api.adapters.aws.mailer_client import SigV4MailerClient
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.postgres.event_store import (
    PostgresCalendarTokenStore,
    PostgresEventStore,
)
from insolvia_api.adapters.postgres.filing_approval_store import (
    PostgresFilingApprovalStore,
)
from insolvia_api.adapters.postgres.job_store import PostgresJobStore
from insolvia_api.adapters.postgres.packet_store import PostgresPacketStore
from insolvia_api.adapters.postgres.waitlist_store import PostgresWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.logging import configure_logging
from insolvia_api.core.ports import FilingQueue, JobQueue, Mailer

configure_logging()

config = load_config()
# load_config() already refused to start without DATABASE_URL (ADR 0030):
# the firm stores, the portal bindings and the waitlist live there.
assert config.database_url is not None

# Hard-required, exactly like DATABASE_URL and unlike MAILER_API_URL
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

# Hard-required: the case and vault KMS key aliases are derived from it
# (case_key_alias, filing_credentials_key_alias). The access log is a
# Postgres table beside the cases (ADR 0030), so it cannot be missing while
# they are there.
if not config.case_table_name:
    raise RuntimeError("CASE_TABLE_NAME must be set for the Lambda entrypoint")

# NOT hard-required, unlike AUTH_CLIENT_ID: without it every /v1/portal/ route
# fails CLOSED with a 401 (insolvia_core.auth.portal_settings_or_raise) and
# every staff route is untouched — refusing to boot would take attorneys down
# over a portal nobody can reach yet. What IS refused is the one
# misconfiguration that would make the portal an open door: the same client
# id in both, which would make every staff token a portal token and back.
if (
    config.auth_portal_client_id
    and config.auth_portal_client_id == config.auth_client_id
):
    raise RuntimeError(
        "AUTH_PORTAL_CLIENT_ID must differ from AUTH_CLIENT_ID — the disjoint "
        "client ids ARE the audience check between staff and clients (ADR 0023)"
    )

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

# NOT the mailer's fallback shape, deliberately (ADR 0018). An in-memory
# queue in a Lambda accepts jobs nothing will ever run — the worker is a
# separate function — so absence composes None and the accept endpoint
# answers 503 while status reads keep working. Unset happens for real in
# exactly one window: this image rolling out ahead of the infra that creates
# the queue and publishes /insolvia/<env>/api/job-queue-url.
job_queue: JobQueue | None = None
if config.job_queue_url:
    job_queue = SqsJobQueue(config.job_queue_url)

# The filing queue (ADR 0024 PR 6), job_queue's shape and for its reason: an
# in-memory queue in a Lambda would record an approval no filing worker ever
# sees, so absence composes None and approving answers 503.
filing_queue: FilingQueue | None = None
if config.filing_queue_url:
    filing_queue = SqsFilingQueue(config.filing_queue_url)

app = create_app(
    ApiDependencies(
        config=config,
        waitlist_store=PostgresWaitlistStore(config.database_url),
        mailer=mailer,
        # Constructed here, at cold start, so its key cache lives for the
        # container's lifetime rather than one request. Nothing is fetched
        # until the first token arrives — an unauthenticated request path
        # never touches the network.
        jwks_provider=CognitoJwksProvider(config.auth_issuer_url),
        # A case open and a debtor link hold the client's row in their own
        # transaction (`lock_linkable_client`): the merge race.
        case_store=PostgresCaseStore(config.database_url),
        access_log=PostgresAccessLog(config.database_url),
        # Read on every authenticated request (ADR 0030: Postgres).
        firm_store=PostgresFirmStore(config.database_url),
        user_directory=CognitoUserDirectory(config.auth_user_pool_id),
        # The document ROW is Postgres; the bytes live in the bucket.
        document_store=PostgresDocumentStore(config.database_url),
        document_blobs=S3DocumentBlobStore(config.case_document_bucket),
        debtor_store=PostgresDebtorStore(config.database_url),
        # The sealed tax ids (issue 13.12 / #382): rows in Postgres, data
        # keys under the case KEY — whose alias is derived from the case
        # table's name (case_key_alias says why that is a derivation and not
        # a coincidence), so this needs no environment variable either.
        tax_id_store=PostgresTaxIdStore(config.database_url),
        tax_id_cipher=KmsTaxIdCipher(case_key_alias(config.case_table_name)),
        case_entity_store=PostgresCaseEntityStore(config.database_url),
        # The pipeline pair (ADR 0018): the queue is Optional — see above.
        job_store=PostgresJobStore(config.database_url),
        job_queue=job_queue,
        # Assembled packets (issue #96): written by the worker Lambda — the
        # API composes this store to read them.
        packet_store=PostgresPacketStore(config.database_url),
        # The review queue (8.9): candidate rows, written
        # by the extraction worker and the MCP service — the API composes
        # this store to list and resolve them.
        candidate_store=PostgresCandidateStore(config.database_url),
        # Events and the feed token (issue 14.6 / #358).
        event_store=PostgresEventStore(config.database_url),
        calendar_token_store=PostgresCalendarTokenStore(config.database_url),
        task_store=PostgresTaskStore(config.database_url),
        # The filing-credential vault (ADR 0024, guardrail 3): its own
        # table, which this role may fill and empty but never read the
        # envelope of (migration 0003), and its own key, named from the case
        # table's name. Seal-only — no opener is composed here.
        filing_credential_store=PostgresFilingCredentialStore(config.database_url),
        filing_credential_sealer=KmsCredentialSealer(
            filing_credentials_key_alias(config.case_table_name)
        ),
        # The signed authorization (guardrail 2).
        filing_authorization_store=PostgresFilingAuthorizationStore(
            config.database_url
        ),
        # The per-filing approval (guardrail 1): case children, and the
        # filing worker's own queue — Optional, above.
        filing_approval_store=PostgresFilingApprovalStore(config.database_url),
        filing_queue=filing_queue,
        # The filing records (ADR 0024 PR 8): read for the approval screen,
        # and a hand-back's resolution written onto them.
        filing_store=PostgresFilingStore(config.database_url),
        # A case's document requests (ADR 0023 PR 5 / #364): the case table
        # again.
        document_request_store=PostgresDocumentRequestStore(config.database_url),
        # The client portal's bindings (ADR 0023), in Postgres beside the
        # firm they belong to.
        client_binding_store=PostgresClientBindingStore(config.database_url),
    )
)
handler = Mangum(WsgiToAsgi(app), lifespan="off")  # type: ignore[no-untyped-call]
