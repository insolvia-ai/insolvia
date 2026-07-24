from __future__ import annotations

import hashlib
import json
import logging
from typing import Any

import boto3
from botocore.exceptions import BotoCoreError, ClientError

from insolvia_mailer.adapters.aws.config import load_service_registry
from insolvia_mailer.adapters.aws.status import event as status_event
from insolvia_mailer.adapters.aws.status import publish
from insolvia_mailer.adapters.aws.store import AwsStore
from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import (
    AttachmentBlockedError,
    RetryableError,
    ValidationError,
)
from insolvia_mailer.core.mime import AttachmentContent, build_message
from insolvia_mailer.core.models import MessageRequest, recipient_hash

logger = logging.getLogger(__name__)
logger.setLevel(logging.INFO)

SAFE_SCAN = "NO_THREATS_FOUND"
BLOCKED_SCANS = {"THREATS_FOUND", "UNSUPPORTED", "ACCESS_DENIED", "FAILED"}


def _string(item: dict[str, Any], key: str) -> str | None:
    value = item.get(key)
    return value.get("S") if value else None


def _mark(store: AwsStore, record_key: str, status: str, **values: str) -> None:
    names = {"#status": "status"}
    expression = ["#status = :status", "updated_at = :updated"]
    attributes: dict[str, dict[str, str]] = {
        ":status": {"S": status},
        ":updated": {"S": status_event_time()},
    }
    for index, (key, value) in enumerate(values.items()):
        names[f"#k{index}"] = key
        attributes[f":v{index}"] = {"S": value}
        expression.append(f"#k{index} = :v{index}")
    store.ddb.update_item(
        TableName=store.messages_table,
        Key={"record_key": {"S": record_key}},
        UpdateExpression="SET " + ", ".join(expression),
        ExpressionAttributeNames=names,
        ExpressionAttributeValues=attributes,
    )


def status_event_time() -> str:
    from datetime import UTC, datetime

    return datetime.now(UTC).isoformat().replace("+00:00", "Z")


def _claim(store: AwsStore, record_key: str) -> bool:
    try:
        store.ddb.update_item(
            TableName=store.messages_table,
            Key={"record_key": {"S": record_key}},
            UpdateExpression="SET #status = :submitting, updated_at = :updated",
            ConditionExpression="#status = :queued",
            ExpressionAttributeNames={"#status": "status"},
            ExpressionAttributeValues={
                ":queued": {"S": "queued"},
                ":submitting": {"S": "submitting"},
                ":updated": {"S": status_event_time()},
            },
        )
        return True
    except ClientError as exc:
        if exc.response["Error"]["Code"] == "ConditionalCheckFailedException":
            return False
        raise


def _enabled(service: ServiceConfig) -> bool:
    if not service.kill_switch_parameter:
        return True
    response = boto3.client("ssm").get_parameter(Name=service.kill_switch_parameter)
    return response["Parameter"]["Value"].strip().lower() in {
        "true",
        "1",
        "yes",
        "enabled",
    }


def _suppressed(store: AwsStore, address: str) -> bool:
    response = store.ddb.get_item(
        TableName=store.suppressions_table,
        Key={"recipient_hash": {"S": recipient_hash(address)}},
        ConsistentRead=True,
    )
    return "Item" in response


def _attachments(store: AwsStore, manifest: dict[str, Any]) -> list[AttachmentContent]:
    contents: list[AttachmentContent] = []
    for value in manifest.get("attachments", []):
        key = value["object_key"]
        tags = store.s3.get_object_tagging(Bucket=store.bucket, Key=key).get(
            "TagSet", []
        )
        scan = next(
            (
                item["Value"]
                for item in tags
                if item["Key"] == "GuardDutyMalwareScanStatus"
            ),
            None,
        )
        if scan is None:
            raise RetryableError("attachment malware scan is pending")
        if scan in BLOCKED_SCANS:
            raise AttachmentBlockedError(f"attachment scan ended with {scan}")
        if scan != SAFE_SCAN:
            raise RetryableError("attachment malware scan has an unknown state")
        response = store.s3.get_object(Bucket=store.bucket, Key=key)
        data = response["Body"].read()
        if len(data) != value["size_bytes"]:
            raise AttachmentBlockedError("attachment size changed after registration")
        if hashlib.sha256(data).hexdigest() != value["sha256"]:
            raise AttachmentBlockedError(
                "attachment checksum changed after registration"
            )
        contents.append(
            AttachmentContent(
                attachment_id=value["attachment_id"],
                file_name=value["file_name"],
                content_type=value["content_type"],
                disposition=value["disposition"],
                content_id=value.get("content_id"),
                data=data,
            )
        )
    return contents


def _service_for_manifest(
    registry: dict[str, ServiceConfig], manifest: dict[str, Any]
) -> ServiceConfig:
    service = registry.get(manifest.get("service_id"))
    if not service:
        raise ValidationError("manifest service is not registered")
    return service


def _send_record(record: dict[str, Any], registry: dict[str, ServiceConfig]) -> None:
    store = AwsStore()
    pointer = json.loads(record["body"])
    manifest_key = pointer["manifest_key"]
    response = store.s3.get_object(Bucket=store.bucket, Key=manifest_key)
    raw_manifest = response["Body"].read()
    if hashlib.sha256(raw_manifest).hexdigest() != pointer["manifest_sha256"]:
        raise ValidationError("manifest checksum does not match the queue pointer")
    manifest = json.loads(raw_manifest)
    service = _service_for_manifest(registry, manifest)
    request = MessageRequest.from_dict(manifest, service, internal=True)
    record_key = store.message_key(service.service_id, request.application_message_id)
    existing = store.ddb.get_item(
        TableName=store.messages_table,
        Key={"record_key": {"S": record_key}},
        ConsistentRead=True,
    ).get("Item")
    if not existing:
        raise ValidationError("message admission record is missing")
    if _string(existing, "status") != "queued":
        return

    if not _enabled(service):
        _mark(store, record_key, "disabled")
        publish(
            service,
            status_event(
                service,
                request.application_message_id,
                request.category,
                request.message_class,
                "disabled",
            ),
        )
        return
    if _suppressed(store, request.to_address):
        _mark(store, record_key, "suppressed")
        publish(
            service,
            status_event(
                service,
                request.application_message_id,
                request.category,
                request.message_class,
                "suppressed",
            ),
        )
        return

    try:
        attachments = _attachments(store, manifest)
    except AttachmentBlockedError as exc:
        _mark(store, record_key, "attachment_blocked")
        publish(
            service,
            status_event(
                service,
                request.application_message_id,
                request.category,
                request.message_class,
                "attachment_blocked",
                reason=str(exc),
            ),
        )
        return

    try:
        email = build_message(service, request, attachments)
    except ValidationError as exc:
        _mark(store, record_key, "reject")
        publish(
            service,
            status_event(
                service,
                request.application_message_id,
                request.category,
                request.message_class,
                "reject",
                reason=str(exc),
            ),
        )
        return

    if not _claim(store, record_key):
        return
    try:
        response = boto3.client("sesv2").send_email(
            FromEmailAddress=service.from_address,
            Destination={"ToAddresses": [request.to_address]},
            Content={"Raw": {"Data": email.as_bytes()}},
            ConfigurationSetName=service.configuration_set,
            EmailTags=[
                {"Name": "mailer-service-id", "Value": service.service_id},
                {
                    "Name": "mailer-application-message-id",
                    "Value": request.application_message_id,
                },
                {"Name": "mailer-category", "Value": request.category},
                {"Name": "mailer-message-class", "Value": request.message_class},
            ],
        )
    except ClientError as exc:
        code = exc.response.get("Error", {}).get("Code", "")
        if code in {
            "TooManyRequestsException",
            "ThrottlingException",
            "ServiceUnavailable",
        }:
            _mark(store, record_key, "queued")
            raise RetryableError("SES explicitly rejected a retryable request") from exc
        _mark(store, record_key, "reject")
        publish(
            service,
            status_event(
                service,
                request.application_message_id,
                request.category,
                request.message_class,
                "reject",
                reason="SES rejected the message",
            ),
        )
        return
    except BotoCoreError:
        # A transport failure can happen after SES accepted the message. Leave
        # the record in submitting so an automatic retry cannot send twice;
        # SES feedback repairs accepted state when provider acceptance occurred.
        logger.exception(
            "ambiguous SES submission service_id=%s application_message_id=%s",
            service.service_id,
            request.application_message_id,
        )
        return

    provider_id = response["MessageId"]
    _mark(store, record_key, "accepted", provider_message_id=provider_id)
    publish(
        service,
        status_event(
            service,
            request.application_message_id,
            request.category,
            request.message_class,
            "accepted",
            provider_message_id=provider_id,
        ),
    )
    keys = [{"Key": manifest_key}] + [
        {"Key": item["object_key"]} for item in manifest.get("attachments", [])
    ]
    store.s3.delete_objects(
        Bucket=store.bucket, Delete={"Objects": keys, "Quiet": True}
    )


def handler(event: dict[str, Any], _context: Any) -> dict[str, Any]:
    registry = load_service_registry()
    failures = []
    for record in event.get("Records", []):
        try:
            _send_record(record, registry)
        except RetryableError:
            failures.append({"itemIdentifier": record["messageId"]})
        except Exception:
            logger.exception("Mailer sender record failed")
            failures.append({"itemIdentifier": record["messageId"]})
    return {"batchItemFailures": failures}
