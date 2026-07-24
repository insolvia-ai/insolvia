from __future__ import annotations

import json
import time
import uuid
from datetime import UTC, datetime
from typing import Any

import boto3

from insolvia_mailer.core.config import ServiceConfig


def occurred_at(value: str | None = None) -> str:
    if value:
        return value
    return datetime.now(UTC).isoformat().replace("+00:00", "Z")


def event(
    service: ServiceConfig,
    application_message_id: str,
    category: str,
    message_class: str,
    status: str,
    *,
    provider_message_id: str | None = None,
    reason: str | None = None,
    event_id: str | None = None,
    timestamp: str | None = None,
) -> dict[str, Any]:
    return {
        "schema_version": 1,
        "event_id": event_id or str(uuid.uuid4()),
        "service_id": service.service_id,
        "application_message_id": application_message_id,
        "category": category,
        "message_class": message_class,
        "status": status,
        "occurred_at": occurred_at(timestamp),
        "provider_message_id": provider_message_id,
        "reason": reason,
    }


def publish(service: ServiceConfig, value: dict[str, Any]) -> None:
    if not service.status_queue_url:
        raise RuntimeError(f"status queue is not configured for {service.service_id}")
    boto3.client("sqs").send_message(
        QueueUrl=service.status_queue_url,
        MessageBody=json.dumps(value, separators=(",", ":")),
    )


STATUS_RANK = {
    "accepted": 10,
    "delivery_delay": 20,
    "delivery": 30,
    "reject": 40,
    "bounce": 40,
    "suppressed": 40,
    "disabled": 40,
    "attachment_blocked": 40,
    "complaint": 50,
}


def ttl_90_days() -> int:
    return int(time.time()) + 90 * 86400
