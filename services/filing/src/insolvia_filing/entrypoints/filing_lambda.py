"""The filing worker Lambda — the SQS event source mapping's target, batch
size 1 (infra/modules/filing_worker).

Composed once per cold start. Each record runs through `core/worker.
run_filing`; an exception escapes only from before a filing record exists
(the store unreachable at claim time), which SQS may safely retry because
every later attempt resumes through the record's own state and never
submits twice."""

from __future__ import annotations

import logging
from typing import Any

from ..core.config import load_config
from ..core.logging import configure_logging
from ..core.worker import FilingDeps, run_filing
from .compose import compose

logger = logging.getLogger(__name__)

_deps: FilingDeps | None = None


def _dependencies() -> FilingDeps:
    global _deps
    if _deps is None:
        configure_logging()
        _deps = compose(load_config())
    return _deps


def handler(event: dict[str, Any], context: object) -> dict[str, Any]:
    deps = _dependencies()
    outcomes = []
    for record in event.get("Records", []):
        result = run_filing(str(record.get("body", "")), deps)
        logger.info(
            "filing job done",
            extra={
                "outcome": result.outcome,
                "filing_id": result.filing_id,
                "reason": result.reason,
            },
        )
        outcomes.append(result.outcome)
    return {"outcomes": outcomes}
