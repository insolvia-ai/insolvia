from __future__ import annotations

import base64
import binascii
import hashlib
import json
import os
import secrets
import time
from datetime import UTC, datetime, timedelta
from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import ConflictError, RetryableError, ValidationError
from insolvia_mailer.core.models import (
    AttachmentUploadRequest,
    MessageRequest,
    canonical_json,
)


def _now() -> int:
    return int(time.time())


def _iso(timestamp: int | None = None) -> str:
    return (
        datetime.fromtimestamp(timestamp or _now(), UTC)
        .isoformat()
        .replace("+00:00", "Z")
    )


class AwsStore:
    def __init__(self) -> None:
        self.bucket = os.environ["MAILER_CONTENT_BUCKET"]
        self.messages_table = os.environ["MAILER_MESSAGES_TABLE"]
        self.suppressions_table = os.environ["MAILER_SUPPRESSIONS_TABLE"]
        self.s3 = boto3.client("s3")
        self.sqs = boto3.client("sqs")
        self.ddb = boto3.client("dynamodb")

    @staticmethod
    def message_key(service_id: str, message_id: str) -> str:
        return f"message#{service_id}#{message_id}"

    @staticmethod
    def attachment_key(service_id: str, message_id: str, attachment_id: str) -> str:
        return f"attachments/{service_id}/{message_id}/{attachment_id}"

    def register_attachment(
        self,
        service: ServiceConfig,
        upload: AttachmentUploadRequest,
        *,
        base_url: str | None = None,
    ) -> dict[str, Any]:
        attachment_id = f"att_{secrets.token_urlsafe(18)}"
        object_key = self.attachment_key(
            service.service_id, upload.application_message_id, attachment_id
        )
        expires_at = datetime.now(UTC) + timedelta(minutes=15)
        checksum = base64.b64encode(bytes.fromhex(upload.sha256)).decode()
        filename = base64.b64encode(upload.file_name.encode()).decode()
        metadata = {
            "mailer-service-id": service.service_id,
            "mailer-message-id": upload.application_message_id,
            "mailer-attachment-id": attachment_id,
            "mailer-sha256": upload.sha256,
            "mailer-size-bytes": str(upload.size_bytes),
            "mailer-filename-b64": filename,
        }
        params = {
            "Bucket": self.bucket,
            "Key": object_key,
            "ContentType": upload.content_type,
            "ChecksumSHA256": checksum,
            "Metadata": metadata,
        }
        url = self.s3.generate_presigned_url(
            "put_object", Params=params, ExpiresIn=900, HttpMethod="PUT"
        )
        return {
            "schema_version": 1,
            "attachment_id": attachment_id,
            "upload_url": url,
            "required_headers": {
                "content-type": upload.content_type,
                "x-amz-checksum-sha256": checksum,
                "x-amz-meta-mailer-service-id": service.service_id,
                "x-amz-meta-mailer-message-id": upload.application_message_id,
                "x-amz-meta-mailer-attachment-id": attachment_id,
                "x-amz-meta-mailer-sha256": upload.sha256,
                "x-amz-meta-mailer-size-bytes": str(upload.size_bytes),
                "x-amz-meta-mailer-filename-b64": filename,
            },
            "expires_at": expires_at.isoformat().replace("+00:00", "Z"),
        }

    def attachment_records(
        self, service: ServiceConfig, message: MessageRequest
    ) -> list[dict[str, Any]]:
        records: list[dict[str, Any]] = []
        for reference in message.attachments:
            object_key = self.attachment_key(
                service.service_id,
                message.application_message_id,
                reference.attachment_id,
            )
            try:
                head = self.s3.head_object(Bucket=self.bucket, Key=object_key)
            except ClientError as exc:
                if exc.response["Error"]["Code"] in {"404", "NoSuchKey", "NotFound"}:
                    raise ValidationError("attachment upload is incomplete") from exc
                raise
            metadata = head.get("Metadata", {})
            expected_metadata = {
                "mailer-service-id": service.service_id,
                "mailer-message-id": message.application_message_id,
                "mailer-attachment-id": reference.attachment_id,
            }
            if any(
                metadata.get(key) != value for key, value in expected_metadata.items()
            ):
                raise ValidationError(
                    "attachment does not belong to this service and message"
                )
            try:
                expected_size = int(metadata["mailer-size-bytes"])
                file_name = base64.b64decode(
                    metadata["mailer-filename-b64"], validate=True
                ).decode()
                digest = metadata["mailer-sha256"]
            except (binascii.Error, KeyError, ValueError, UnicodeDecodeError) as exc:
                raise ValidationError(
                    "attachment registration metadata is invalid"
                ) from exc
            if head["ContentLength"] != expected_size:
                raise ValidationError("attachment size does not match registration")
            records.append(
                {
                    "attachment_id": reference.attachment_id,
                    "object_key": object_key,
                    "file_name": file_name,
                    "content_type": head["ContentType"],
                    "size_bytes": expected_size,
                    "sha256": digest,
                    "disposition": reference.disposition,
                    "content_id": reference.content_id,
                }
            )
        return records

    def admit_message(self, service: ServiceConfig, message: MessageRequest) -> None:
        record_key = self.message_key(
            service.service_id, message.application_message_id
        )
        request_hash = message.canonical_hash()
        attachments = self.attachment_records(service, message)
        now = _now()
        try:
            self.ddb.put_item(
                TableName=self.messages_table,
                Item={
                    "record_key": {"S": record_key},
                    "record_type": {"S": "message"},
                    "service_id": {"S": service.service_id},
                    "application_message_id": {"S": message.application_message_id},
                    "category": {"S": message.category},
                    "message_class": {"S": message.message_class},
                    "request_hash": {"S": request_hash},
                    "status": {"S": "admitting"},
                    "created_at": {"S": _iso(now)},
                    "updated_at": {"S": _iso(now)},
                    "expires_at": {"N": str(now + 90 * 86400)},
                },
                ConditionExpression="attribute_not_exists(record_key)",
            )
        except ClientError as exc:
            if exc.response["Error"]["Code"] != "ConditionalCheckFailedException":
                raise
            existing = self.ddb.get_item(
                TableName=self.messages_table,
                Key={"record_key": {"S": record_key}},
                ConsistentRead=True,
            ).get("Item", {})
            if existing.get("request_hash", {}).get("S") != request_hash:
                raise ConflictError(
                    "application_message_id was reused with different content"
                ) from exc
            if existing.get("status", {}).get("S") != "admitting":
                return

        manifest = {
            **message.to_public_dict(),
            "service_id": service.service_id,
            "sender_address": service.sender_address,
            "configuration_set": service.configuration_set,
            "attachments": attachments,
        }
        manifest_bytes = canonical_json(manifest)
        manifest_key = f"requests/{service.service_id}/{message.application_message_id}/manifest.json"
        digest = hashlib.sha256(manifest_bytes).hexdigest()
        self.s3.put_object(
            Bucket=self.bucket,
            Key=manifest_key,
            Body=manifest_bytes,
            ContentType="application/json",
            ServerSideEncryption="AES256",
        )
        if not service.send_queue_url:
            raise RetryableError("service send queue is not configured")
        self.sqs.send_message(
            QueueUrl=service.send_queue_url,
            MessageBody=json.dumps(
                {
                    "schema_version": 1,
                    "manifest_key": manifest_key,
                    "manifest_sha256": digest,
                },
                separators=(",", ":"),
            ),
        )
        self.ddb.update_item(
            TableName=self.messages_table,
            Key={"record_key": {"S": record_key}},
            UpdateExpression="SET #status = :queued, updated_at = :updated",
            ExpressionAttributeNames={"#status": "status"},
            ExpressionAttributeValues={
                ":queued": {"S": "queued"},
                ":updated": {"S": _iso()},
            },
        )
