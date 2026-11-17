import pytest
from insolvia_core.database import database_url
from insolvia_core.errors import ValidationError


def test_a_running_service_without_database_url_refuses_to_start():
    with pytest.raises(ValidationError, match="DATABASE_URL"):
        database_url({"DATABASE_URL": " "}, required=True)


def test_a_test_mapping_may_leave_it_unset():
    assert database_url({}, required=False) is None


def test_the_url_is_read():
    assert (
        database_url({"DATABASE_URL": "postgresql://h/db"}, required=True)
        == "postgresql://h/db"
    )
