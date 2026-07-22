from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass

from insolvia_api.core.errors import ValidationError

SERVICE_NAME = "insolvia-api"

ENVIRONMENTS = ("local", "staging", "production")


@dataclass(frozen=True)
class AppConfig:
    """The service configuration, parsed and validated once at composition time.

    Per-environment values land here as plain fields — the waitlist table
    name, allowed CORS origins, and so on arrive with the endpoints that need
    them. Everything is read in load_config; nothing else in the package
    touches os.environ.
    """

    environment: str


def load_config(environ: Mapping[str, str] | None = None) -> AppConfig:
    """Build the configuration from INSOLVIA_ENV (local|staging|production).

    Defaults to "local", mirroring the app's --dart-define=INSOLVIA_ENV.
    """
    source = os.environ if environ is None else environ
    environment = source.get("INSOLVIA_ENV", "local")
    if environment not in ENVIRONMENTS:
        raise ValidationError(
            f"INSOLVIA_ENV must be one of {', '.join(ENVIRONMENTS)}, "
            f"got {environment!r}"
        )
    return AppConfig(environment=environment)
