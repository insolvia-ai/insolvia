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


def test_waitlist_table_name_defaults_to_none():
    config = load_config({})
    assert config.waitlist_table_name is None
    assert config.dynamodb_endpoint_url is None


def test_waitlist_table_name_is_read():
    config = load_config({"WAITLIST_TABLE_NAME": "insolvia-waitlist-staging"})
    assert config.waitlist_table_name == "insolvia-waitlist-staging"


def test_dynamodb_endpoint_override_is_local_only():
    config = load_config({"DYNAMODB_ENDPOINT_URL": "http://localhost:8000"})
    assert config.dynamodb_endpoint_url == "http://localhost:8000"

    for environment in ("staging", "production"):
        with pytest.raises(ValidationError, match="DYNAMODB_ENDPOINT_URL"):
            load_config(
                {
                    "INSOLVIA_ENV": environment,
                    "DYNAMODB_ENDPOINT_URL": "http://localhost:8000",
                }
            )


def test_cors_allowlist_per_environment():
    # Exact origins only; www.insolvia.ai deliberately absent everywhere —
    # the marketing site's waitlist call is server-to-server (no Origin).
    assert load_config({"INSOLVIA_ENV": "production"}).cors_allowed_origins == (
        "https://app.insolvia.ai",
    )
    assert load_config({"INSOLVIA_ENV": "staging"}).cors_allowed_origins == (
        "https://staging-app.insolvia.ai",
    )
    assert load_config({}).cors_allowed_origins == ()


def test_localhost_origins_allowed_everywhere_but_production():
    assert load_config({}).cors_allow_localhost is True
    assert load_config({"INSOLVIA_ENV": "staging"}).cors_allow_localhost is True
    assert load_config({"INSOLVIA_ENV": "production"}).cors_allow_localhost is False
