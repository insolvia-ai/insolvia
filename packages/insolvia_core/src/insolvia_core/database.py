"""The DATABASE_URL rule every service's config shares (ADR 0030)."""

from __future__ import annotations

from collections.abc import Mapping

from insolvia_core.errors import ValidationError


def database_url(source: Mapping[str, str], *, required: bool) -> str | None:
    """The service's Postgres URL, failing closed when a running service has none.

    `required` is True when the config is read from the process environment —
    a service starting — and False when a test hands `load_config` a mapping.
    Unset never falls back to the in-memory stores silently.
    """
    value = source.get("DATABASE_URL", "").strip() or None
    if value is None and required:
        raise ValidationError(
            "DATABASE_URL is not set: the service needs its Postgres database. "
            "Locally that is postgresql://insolvia:insolvia@127.0.0.1:5432/insolvia, "
            "started by ./services/api/scripts/dev-up.sh"
        )
    return value
