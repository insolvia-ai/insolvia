from __future__ import annotations

from dataclasses import dataclass
from typing import cast

from flask import current_app
from insolvia_core.ports import FirmStore, JwksProvider, UserDirectory

from insolvia_api.core.config import AppConfig
from insolvia_api.core.ports import (
    AccessLog,
    CaseEntityStore,
    CaseStore,
    DebtorStore,
    DocumentBlobStore,
    DocumentStore,
    JobQueue,
    JobStore,
    Mailer,
    PacketStore,
    WaitlistStore,
)


@dataclass(frozen=True)
class ApiDependencies:
    """Everything the API layer needs, composed by an entrypoint.

    Each core port gets a field: the Lambda entrypoint supplies the AWS
    implementation, and the development server and tests supply the
    in-memory one — mirroring mailer's ApiDependencies.
    """

    config: AppConfig
    waitlist_store: WaitlistStore
    mailer: Mailer
    # Optional for the same reason jwks_provider is: the existing public-route
    # tests build an ApiDependencies without them. The case routes are
    # unreachable without both — api/routes/cases.py answers 503 rather than
    # pretending, and the Lambda entrypoint refuses to boot without them.
    case_store: CaseStore | None = None
    access_log: AccessLog | None = None
    # The tenancy layer. Optional for the same reason as the pair above — the
    # existing public-route tests build an ApiDependencies without one — but it
    # is the field with the shortest fuse: once accessor resolution lands,
    # absent means no authenticated request can establish which firm the caller
    # belongs to, and every case route answers 403 rather than degrading.
    firm_store: FirmStore | None = None
    # The identity provider, needed by exactly one endpoint: adding a colleague
    # has to mint their Cognito account first. Separate from firm_store so the
    # READ routes work without it — a deployment missing the pool id should
    # fail on that one endpoint rather than take the staff list down too.
    user_directory: UserDirectory | None = None
    # Two ports rather than one, because they are two pieces of infrastructure
    # with two IAM grants and two failure modes: the row lives in the case
    # table, the bytes live in the bucket. Folding them together would put a
    # DynamoDB client and an S3 client in one adapter and force the in-memory
    # store to fake presigning as well as storage. Optional for the same reason
    # as the pair above — api/routes/documents.py raises rather than degrading,
    # and the Lambda entrypoint refuses to boot without the bucket.
    document_store: DocumentStore | None = None
    document_blobs: DocumentBlobStore | None = None
    # Shares the case table rather than taking one of its own — a debtor
    # lives in its case's partition (SK=DEBTOR#<role>), so there is no
    # second table, no new environment variable, and nothing to provision.
    debtor_store: DebtorStore | None = None
    # The generic case collections (issue #249) — same table, same partition,
    # same "nothing to provision" argument as the debtor store above.
    case_entity_store: CaseEntityStore | None = None
    # The pipeline pair (ADR 0018). The STORE shares the case table — same
    # "nothing to provision" argument as the two above. The QUEUE is the one
    # dependency here where None is a legitimate DEPLOYED state, not only a
    # test convenience: the api image can roll out ahead of the infra that
    # creates the queue and publishes /insolvia/<env>/api/job-queue-url, and
    # in that window the accept endpoint answers 503 (api/routes/jobs.py)
    # while status reads keep working. It must never silently fall back to
    # the in-memory queue in a Lambda — that accepts work nothing will ever
    # run.
    job_store: JobStore | None = None
    job_queue: JobQueue | None = None
    # Assembled packets (issue #96): the records share the case table (the
    # "nothing to provision" argument again); the bytes share the document
    # bucket, reached through document_blobs above. The API only reads this
    # store — the pipeline worker is the writer.
    packet_store: PacketStore | None = None
    # None means "this deployment cannot verify tokens" (issue #79). It is a
    # fail-CLOSED default, not a permissive one: api/auth.py answers 401 on
    # every protected route when it is absent, and the Lambda entrypoint
    # refuses to boot without one. It stays optional only so the existing
    # public-route tests can build an ApiDependencies without one.
    jwks_provider: JwksProvider | None = None


def dependencies() -> ApiDependencies:
    return cast("ApiDependencies", current_app.extensions["insolvia_api_dependencies"])
