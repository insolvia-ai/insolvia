from __future__ import annotations

import hashlib
import json
import logging
import time
from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_mailer.adapters.aws.config import (
    configuration_set_registry,
    load_service_registry,
)
from insolvia_mailer.adapters.aws.status import STATUS_RANK, publish, ttl_90_days
from insolvia_mailer.adapters.aws.status import event as status_event
from insolvia_mailer.adapters.aws.store import AwsStore
from insolvia_mailer.core.errors import RetryableError
from insolvia_mailer.core.models import recipient_hash

logger = logging.getLogger(__name__)
logger.setLevel(logging.INFO)

EVENT_STATUS = {
    "SEND": "accepted",
    "DELIVERY": "delivery",
    "DELIVERY_DELAY": "delivery_delay",
    "BOUNCE": "bounce",
    "COMPLAINT": "complaint",
    "REJECT": "reject",
}


def _metric(name: str, *, service_id: str | None = None) -> None:
    value: dict[str, Any] = {"MetricName": name, "Value": 1, "Unit": "Count"}
    if service_id:
        value["Dimensions"] = [{"Name": "ServiceId", "Value": service_id}]
    boto3.client("cloudwatch").put_metric_data(Namespace="Mailer", MetricData=[value])


def _unwrap(record: dict[str, Any]) -> dict[str, Any]:
    body = json.loads(record["body"])
    if "Message" in body:
        body = json.loads(body["Message"])
    return body


def _tag(tags: dict[str, Any], name: str) -> str | None:
    value = tags.get(name)
    if isinstance(value, list):
        return str(value[0]) if value else None
    return str(value) if value is not None else None


def _recipients(payload: dict[str, Any], status: str) -> list[str]:
    if status == "bounce":
        return [
            item["emailAddress"]
            for item in payload.get("bounce", {}).get("bouncedRecipients", [])
            if item.get("emailAddress")
        ]
    if status == "complaint":
        return [
            item["emailAddress"]
            for item in payload.get("complaint", {}).get("complainedRecipients", [])
            if item.get("emailAddress")
        ]
    return []


def _suppresses_recipient(payload: dict[str, Any], status: str) -> bool:
    if status == "complaint":
        return True
    return (
        status == "bounce"
        and payload.get("bounce", {}).get("bounceType") == "Permanent"
    )


def _event_identity(
    payload: dict[str, Any], registry: dict, config_sets: dict[str, str]
) -> tuple[Any, str, str, str]:
    mail = payload.get("mail", {})
    tags = mail.get("tags", {})
    service_id = _tag(tags, "mailer-service-id")
    application_id = _tag(tags, "mailer-application-message-id")
    category = _tag(tags, "mailer-category")
    message_class = _tag(tags, "mailer-message-class")
    if not service_id:
        configuration_set = _tag(tags, "ses:configuration-set") or mail.get(
            "configurationSetName"
        )
        service_id = config_sets.get(configuration_set)
        application_id = f"auth_{mail.get('messageId')}"
        category = "authentication"
        message_class = "authentication"
    service = registry.get(service_id)
    if not service or not application_id or not category or not message_class:
        raise ValueError("SES event cannot be mapped to a registered service")
    return service, application_id, category, message_class


def _claim_event(store: AwsStore, event_id: str) -> bool:
    now = int(time.time())
    key = {"record_key": {"S": f"event#{event_id}"}}
    try:
        store.ddb.update_item(
            TableName=store.messages_table,
            Key=key,
            UpdateExpression=(
                "SET record_type = :event, #state = :processing, "
                "lease_until = :lease, expires_at = :expires"
            ),
            ConditionExpression=(
                "attribute_not_exists(record_key) OR "
                "(#state = :processing AND lease_until < :now)"
            ),
            ExpressionAttributeNames={"#state": "processing_state"},
            ExpressionAttributeValues={
                ":event": {"S": "event"},
                ":processing": {"S": "processing"},
                ":lease": {"N": str(now + 300)},
                ":now": {"N": str(now)},
                ":expires": {"N": str(ttl_90_days())},
            },
        )
        return True
    except ClientError as exc:
        if exc.response["Error"]["Code"] != "ConditionalCheckFailedException":
            raise
        existing = store.ddb.get_item(
            TableName=store.messages_table,
            Key=key,
            ConsistentRead=True,
        ).get("Item", {})
        if existing.get("processing_state", {}).get("S") == "processed":
            return False
        raise RetryableError("feedback event is already being processed") from exc


def _complete_event(store: AwsStore, event_id: str) -> None:
    store.ddb.update_item(
        TableName=store.messages_table,
        Key={"record_key": {"S": f"event#{event_id}"}},
        UpdateExpression="SET #state = :processed REMOVE lease_until",
        ExpressionAttributeNames={"#state": "processing_state"},
        ExpressionAttributeValues={":processed": {"S": "processed"}},
    )


def _suppress(
    store: AwsStore, recipients: list[str], reason: str, event_id: str
) -> None:
    for address in recipients:
        store.ddb.put_item(
            TableName=store.suppressions_table,
            Item={
                "recipient_hash": {"S": recipient_hash(address)},
                "reason": {"S": reason},
                "source_event_id": {"S": event_id},
            },
        )


def _update_message(
    store: AwsStore, service_id: str, application_id: str, status: str, provider_id: str
) -> None:
    key = store.message_key(service_id, application_id)
    existing = store.ddb.get_item(
        TableName=store.messages_table,
        Key={"record_key": {"S": key}},
        ConsistentRead=True,
    ).get("Item")
    if not existing:
        return
    current = existing.get("status", {}).get("S", "accepted")
    if STATUS_RANK.get(status, 0) < STATUS_RANK.get(current, 0):
        return
    store.ddb.update_item(
        TableName=store.messages_table,
        Key={"record_key": {"S": key}},
        UpdateExpression="SET #status = :status, provider_message_id = :provider",
        ExpressionAttributeNames={"#status": "status"},
        ExpressionAttributeValues={
            ":status": {"S": status},
            ":provider": {"S": provider_id},
        },
    )


def _process(
    record: dict[str, Any], registry: dict, config_sets: dict[str, str]
) -> None:
    payload = _unwrap(record)
    event_type = payload.get("eventType") or payload.get("notificationType")
    status = EVENT_STATUS.get(str(event_type).upper())
    if not status:
        return
    mail = payload.get("mail", {})
    provider_id = mail.get("messageId")
    raw_event_id = payload.get("eventId") or (
        f"{provider_id}:{event_type}:{mail.get('timestamp', '')}"
    )
    event_id = hashlib.sha256(raw_event_id.encode()).hexdigest()
    store = AwsStore()
    if not _claim_event(store, event_id):
        return
    service, application_id, category, message_class = _event_identity(
        payload, registry, config_sets
    )
    if _suppresses_recipient(payload, status):
        _suppress(store, _recipients(payload, status), status, event_id)
    _update_message(store, service.service_id, application_id, status, provider_id)
    _metric(
        status.replace("_", "-").title().replace("-", ""), service_id=service.service_id
    )
    publish(
        service,
        status_event(
            service,
            application_id,
            category,
            message_class,
            status,
            provider_message_id=provider_id,
            event_id=event_id,
            timestamp=mail.get("timestamp"),
        ),
    )
    _complete_event(store, event_id)


def handler(event: dict[str, Any], _context: Any) -> dict[str, Any]:
    guardduty_status = event.get("guardduty_status")
    if guardduty_status:
        metric = (
            "AttachmentThreat"
            if guardduty_status == "THREATS_FOUND"
            else "AttachmentScanFailure"
        )
        _metric(metric)
        logger.warning("GuardDuty attachment result status=%s", guardduty_status)
        return {"batchItemFailures": []}

    registry = load_service_registry()
    config_sets = configuration_set_registry()
    failures = []
    for record in event.get("Records", []):
        try:
            _process(record, registry, config_sets)
        except Exception:
            logger.exception("Mailer feedback record failed")
            failures.append({"itemIdentifier": record["messageId"]})
    return {"batchItemFailures": failures}
