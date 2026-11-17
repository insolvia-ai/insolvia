"""What a deployed worker refuses to be configured as."""

from __future__ import annotations

import pytest
from insolvia_core.errors import ValidationError
from insolvia_filing.core.config import load_config

DEPLOYED = {
    "FILING_KILL_SWITCH_PARAMETER": "/insolvia/staging/filing/submissions-enabled"
}


@pytest.mark.parametrize("environment", ["staging", "production"])
def test_a_deployed_worker_cannot_be_pointed_at_a_fake(environment):
    with pytest.raises(ValidationError):
        load_config(
            {
                "INSOLVIA_ENV": environment,
                "FAKE_CMECF_URL": "http://127.0.0.1:8790",
                **DEPLOYED,
            }
        )


@pytest.mark.parametrize("environment", ["staging", "production"])
def test_a_deployed_worker_needs_its_kill_switch_parameter(environment):
    with pytest.raises(ValidationError):
        load_config({"INSOLVIA_ENV": environment})


@pytest.mark.parametrize("environment", ["staging", "production"])
def test_a_deployed_worker_takes_no_local_kill_switch(environment):
    with pytest.raises(ValidationError):
        load_config(
            {
                "INSOLVIA_ENV": environment,
                "FILING_SUBMISSIONS_ENABLED": "true",
                **DEPLOYED,
            }
        )


@pytest.mark.parametrize(
    "url",
    ["https://127.0.0.1:8790", "http://localhost:8790", "http://10.0.0.5:8790"],
)
def test_the_fake_must_be_on_plain_http_loopback(url):
    with pytest.raises(ValidationError):
        load_config({"FAKE_CMECF_URL": url})


def test_locally_submissions_are_off_unless_said_otherwise():
    assert load_config({}).local_submissions_enabled is False
    assert load_config({"FILING_SUBMISSIONS_ENABLED": "true"}).local_submissions_enabled


def test_an_unknown_environment_is_refused():
    with pytest.raises(ValidationError):
        load_config({"INSOLVIA_ENV": "prod"})
