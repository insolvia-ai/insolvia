# Mailer operations

## Health signals

Investigate immediately when a Mailer Lambda error alarm, DLQ alarm, or
15-minute queue-age alarm fires. SES reputation is account-wide, so elevated
bounces or complaints affect every application even though configuration sets
are separated.

The platform deliberately creates no alarm subscription until an operational
destination is selected. Alarms remain visible in CloudWatch.

## Product-email kill switch

Each application has an SSM parameter. For `insolvia_api` it is:

```text
/mailer/prod/insolvia_api/transactional-email-enabled
```

Set the value to `false` to stop product sends before SES. Authentication mail
uses a separate Cognito configuration set and is not controlled by this switch.

## DLQ recovery

1. Inspect message identifiers and failure metadata only. Do not paste queue
   bodies or SES payloads into issues because they can contain private content.
2. Fix the underlying permission, validation, provider, or scan failure.
3. Redrive the DLQ into its source queue.
4. Confirm queue age and Lambda errors return to zero.

The sender marks a message `submitting` before calling SES. If a Lambda stops in
the ambiguous call window, it does not automatically send again. Wait for SES
feedback to repair the accepted state. Escalate a long-lived `submitting` record
for manual provider-event review rather than resetting it blindly.

## Suppression removal

Hard bounces and complaints are stored as irreversible recipient hashes and are
also covered by the SES account suppression list. Remove suppression only after
the address owner explicitly requests mail again and the cause is understood.
Both the Mailer table and SES account list must be reviewed.

## Attachments

Only objects tagged `GuardDutyMalwareScanStatus=NO_THREATS_FOUND` are readable by
the sender. Missing tags are pending and retryable. Threats, unsupported files,
access failures, and scan failures are terminally blocked. Quarantined content
expires after 14 days.

## Privacy

CloudWatch logs and DynamoDB records must contain only service IDs, message IDs,
categories, statuses, provider IDs, hashes, and timestamps. Never add recipient,
subject, body, filename, object key, presigned URL, or raw SES-event logging.
