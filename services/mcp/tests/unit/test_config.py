from __future__ import annotations

import pytest
from insolvia_core.errors import ValidationError
from insolvia_mcp.core.config import load_config


def test_defaults_are_local_and_unconfigured() -> None:
    config = load_config({})
    assert config.environment == "local"
    assert config.case_table_name is None
    assert config.auth_issuer_url is None
    assert config.auth_client_ids == ()
    assert config.resource_url == "http://127.0.0.1:8788/mcp"


def test_unknown_environment_is_rejected() -> None:
    with pytest.raises(ValidationError):
        load_config({"INSOLVIA_ENV": "sandbox"})


@pytest.mark.parametrize(
    ("environment", "expected"),
    [
        ("staging", "https://staging-mcp.insolvia.ai/mcp"),
        ("production", "https://mcp.insolvia.ai/mcp"),
    ],
)
def test_resource_url_follows_the_environment(environment: str, expected: str) -> None:
    assert load_config({"INSOLVIA_ENV": environment}).resource_url == expected


def test_resource_url_override_is_local_devs_seam() -> None:
    config = load_config({"MCP_RESOURCE_URL": "http://127.0.0.1:9000/mcp"})
    assert config.resource_url == "http://127.0.0.1:9000/mcp"


def test_empty_values_read_as_unset() -> None:
    config = load_config({"CASE_TABLE_NAME": "", "AUTH_ISSUER_URL": ""})
    assert config.case_table_name is None
    assert config.auth_issuer_url is None
