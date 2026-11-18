"""The one composition of the worker's dependencies against AWS — shared by
the Lambda entrypoint and the local poller, so a laptop runs the exact
worker the cloud runs.

What differs by environment is decided HERE, from `FilingConfig`, and
nowhere else:

- the kill switch: the SSM parameter when deployed; the local stand-in
  (`FILING_SUBMISSIONS_ENABLED`) on a laptop;
- the fake driver: composed only when `INSOLVIA_ENV=local` AND
  `FAKE_CMECF_URL` is set (load_config already refuses the URL anywhere
  else, and `resolve_driver` refuses a fake outside local besides);
- the fence: `fence_for(environment)` — the code's allowlist, no input.
"""

from __future__ import annotations

from insolvia_api.adapters.postgres.filing_approval_store import (
    PostgresFilingApprovalStore,
)
from insolvia_api.adapters.postgres.packet_store import PostgresPacketStore
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.adapters.aws.filing_credentials import (
    KmsCredentialOpener,
    filing_credentials_key_alias,
)
from insolvia_core.adapters.aws.tax_id_cipher import KmsTaxIdCipher, case_key_alias
from insolvia_core.adapters.postgres.access_log import PostgresAccessLog
from insolvia_core.adapters.postgres.case_entity_store import PostgresCaseEntityStore
from insolvia_core.adapters.postgres.case_store import PostgresCaseStore
from insolvia_core.adapters.postgres.debtor_store import PostgresDebtorStore
from insolvia_core.adapters.postgres.document_store import PostgresDocumentStore
from insolvia_core.adapters.postgres.filing_credentials import (
    PostgresFilingAuthorizationStore,
    PostgresFilingCredentialStore,
)
from insolvia_core.adapters.postgres.filing_store import PostgresFilingStore
from insolvia_core.adapters.postgres.tax_id_store import PostgresTaxIdStore

from ..adapters.aws.kill_switch import SsmKillSwitch
from ..adapters.http.fenced_client import FencedHttpClient
from ..adapters.memory.kill_switch import StaticKillSwitch
from ..core.config import FilingConfig
from ..core.drivers.base import CourtDriver, DriverFactory
from ..core.drivers.fake import FakeCmEcfDriver
from ..core.fence import fence_for
from ..core.ports import KillSwitch
from ..core.worker import FilingDeps


def compose(config: FilingConfig) -> FilingDeps:
    database_url = config.database_url
    table = config.case_table_name
    bucket = config.case_document_bucket
    if database_url is None:
        raise RuntimeError("DATABASE_URL must be set for the filing worker")
    # The case table's name names the case and vault keys (case_key_alias).
    if not table or not bucket:
        raise RuntimeError(
            "CASE_TABLE_NAME and CASE_DOCUMENT_BUCKET must be set for the filing worker"
        )
    fence = fence_for(config.environment)
    kill_switch: KillSwitch
    if config.environment == "local":
        kill_switch = StaticKillSwitch(config.local_submissions_enabled)
    else:
        assert config.kill_switch_parameter is not None  # load_config refuses None
        kill_switch = SsmKillSwitch(config.kill_switch_parameter)

    fake: DriverFactory | None = None
    if config.environment == "local" and config.fake_cmecf_url is not None:
        url = config.fake_cmecf_url

        def fake_driver() -> CourtDriver:
            return FakeCmEcfDriver(url)

        fake = fake_driver

    timeout = config.request_timeout_seconds
    return FilingDeps(
        environment=config.environment,
        filings=PostgresFilingStore(database_url),
        approvals=PostgresFilingApprovalStore(database_url),
        case_store=PostgresCaseStore(database_url),
        debtor_store=PostgresDebtorStore(database_url),
        entity_store=PostgresCaseEntityStore(database_url),
        packet_store=PostgresPacketStore(database_url),
        blobs=S3DocumentBlobStore(bucket),
        document_store=PostgresDocumentStore(database_url),
        # The one composition that reads the envelope — the filing role is
        # the only one granted it (migration 0003).
        credentials=PostgresFilingCredentialStore(database_url, opens=True),
        authorizations=PostgresFilingAuthorizationStore(database_url),
        opener=KmsCredentialOpener(filing_credentials_key_alias(table)),
        access_log=PostgresAccessLog(database_url),
        # The Case Upload file's tax-id read (ADR 0024 PR 9): the case key by
        # its alias, Decrypt only, under the tax-id encryption context.
        tax_id_store=PostgresTaxIdStore(database_url),
        tax_id_cipher=KmsTaxIdCipher(case_key_alias(table)),
        kill_switch=kill_switch,
        fence=fence,
        http=lambda: FencedHttpClient(fence, timeout=timeout),
        fake_driver=fake,
        lease_seconds=config.lease_seconds,
    )
