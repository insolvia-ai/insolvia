"""The AWS adapters' calls, with the transport replaced: the conditions the
filing store sends, and the kill switch failing closed."""

from __future__ import annotations

import pytest
from botocore.exceptions import ClientError
from insolvia_filing.adapters.aws.filing_store import DynamoDbFilingStore
from insolvia_filing.adapters.aws.kill_switch import SsmKillSwitch


class Table:
    def __init__(self, fail: bool = False) -> None:
        self.calls: list[dict] = []
        self.fail = fail

    def put_item(self, **kwargs):
        self.calls.append(kwargs)
        if self.fail:
            raise ClientError(
                {"Error": {"Code": "ConditionalCheckFailedException"}}, "PutItem"
            )

    def get_item(self, **kwargs):
        self.calls.append(kwargs)
        return {}


class Resource:
    def __init__(self, table: Table) -> None:
        self.table = table

    def Table(self, name):  # noqa: N802 — boto3's name
        return self.table


def test_a_claim_is_conditional_on_no_record(make_filing):
    table = Table()
    DynamoDbFilingStore("t", resource=Resource(table)).claim(make_filing())
    assert table.calls[0]["ConditionExpression"] == "attribute_not_exists(PK)"


def test_a_transition_is_conditional_on_the_state_and_the_attempt(make_filing):
    table = Table()
    DynamoDbFilingStore("t", resource=Resource(table)).transition(
        make_filing(state="signed_in"), expected_state="claimed"
    )
    call = table.calls[0]
    assert call["ConditionExpression"] == "#state = :expected AND attemptId = :attempt"
    assert call["ExpressionAttributeValues"] == {
        ":expected": "claimed",
        ":attempt": "attempt-1",
    }


def test_a_lost_condition_is_false_not_an_error(make_filing):
    store = DynamoDbFilingStore("t", resource=Resource(Table(fail=True)))
    assert store.claim(make_filing()) is False
    assert store.transition(make_filing(), expected_state="claimed") is False


def test_reads_are_strongly_consistent():
    table = Table()
    DynamoDbFilingStore("t", resource=Resource(table)).get("c", "f")
    assert table.calls[0]["ConsistentRead"] is True


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
