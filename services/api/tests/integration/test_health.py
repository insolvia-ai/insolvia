"""The cheapest possible proof that the suite is aimed where it thinks it is."""

from __future__ import annotations

from tests.integration.conftest import EXPECTED_ENVIRONMENT, Api


def test_health_names_the_environment_this_run_targets(anonymous: Api, target: str):
    """A green run against the wrong environment is worse than a red one: it
    blesses a deploy nothing tested. The environment name is the one field
    that cannot be right by accident."""
    body = anonymous.get("/health")

    assert body["status"] == "ok"
    assert body["service"] == "insolvia-api"
    assert body["environment"] == EXPECTED_ENVIRONMENT[target]
