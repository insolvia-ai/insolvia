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

from insolvia_api.adapters.aws.filing_approval_store import DynamoDbFilingApprovalStore
from insolvia_api.adapters.aws.packet_store import DynamoDbPacketStore
from insolvia_core.adapters.aws.access_log import DynamoDbAccessLog
from insolvia_core.adapters.aws.case_entity_store import DynamoDbCaseEntityStore
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.adapters.aws.document_store import DynamoDbDocumentStore
from insolvia_core.adapters.aws.filing_credentials import (
    DynamoDbFilingAuthorizationStore,
    DynamoDbFilingCredentialStore,
    KmsCredentialOpener,
    filing_credentials_key_alias,
    filing_credentials_table_name,
)

from ..adapters.aws.filing_store import DynamoDbFilingStore
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
    table = config.case_table_name
    access_log_table = config.case_access_log_table_name
    bucket = config.case_document_bucket
    if not table or not access_log_table or not bucket:
        raise RuntimeError(
            "CASE_TABLE_NAME, CASE_ACCESS_LOG_TABLE_NAME and CASE_DOCUMENT_BUCKET"
            " must be set for the filing worker"
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

    vault = filing_credentials_table_name(table)
    timeout = config.request_timeout_seconds
    return FilingDeps(
        environment=config.environment,
        filings=DynamoDbFilingStore(table),
        approvals=DynamoDbFilingApprovalStore(table),
        case_store=DynamoDbCaseStore(table),
        debtor_store=DynamoDbDebtorStore(table),
        entity_store=DynamoDbCaseEntityStore(table),
        packet_store=DynamoDbPacketStore(table),
        blobs=S3DocumentBlobStore(bucket),
        document_store=DynamoDbDocumentStore(table),
        credentials=DynamoDbFilingCredentialStore(vault),
        authorizations=DynamoDbFilingAuthorizationStore(vault),
        opener=KmsCredentialOpener(filing_credentials_key_alias(table)),
        access_log=DynamoDbAccessLog(access_log_table),
        kill_switch=kill_switch,
        fence=fence,
        http=lambda: FencedHttpClient(fence, timeout=timeout),
        fake_driver=fake,
        lease_seconds=config.lease_seconds,
    )
