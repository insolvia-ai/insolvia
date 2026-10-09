"""The kill switch failing closed, with the transport replaced. (The filing
store's conditions moved with the store to insolvia_core in ADR 0024 PR 8 —
packages/insolvia_core/tests/unit/test_filings.py.)"""

from __future__ import annotations

import pytest
from insolvia_filing.adapters.aws.kill_switch import SsmKillSwitch


class Ssm:
    def __init__(self, value=None, fail=False):
        self.value = value
        self.fail = fail
        self.reads = 0

    def get_parameter(self, Name):  # noqa: N803 — boto3's name
        self.reads += 1
        if self.fail:
            raise RuntimeError("unreachable")
        return {"Parameter": {"Value": self.value}}


@pytest.mark.parametrize(
    ("ssm", "enabled"),
    [
        (Ssm("true"), True),
        (Ssm("TRUE "), True),
        (Ssm("false"), False),
        (Ssm(""), False),
        (Ssm("yes"), False),
        (Ssm(fail=True), False),
    ],
)
def test_the_kill_switch_is_on_only_for_true_and_fails_closed(ssm, enabled):
    assert SsmKillSwitch("/p", client=ssm).submissions_enabled() is enabled


def test_the_kill_switch_is_read_fresh_every_time():
    ssm = Ssm("true")
    switch = SsmKillSwitch("/p", client=ssm)
    switch.submissions_enabled()
    ssm.value = "false"
    assert switch.submissions_enabled() is False
    assert ssm.reads == 2
