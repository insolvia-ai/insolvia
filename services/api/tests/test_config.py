import pytest
from insolvia_api.core.config import AppConfig, load_config
from insolvia_core.errors import ValidationError


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
    assert load_config({}).waitlist_table_name is None


def test_waitlist_table_name_is_read():
    config = load_config({"WAITLIST_TABLE_NAME": "insolvia-staging-waitlist"})
    assert config.waitlist_table_name == "insolvia-staging-waitlist"


def test_case_table_name_defaults_to_none():
    assert load_config({}).case_table_name is None


def test_case_table_name_is_read():
    config = load_config({"CASE_TABLE_NAME": "insolvia-staging-cases"})
    assert config.case_table_name == "insolvia-staging-cases"


def test_firm_table_name_defaults_to_none():
    assert load_config({}).firm_table_name is None


def test_firm_table_name_is_read():
    config = load_config({"FIRM_TABLE_NAME": "insolvia-staging-firms"})
    assert config.firm_table_name == "insolvia-staging-firms"


def test_case_access_log_table_name_defaults_to_none():
    assert load_config({}).case_access_log_table_name is None


def test_case_access_log_table_name_is_read():
    name = "insolvia-staging-case-access-log"
    config = load_config({"CASE_ACCESS_LOG_TABLE_NAME": name})
    assert config.case_access_log_table_name == name


def test_case_document_bucket_defaults_to_none():
    assert load_config({}).case_document_bucket is None


def test_case_document_bucket_is_read():
    config = load_config({"CASE_DOCUMENT_BUCKET": "insolvia-staging-case-documents-us-east-1"})
    assert config.case_document_bucket == "insolvia-staging-case-documents-us-east-1"


def test_mailer_api_url_defaults_to_none():
    assert load_config({}).mailer_api_url is None


def test_mailer_api_url_is_read():
    config = load_config({"MAILER_API_URL": "https://mailer-staging.insolvia.ai"})
    assert config.mailer_api_url == "https://mailer-staging.insolvia.ai"


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
