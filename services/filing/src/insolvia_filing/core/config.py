"""The filing worker's configuration, read once at composition time.

Nothing else in the package touches `os.environ`. Two things are
deliberately NOT configuration, and that absence is the design:

- **Which court hosts the worker may reach.** That is `core/fence.py`'s
  per-environment allowlist, in code: widening it is a reviewed diff (ADR
  0024 PR 10 adds a court only after its driver opened the fixture case on
  that court's training database), never an environment variable somebody
  can set on a Lambda.
- **Whether submissions are on.** That is the kill switch, read at run time
  from its own SSM parameter (`FILING_KILL_SWITCH_PARAMETER`) — so the
  maintainer flips it without a deploy, and the worker re-reads it before
  every final submit. Locally there is no parameter; `FILING_SUBMISSIONS_
  ENABLED` stands in for it, and is refused in staging and production.
"""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass
from urllib.parse import urlsplit

from insolvia_core.errors import ValidationError

SERVICE_NAME = "insolvia-filing"

ENVIRONMENTS = ("local", "staging", "production")

# How long one attempt may hold a claimed filing before a redelivery may call
# it interrupted (`core/worker.py`). Equal to the Lambda's timeout
# (infra/modules/filing_worker): an attempt that is still running cannot be
# older than this, so a redelivery that finds an older claim is looking at a
# dead attempt, never a live one.
DEFAULT_LEASE_SECONDS = 600

# How long one request to a court may take before the run stops. Generous —
# CM/ECF is slow — and an order of magnitude inside the lease.
DEFAULT_REQUEST_TIMEOUT_SECONDS = 60.0


@dataclass(frozen=True)
class FilingConfig:
    """Parsed configuration. `load_config` is the only real constructor; the
    defaults describe a bare local run against in-memory stores."""

    environment: str
    case_table_name: str | None = None
    case_access_log_table_name: str | None = None
    case_document_bucket: str | None = None
    filing_queue_url: str | None = None
    # The SSM parameter holding the kill switch ("true" = submissions on).
    # Required in staging and production; absent locally.
    kill_switch_parameter: str | None = None
    # Local only: the kill switch's stand-in. Off unless said otherwise.
    local_submissions_enabled: bool = False
    # Local only: where the fake CM/ECF server listens (dev-up.sh). Refused
    # anywhere else — the fake is never deployed, and a deployed worker
    # pointed at one is a misconfiguration to fail on, not to honour.
    fake_cmecf_url: str | None = None
    # Local only: the filing worker's role, which the local poller assumes so
    # a laptop run uses the role's real, narrow grants (infra/envs/dev trusts
    # the developer to assume it; nowhere else does).
    worker_role_arn: str | None = None
    lease_seconds: int = DEFAULT_LEASE_SECONDS
    request_timeout_seconds: float = DEFAULT_REQUEST_TIMEOUT_SECONDS


def _flag(value: str | None) -> bool:
    return (value or "").strip().lower() == "true"


def load_config(environ: Mapping[str, str] | None = None) -> FilingConfig:
    source = os.environ if environ is None else environ
    environment = source.get("INSOLVIA_ENV", "local")
    if environment not in ENVIRONMENTS:
        raise ValidationError(
            f"INSOLVIA_ENV must be one of {', '.join(ENVIRONMENTS)}, "
            f"got {environment!r}"
        )
    deployed = environment != "local"

    fake = source.get("FAKE_CMECF_URL") or None
    if fake is not None:
        if deployed:
            raise ValidationError(
                "FAKE_CMECF_URL is a local-only setting: the fake CM/ECF is "
                "never deployed, and a deployed worker must not be pointed at one"
            )
        parts = urlsplit(fake)
        if parts.scheme != "http" or parts.hostname not in ("127.0.0.1", "::1"):
            raise ValidationError(
                "FAKE_CMECF_URL must be an http:// loopback address (127.0.0.1)"
            )

    local_enabled = source.get("FILING_SUBMISSIONS_ENABLED")
    if deployed and local_enabled is not None:
        raise ValidationError(
            "FILING_SUBMISSIONS_ENABLED is the local stand-in for the kill "
            "switch; deployed environments read the SSM parameter only"
        )
    kill_switch = source.get("FILING_KILL_SWITCH_PARAMETER") or None
    if deployed and kill_switch is None:
        raise ValidationError(
            "FILING_KILL_SWITCH_PARAMETER is required outside local: a "
            "deployed worker with no kill switch to read must not exist"
        )

    role = source.get("FILING_WORKER_ROLE_ARN") or None
    if deployed and role is not None:
        raise ValidationError(
            "FILING_WORKER_ROLE_ARN is for the local poller only; the Lambda "
            "already runs as the worker's role"
        )

    return FilingConfig(
        environment=environment,
        case_table_name=source.get("CASE_TABLE_NAME") or None,
        case_access_log_table_name=source.get("CASE_ACCESS_LOG_TABLE_NAME") or None,
        case_document_bucket=source.get("CASE_DOCUMENT_BUCKET") or None,
        filing_queue_url=source.get("FILING_QUEUE_URL") or None,
        kill_switch_parameter=kill_switch,
        local_submissions_enabled=_flag(local_enabled),
        fake_cmecf_url=fake,
        worker_role_arn=role,
    )
