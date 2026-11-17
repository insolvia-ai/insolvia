import pytest
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config

# Store tests run against a real Postgres (ADR 0030): the `postgres` and
# `postgres_url` fixtures. See insolvia_core/adapters/postgres/testing.py.
pytest_plugins = ("insolvia_core.adapters.postgres.testing",)


@pytest.fixture
def store():
    return MemoryWaitlistStore()


@pytest.fixture
def mailer():
    return InMemoryMailerClient()


@pytest.fixture
def client(store, mailer):
    app = create_app(
        ApiDependencies(config=load_config({}), waitlist_store=store, mailer=mailer)
    )
    return app.test_client()
