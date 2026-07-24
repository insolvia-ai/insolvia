from __future__ import annotations

import json
import os

from insolvia_mailer.core.config import ServiceConfig, parse_service_registry
from insolvia_mailer.core.errors import ValidationError


def load_service_registry() -> dict[str, ServiceConfig]:
    name = "MAILER_SERVICE_REGISTRY_JSON"
    raw = os.environ.get(name)
    if not raw:
        raise ValidationError(f"{name} is required")
    return parse_service_registry(raw)


def configuration_set_registry() -> dict[str, str]:
    raw = os.environ.get("MAILER_CONFIGURATION_SET_REGISTRY_JSON", "{}")
    parsed = json.loads(raw)
    if not isinstance(parsed, dict):
        raise ValidationError(
            "MAILER_CONFIGURATION_SET_REGISTRY_JSON must be an object"
        )
    return {str(key): str(value) for key, value in parsed.items()}
