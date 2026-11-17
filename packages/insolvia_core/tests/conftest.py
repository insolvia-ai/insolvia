# Store tests run against a real Postgres (ADR 0030): the `postgres` and
# `postgres_url` fixtures. See insolvia_core/adapters/postgres/testing.py.
pytest_plugins = ("insolvia_core.adapters.postgres.testing",)
