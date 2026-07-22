import pytest

from insolvia_api.core.config import AppConfig, load_config
from insolvia_api.core.errors import ValidationError


def test_environment_defaults_to_local():
    assert load_config({}) == AppConfig(environment="local")


@pytest.mark.parametrize("environment", ["local", "staging", "production"])
def test_known_environments_are_accepted(environment):
    assert load_config({"INSOLVIA_ENV": environment}).environment == environment


def test_unknown_environment_is_rejected():
    with pytest.raises(ValidationError, match="INSOLVIA_ENV"):
        load_config({"INSOLVIA_ENV": "prod"})


def test_defaults_to_process_environment(monkeypatch):
    monkeypatch.setenv("INSOLVIA_ENV", "production")
    assert load_config().environment == "production"
