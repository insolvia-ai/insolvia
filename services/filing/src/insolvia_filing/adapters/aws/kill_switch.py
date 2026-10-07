"""The environment kill switch, as an SSM parameter (ADR 0024: "one flag the
maintainer can flip — refuses every submission in that environment without a
deploy").

    /insolvia/<env>/filing/submissions-enabled     "true" | anything else

Read FRESH on every call — at the start of a run and again immediately before
the final submit — never cached: a flip must stop the next submission, not
the next cold start. FAILS CLOSED: a missing parameter, any value but
"true", or an error reading it all mean OFF. Terraform creates the parameter
"false" and ignores later changes to its value (infra/modules/filing_worker),
so a flip in the console is not reverted by the next apply.
"""

from __future__ import annotations

import logging
from typing import Any

import boto3

logger = logging.getLogger(__name__)


class SsmKillSwitch:
    def __init__(self, parameter_name: str, *, client: Any = None) -> None:
        self._name = parameter_name
        self._client = client or boto3.client("ssm")

    def submissions_enabled(self) -> bool:
        try:
            response = self._client.get_parameter(Name=self._name)
        except Exception:
            logger.warning(
                "kill switch unreadable; submissions are off",
                extra={"parameter": self._name},
            )
            return False
        value = str(response.get("Parameter", {}).get("Value", ""))
        return value.strip().lower() == "true"
