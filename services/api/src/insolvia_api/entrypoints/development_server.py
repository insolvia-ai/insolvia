from __future__ import annotations

from insolvia_core.adapters.aws.access_log import DynamoDbAccessLog
from insolvia_core.adapters.aws.candidate_store import DynamoDbCandidateStore
from insolvia_core.adapters.aws.case_entity_store import DynamoDbCaseEntityStore
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.client_binding_store import DynamoDbClientBindingStore
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.adapters.aws.document_request_store import (
    DynamoDbDocumentRequestStore,
)
from insolvia_core.adapters.aws.document_store import DynamoDbDocumentStore
from insolvia_core.adapters.aws.filing_credentials import (
    DynamoDbFilingAuthorizationStore,
    DynamoDbFilingCredentialStore,
    KmsCredentialSealer,
    filing_credentials_key_alias,
    filing_credentials_table_name,
)
from insolvia_core.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_core.adapters.aws.jwks_provider import CognitoJwksProvider
from insolvia_core.adapters.aws.task_store import DynamoDbTaskStore
from insolvia_core.adapters.aws.tax_id_cipher import KmsTaxIdCipher, case_key_alias
from insolvia_core.adapters.aws.tax_id_store import DynamoDbTaxIdStore
from insolvia_core.adapters.aws.user_directory import CognitoUserDirectory
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.candidate_store import MemoryCandidateStore
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.client_binding_store import (
    MemoryClientBindingStore,
)
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.document_blobs import MemoryDocumentBlobStore
from insolvia_core.adapters.memory.document_request_store import (
    MemoryDocumentRequestStore,
)
from insolvia_core.adapters.memory.document_store import MemoryDocumentStore
from insolvia_core.adapters.memory.filing_credentials import (
    LocalCredentialSealer,
    MemoryFilingAuthorizationStore,
    MemoryFilingCredentialStore,
)
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.task_store import MemoryTaskStore
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.adapters.memory.tax_id_store import MemoryTaxIdStore
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_core.ports import (
    AccessLog,
    CandidateStore,
    CaseEntityStore,
    CaseStore,
    ClientBindingStore,
    DebtorStore,
    DocumentBlobStore,
    DocumentRequestStore,
    DocumentStore,
    FilingAuthorizationStore,
    FilingCredentialSealer,
    FilingCredentialStore,
    FirmStore,
    JwksProvider,
    TaskStore,
    TaxIdCipher,
    TaxIdStore,
    UserDirectory,
)

from insolvia_api.adapters.aws.event_store import (
    DynamoDbCalendarTokenStore,
    DynamoDbEventStore,
)
from insolvia_api.adapters.aws.filing_approval_store import (
    DynamoDbFilingApprovalStore,
)
from insolvia_api.adapters.aws.filing_queue import SqsFilingQueue
from insolvia_api.adapters.aws.job_queue import SqsJobQueue
from insolvia_api.adapters.aws.job_store import DynamoDbJobStore
from insolvia_api.adapters.aws.mailer_client import SigV4MailerClient
from insolvia_api.adapters.aws.packet_store import DynamoDbPacketStore
from insolvia_api.adapters.aws.waitlist_store import DynamoDbWaitlistStore
from insolvia_api.adapters.memory.event_store import (
    MemoryCalendarTokenStore,
    MemoryEventStore,
)
from insolvia_api.adapters.memory.filing_approval_store import (
    MemoryFilingApprovalStore,
)
from insolvia_api.adapters.memory.filing_queue import MemoryFilingQueue
from insolvia_api.adapters.memory.job_queue import MemoryJobQueue
from insolvia_api.adapters.memory.job_store import MemoryJobStore
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.packet_store import MemoryPacketStore
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
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
    WaitlistStore,
)

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

# The plain development server never sends real mail. Unset MAILER_API_URL
# records sends in memory — mirroring the memory waitlist store. Set, it
# posts to THAT mailer: pointed at services/mailer's own local stack
# (`./services/mailer/scripts/dev-up.sh`, then MAILER_API_URL=
# http://127.0.0.1:8026 — or http://host.docker.internal:8026 from the API's
# compose container), every message lands in Mailpit at :8025 and nothing
# leaves the machine, which is how the client-portal invitation's context
# mail (ADR 0023) is checked locally. The local mailer ignores the SigV4
# signature API Gateway would verify, so the client needs only whatever
# credentials the developer's session already exports to sign with.
mailer: Mailer
if config.mailer_api_url:
    mailer = SigV4MailerClient(config.mailer_api_url)
else:
    mailer = InMemoryMailerClient()

# Auth against this machine's own Cognito pool when AUTH_ISSUER_URL and
# AUTH_CLIENT_ID are set (infra/envs/dev publishes both; export them into
# services/api/.env to use them). Unset, the provider is simply absent and
# every protected route answers 401 — the bare dev server keeps working for
# the public routes, and auth fails CLOSED rather than waving requests
# through. This is the ONE entrypoint that tolerates missing auth config;
# api_lambda.py refuses to boot without it.
# Same shape as the waitlist store above: with this machine's real stores named
# (scripts/dev-aws-setup.sh writes all of them into services/api/.env) the AWS
# adapters run against them, and the bare dev server falls back to in-memory.
# The GROUP moves together — an in-memory store with a real access log, or a
# real document row pointing at a bucket that only exists in this process,
# would be a confusing half-state to debug. That is also why the document
# bucket is in this condition rather than in one of its own: dev-aws-setup.sh
# provisions the table and the bucket in the same apply, so "some but not all"
# means a stale .env, and falling back wholesale is the state a developer can
# actually reason about.
case_store: CaseStore
access_log: AccessLog
firm_store: FirmStore
if (
    config.case_table_name
    and config.case_access_log_table_name
    and config.firm_table_name
):
    case_store = DynamoDbCaseStore(config.case_table_name)
    access_log = DynamoDbAccessLog(config.case_access_log_table_name)
    firm_store = DynamoDbFirmStore(config.firm_table_name)
else:
    case_store = MemoryCaseStore()
    access_log = MemoryAccessLog()
    # Empty at every start, so the bare dev server has no firms and no users in
    # it. That is the honest shape rather than a seeded one: a signed-in
    # developer with no firm is exactly the "authenticated but not provisioned"
    # case, and inventing a firm here would hide it until staging.
    firm_store = MemoryFirmStore()
document_store: DocumentStore
document_blobs: DocumentBlobStore
if (
    config.case_table_name
    and config.case_access_log_table_name
    and config.case_document_bucket
):
    case_store = DynamoDbCaseStore(config.case_table_name)
    access_log = DynamoDbAccessLog(config.case_access_log_table_name)
    document_store = DynamoDbDocumentStore(config.case_table_name)
    document_blobs = S3DocumentBlobStore(config.case_document_bucket)
else:
    case_store = MemoryCaseStore()
    access_log = MemoryAccessLog()
    document_store = MemoryDocumentStore()
    # Mints URLs nothing can fetch, which is the honest local shape: there is
    # no S3 emulator here. Run scripts/dev-aws-setup.sh to get a real bucket.
    document_blobs = MemoryDocumentBlobStore()
debtor_store: DebtorStore
case_entity_store: CaseEntityStore
tax_id_store: TaxIdStore
tax_id_cipher: TaxIdCipher
task_store: TaskStore
document_request_store: DocumentRequestStore
filing_credential_store: FilingCredentialStore
filing_credential_sealer: FilingCredentialSealer
filing_authorization_store: FilingAuthorizationStore
if config.case_table_name and config.case_access_log_table_name:
    case_store = DynamoDbCaseStore(config.case_table_name)
    access_log = DynamoDbAccessLog(config.case_access_log_table_name)
    debtor_store = DynamoDbDebtorStore(config.case_table_name)
    # The same table again: the generic collections (issue #249) are child
    # items of their case's partition, so the dev table already holds them.
    case_entity_store = DynamoDbCaseEntityStore(config.case_table_name)
    # And the sealed tax ids (issue 13.12 / #382), under THIS MACHINE's real
    # case key — the developer's own IAM user reaches it through the key
    # policy's root delegation (infra/envs/dev), so a laptop seals and opens
    # exactly as staging does. The alias is derived from the table name.
    tax_id_store = DynamoDbTaxIdStore(config.case_table_name)
    tax_id_cipher = KmsTaxIdCipher(case_key_alias(config.case_table_name))
    # Likewise: case tasks (issue #356 / 14.4) are child items too.
    task_store = DynamoDbTaskStore(config.case_table_name)
    # And a case's document requests (ADR 0023 PR 5 / #364).
    document_request_store = DynamoDbDocumentRequestStore(config.case_table_name)
    # The filing-credential vault (ADR 0024), on THIS MACHINE's real
    # vault table and key (infra/envs/dev). The developer's principal
    # seals through the key's root delegation and is refused Decrypt by
    # its key policy, exactly as the deployed API's role is.
    filing_credential_store = DynamoDbFilingCredentialStore(
        filing_credentials_table_name(config.case_table_name)
    )
    filing_credential_sealer = KmsCredentialSealer(
        filing_credentials_key_alias(config.case_table_name)
    )
    filing_authorization_store = DynamoDbFilingAuthorizationStore(
        filing_credentials_table_name(config.case_table_name)
    )
else:
    debtor_store = MemoryDebtorStore()
    # ONE debtor store for both: opening a case writes its debtors through
    # the case store (ADR 0022), and the debtor routes must see them — the
    # shared-table property the DynamoDB pair has by construction.
    case_store = MemoryCaseStore(debtor_store=debtor_store)
    access_log = MemoryAccessLog()
    case_entity_store = MemoryCaseEntityStore()
    tax_id_store = MemoryTaxIdStore()
    # The deterministic local key — never composed beside a real table.
    tax_id_cipher = LocalTaxIdCipher()
    task_store = MemoryTaskStore()
    document_request_store = MemoryDocumentRequestStore()
    filing_credential_store = MemoryFilingCredentialStore()
    filing_credential_sealer = LocalCredentialSealer()
    filing_authorization_store = MemoryFilingAuthorizationStore()

# The pipeline pair (ADR 0018). The store rides the case-table condition
# above — a job is a child item of the case partition, so whichever table the
# case store got, the job store shares. The QUEUE follows the user-directory
# shape: with JOB_QUEUE_URL set (dev-aws-setup.sh writes this machine's real
# dev queue into services/api/.env) an accepted job lands on real SQS, and
# `python -m insolvia_api.entrypoints.worker_poller` in another terminal runs
# it — the full accept → deliver → run → status loop on a laptop. Unset, the
# memory queue records the enqueue and nothing runs, which is honest: there
# is no worker in this process.
job_store: JobStore
if config.case_table_name and config.case_access_log_table_name:
    job_store = DynamoDbJobStore(config.case_table_name)
else:
    job_store = MemoryJobStore()
job_queue: JobQueue
if config.job_queue_url:
    job_queue = SqsJobQueue(config.job_queue_url)
else:
    job_queue = MemoryJobQueue()

# The per-filing approval (ADR 0024 PR 6): the records ride the case-table
# group like the job store; the queue follows the job queue's shape — with
# FILING_QUEUE_URL set (dev-aws-setup.sh writes this machine's real filing
# queue) an approval's job lands on real SQS and waits there, since nothing
# consumes it until services/filing (PR 7); unset, the memory queue records
# it and sends nothing.
filing_approval_store: FilingApprovalStore
if config.case_table_name and config.case_access_log_table_name:
    filing_approval_store = DynamoDbFilingApprovalStore(config.case_table_name)
else:
    filing_approval_store = MemoryFilingApprovalStore()
filing_queue: FilingQueue
if config.filing_queue_url:
    filing_queue = SqsFilingQueue(config.filing_queue_url)
else:
    filing_queue = MemoryFilingQueue()

# Assembled packets (issue #96): the record rides the case table like the job
# store; the API side only reads it. The memory fallback shares the memory
# case store because its `create` is a transaction over both — and in that
# branch the case store above IS the memory one (the group moves together).
packet_store: PacketStore
if config.case_table_name and config.case_access_log_table_name:
    packet_store = DynamoDbPacketStore(config.case_table_name)
else:
    if not isinstance(case_store, MemoryCaseStore):  # pragma: no cover - guard
        raise RuntimeError("memory packet store needs the memory case store")
    packet_store = MemoryPacketStore(case_store)

# The review queue (8.9): candidate rows ride the case table like the job
# store — this machine's real dev table, or the in-memory group with the
# rest of the memory stores.
candidate_store: CandidateStore
if config.case_table_name and config.case_access_log_table_name:
    candidate_store = DynamoDbCandidateStore(config.case_table_name)
else:
    candidate_store = MemoryCandidateStore()

# Events, deadlines and the feed token (issue 14.6 / #358): the case table
# again, so a developer's calendar rides this machine's real dev table and
# the bare server keeps everything in memory.
event_store: EventStore
calendar_token_store: CalendarTokenStore
if config.case_table_name and config.case_access_log_table_name:
    event_store = DynamoDbEventStore(config.case_table_name)
    calendar_token_store = DynamoDbCalendarTokenStore(config.case_table_name)
else:
    event_store = MemoryEventStore()
    calendar_token_store = MemoryCalendarTokenStore()

# The client portal's bindings (ADR 0023) span the firm table AND the case
# table, so they ride this machine's real pair when BOTH are named and the
# in-memory store otherwise — the same "the group moves together" rule.
client_binding_store: ClientBindingStore
if config.firm_table_name and config.case_table_name:
    client_binding_store = DynamoDbClientBindingStore(
        config.firm_table_name, config.case_table_name
    )
else:
    client_binding_store = MemoryClientBindingStore()

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
    )
)
