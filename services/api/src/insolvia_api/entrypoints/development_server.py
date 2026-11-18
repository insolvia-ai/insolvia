from __future__ import annotations

from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.adapters.aws.filing_credentials import (
    KmsCredentialSealer,
    filing_credentials_key_alias,
)
from insolvia_core.adapters.aws.jwks_provider import CognitoJwksProvider
from insolvia_core.adapters.aws.tax_id_cipher import KmsTaxIdCipher, case_key_alias
from insolvia_core.adapters.aws.user_directory import CognitoUserDirectory
from insolvia_core.adapters.development_keys import development_key_adapters
from insolvia_core.adapters.memory.document_blobs import MemoryDocumentBlobStore
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
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
from insolvia_core.ports import (
    AccessLog,
    CandidateStore,
    CaseEntityStore,
    CaseStore,
    DebtorStore,
    DocumentBlobStore,
    DocumentRequestStore,
    DocumentStore,
    FilingAuthorizationStore,
    FilingCredentialSealer,
    FilingCredentialStore,
    FilingStore,
    JwksProvider,
    TaskStore,
    TaxIdCipher,
    TaxIdStore,
    UserDirectory,
)

from insolvia_api.adapters.aws.filing_queue import SqsFilingQueue
from insolvia_api.adapters.aws.job_queue import SqsJobQueue
from insolvia_api.adapters.aws.mailer_client import SigV4MailerClient
from insolvia_api.adapters.memory.filing_queue import MemoryFilingQueue
from insolvia_api.adapters.memory.in_process_job_queue import InProcessJobQueue
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
from insolvia_api.core.ports import (
    CalendarTokenStore,
    EventStore,
    FilingApprovalStore,
    FilingQueue,
    JobQueue,
    JobStore,
    Mailer,
    PacketStore,
)
from insolvia_api.entrypoints.workers import compose_workers

config = load_config()
if config.environment != "local":
    raise RuntimeError("the development server requires INSOLVIA_ENV=local")

configure_logging()

# The Postgres stores (ADR 0030): this machine's Docker Postgres, migrated by
# dev-up.sh. load_config() refused to start without DATABASE_URL, so there is
# no in-memory fallback to drift to. The firm group, the portal bindings,
# the waitlist and the case stores (#622) live there.
assert config.database_url is not None
waitlist_store = PostgresWaitlistStore(config.database_url)
firm_store = PostgresFirmStore(config.database_url)
client_binding_store = PostgresClientBindingStore(config.database_url)

# The plain development server never sends real mail. Unset MAILER_API_URL
# records sends in memory — mirroring the memory waitlist store. Set, it
# posts to THAT mailer: pointed at the mailer's own local stack (it lives in
# the insolvia-services repo — `./scripts/dev-up.sh` starts it from a sibling
# checkout, or run its `services/mailer/scripts/dev-up.sh` there; then
# MAILER_API_URL=http://127.0.0.1:8026 — or http://host.docker.internal:8026
# from the API's compose container), every message lands in Mailpit at :8025 and nothing
# leaves the machine, which is how the client-portal invitation's context
# mail (ADR 0023) is checked locally. The local mailer ignores the SigV4
# signature API Gateway would verify, so the client needs only whatever
# credentials the developer's session already exports to sign with.
mailer: Mailer
if config.mailer_api_url:
    mailer = SigV4MailerClient(config.mailer_api_url)
else:
    mailer = InMemoryMailerClient()

# The case stores are Postgres, always (#622). What still needs a stand-in
# locally (#624, ADR 0030) is everything AWS used to provide:
#
# - KMS: the real case key beside a real table, else the development-only
#   key adapters, which refuse to compose outside INSOLVIA_ENV=local.
# - S3: CASE_DOCUMENT_BUCKET on CASE_DOCUMENT_ENDPOINT_URL — the compose
#   stack's MinIO, presigning for the browser's address — or a real bucket
#   with no endpoint; unset, the memory store mints URLs nothing can fetch.
# - SQS: JOB_QUEUE_URL names a real queue (worker_poller.py consumes it);
#   unset, the in-process queue runs jobs in this process.
case_store: CaseStore = PostgresCaseStore(config.database_url)
debtor_store: DebtorStore = PostgresDebtorStore(config.database_url)
case_entity_store: CaseEntityStore = PostgresCaseEntityStore(config.database_url)
document_store: DocumentStore = PostgresDocumentStore(config.database_url)
task_store: TaskStore = PostgresTaskStore(config.database_url)
document_request_store: DocumentRequestStore = PostgresDocumentRequestStore(
    config.database_url
)
job_store: JobStore = PostgresJobStore(config.database_url)
packet_store: PacketStore = PostgresPacketStore(config.database_url)
candidate_store: CandidateStore = PostgresCandidateStore(config.database_url)
event_store: EventStore = PostgresEventStore(config.database_url)
calendar_token_store: CalendarTokenStore = PostgresCalendarTokenStore(
    config.database_url
)

# The sealed and logged data is Postgres too (#623): the access log, tax ids,
# filings, approvals and the credential vault.
access_log: AccessLog = PostgresAccessLog(config.database_url)
tax_id_store: TaxIdStore = PostgresTaxIdStore(config.database_url)
filing_credential_store: FilingCredentialStore = PostgresFilingCredentialStore(
    config.database_url
)
filing_authorization_store: FilingAuthorizationStore = PostgresFilingAuthorizationStore(
    config.database_url
)
filing_approval_store: FilingApprovalStore = PostgresFilingApprovalStore(
    config.database_url
)
filing_store: FilingStore = PostgresFilingStore(config.database_url)

tax_id_cipher: TaxIdCipher
filing_credential_sealer: FilingCredentialSealer
if config.case_table_name:
    # This machine's real case key and vault key (infra/envs/dev); the
    # developer's principal reaches them through the key policy's root
    # delegation, so a laptop seals exactly as staging does.
    tax_id_cipher = KmsTaxIdCipher(case_key_alias(config.case_table_name))
    filing_credential_sealer = KmsCredentialSealer(
        filing_credentials_key_alias(config.case_table_name)
    )
else:
    tax_id_cipher, filing_credential_sealer = development_key_adapters(
        config.environment
    )

document_blobs: DocumentBlobStore
if config.case_document_bucket:
    document_blobs = S3DocumentBlobStore(
        config.case_document_bucket,
        endpoint_url=config.case_document_endpoint_url,
        public_endpoint_url=config.case_document_public_endpoint_url,
    )
else:
    document_blobs = MemoryDocumentBlobStore()

job_queue: JobQueue
if config.job_queue_url:
    job_queue = SqsJobQueue(config.job_queue_url)
else:
    job_queue = InProcessJobQueue(
        store=job_store,
        # The SAME instances the routes use, so a memory twin's writes are
        # visible to the job that reads them.
        workers=compose_workers(
            case_store=case_store,
            debtor_store=debtor_store,
            entity_store=case_entity_store,
            packet_store=packet_store,
            document_store=document_store,
            candidate_store=candidate_store,
            blobs=document_blobs,
            access_log=access_log,
            tax_id_store=tax_id_store,
            tax_id_cipher=tax_id_cipher,
            anthropic_api_key=config.anthropic_api_key,
        ),
    )

# The filing queue's consumer is services/filing, its own service with its
# own fake court; unset, the memory queue records an approval's job and
# sends nothing (no local filing poller yet).
filing_queue: FilingQueue
if config.filing_queue_url:
    filing_queue = SqsFilingQueue(config.filing_queue_url)
else:
    filing_queue = MemoryFilingQueue()

# Auth against the shared dev Cognito pool when AUTH_ISSUER_URL and
# AUTH_CLIENT_ID are set (scripts/dev-aws-seed.sh writes both into
# services/api/.env). Unset, the provider is simply absent and every
# protected route answers 401 — auth fails CLOSED rather than waving
# requests through. This is the ONE entrypoint that tolerates missing auth
# config; api_lambda.py refuses to boot without it.
jwks_provider: JwksProvider | None = None
if config.auth_issuer_url and config.auth_client_id:
    jwks_provider = CognitoJwksProvider(config.auth_issuer_url)

# Its own condition, not the store group's: adding a colleague is the one
# operation that reaches Cognito, and with a real pool named it should reach
# THIS machine's real pool — an invitation that lands in a developer's own
# inbox is the only way to check the flow locally. Unset, the in-memory
# directory mints a subject and sends nothing, which is honest: there is no
# mail here.
user_directory: UserDirectory
if config.auth_user_pool_id:
    user_directory = CognitoUserDirectory(config.auth_user_pool_id)
else:
    user_directory = MemoryUserDirectory()

app = create_app(
    ApiDependencies(
        config=config,
        waitlist_store=waitlist_store,
        mailer=mailer,
        jwks_provider=jwks_provider,
        case_store=case_store,
        access_log=access_log,
        firm_store=firm_store,
        user_directory=user_directory,
        document_store=document_store,
        document_blobs=document_blobs,
        debtor_store=debtor_store,
        tax_id_store=tax_id_store,
        tax_id_cipher=tax_id_cipher,
        case_entity_store=case_entity_store,
        job_store=job_store,
        job_queue=job_queue,
        packet_store=packet_store,
        candidate_store=candidate_store,
        event_store=event_store,
        calendar_token_store=calendar_token_store,
        task_store=task_store,
        document_request_store=document_request_store,
        client_binding_store=client_binding_store,
        filing_credential_store=filing_credential_store,
        filing_credential_sealer=filing_credential_sealer,
        filing_authorization_store=filing_authorization_store,
        filing_approval_store=filing_approval_store,
        filing_queue=filing_queue,
        filing_store=filing_store,
    )
)
