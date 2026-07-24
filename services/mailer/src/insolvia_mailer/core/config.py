from __future__ import annotations

import json
from dataclasses import dataclass

from insolvia_mailer.core.errors import ValidationError


@dataclass(frozen=True)
class ServiceConfig:
    service_id: str
    sender_name: str
    sender_address: str
    allowed_categories: frozenset[str]
    allowed_message_classes: frozenset[str]
    allowed_role_arns: frozenset[str] = frozenset()
    configuration_set: str | None = None
    send_queue_url: str | None = None
    status_queue_url: str | None = None
    kill_switch_parameter: str | None = None

    @property
    def from_address(self) -> str:
        return f"{self.sender_name} <{self.sender_address}>"


def parse_service_registry(raw: str) -> dict[str, ServiceConfig]:
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ValidationError("service registry is not valid JSON") from exc
    if not isinstance(parsed, dict) or not parsed:
        raise ValidationError("service registry must contain at least one service")

    result: dict[str, ServiceConfig] = {}
    for service_id, value in parsed.items():
        if not isinstance(value, dict):
            raise ValidationError(
                f"service configuration for {service_id!r} must be an object"
            )
        result[service_id] = ServiceConfig(
            service_id=service_id,
            sender_name=str(value["sender_name"]),
            sender_address=str(value["sender_address"]),
            allowed_categories=frozenset(value.get("allowed_categories", [])),
            allowed_message_classes=frozenset(value.get("allowed_message_classes", [])),
            allowed_role_arns=frozenset(value.get("allowed_role_arns", [])),
            configuration_set=value.get("configuration_set"),
            send_queue_url=value.get("send_queue_url"),
            status_queue_url=value.get("status_queue_url"),
            kill_switch_parameter=value.get("kill_switch_parameter"),
        )
    return result
