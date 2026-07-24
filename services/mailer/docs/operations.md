# Mailer operations

Alarms live in `infra/modules/mailer/main.tf` under "Alerting (issue 6.7)",
wired to the module's own SNS topic (`insolvia-mailer-<env>-alarms`, module
output `alarms_topic_arn`, env output `mailer_alarms_topic_arn`). Terraform
manages no subscriptions — subscribe by hand once, against that topic ARN, and
confirm the email. Until someone does, every alarm is visible in CloudWatch
but pages nobody.

## Alarms and what to do

| Alarm | Fires when | What it means | What to do |
|---|---|---|---|
| `<env>-ingress-errors` / `-sender-errors` / `-feedback-errors` | `AWS/Lambda` `Errors` > 0 (5 min) on the named Lambda | An unhandled exception or crashed runtime — the mailer catches its own expected failures (bad category, disallowed message class, disallowed caller) and turns them into a reject event instead of an Errors datapoint | Check the Lambda's CloudWatch log group for the stack trace; fix and redeploy |
| `<env>-send-dlq-not-empty` / `-status-dlq-not-empty` / `-feedback-dlq-not-empty` | `AWS/SQS` `ApproximateNumberOfMessagesVisible` > 0 on the named DLQ | A message exhausted its 5 redrive attempts — a consistent failure, not a transient one | See *DLQ recovery* below |
| `<env>-api-send-oldest-message` | `AWS/SQS` `ApproximateAgeOfOldestMessage` > 900s on the send queue | The sender Lambda is falling behind or stalled, without any single message failing enough times to DLQ | Check sender Lambda health and concurrency (`reserved_concurrent_executions = 5`); check for a stuck SES call |
| `<env>-api-rejects` | Custom `Mailer`/`Reject` metric (dimension `ServiceId=insolvia_api`) > 0 | insolvia_api sent a message the mailer refused at ingress, or SES itself rejected it | Check the reject reason in the ingress Lambda logs — usually a caller/category/message-class mismatch between the API service and the mailer's `insolvia_api` service-registry entry |
| `<env>-ses-bounce-rate` | `AWS/SES` `Reputation.BounceRate` average > 5% (15 min) | See *SES reputation* below | See *SES reputation* below |
| `<env>-ses-complaint-rate` | `AWS/SES` `Reputation.ComplaintRate` average > 0.1% (15 min) | See *SES reputation* below | See *SES reputation* below |
| `<env>-attachment-threat` / `-attachment-scan-failure` | Custom `Mailer`/`AttachmentThreat`/`AttachmentScanFailure` metric > 0 | GuardDuty found a threat in, or could not scan, an uploaded attachment | See *Attachments* below. **Only exists when `enable_attachment_scanning = true`** — off by default in both environments (no category sends attachments yet), so these alarms are absent from the account, not just quiet, until that flag flips |

## SES reputation — bounce and complaint rate

These two alarms are the reason this is its own PR (issue 6.7), and the reason
they exist before requesting SES production access (issue 6.8): AWS reviews
exactly these numbers before granting it, and a dirty reputation is grounds
for refusal or later suspension.

- **Bounce rate** is the share of sends SES could not deliver — mostly invalid
  or non-existent addresses. AWS's own enforcement threshold is roughly 10%;
  this alarm fires at **5%**, half that, so there is room to react before SES
  acts on its own.
- **Complaint rate** is the share of recipients who hit "report spam." AWS's
  own threshold is roughly 0.5%; this alarm fires at **0.1%**, again well
  before SES's own cutoff — complaints are the more sensitive signal because
  legitimate transactional mail (welcome, verification, password reset)
  should almost never be marked as spam, so even a handful of complaints
  against low volume can trip this.

**Both metrics are account-wide**, not scoped to one configuration set or one
application. SES reputation is a shared resource: if either threshold is
breached and ignored, SES can throttle or suspend sending for *every*
application on this account, not just insolvia_api.

When either alarm fires:

1. **Do not wait.** Sustained breach risks an account-wide sending pause.
2. Check `services/mailer`'s suppressions table and the SES account-level
   suppression list for the affected recipients — confirm the bounces/
   complaints are being suppressed going forward, not just logged.
3. Identify the source: a bad import, a compromised signup flow generating
   junk addresses, a content/formatting issue triggering spam reports, etc.
4. If the source can't be identified or stopped quickly, use the kill switch
   below to stop insolvia_api sends entirely while you investigate — a
   deliberate pause protects the account's reputation far better than
   continuing to send into it.
5. Confirm both rates have returned under threshold before resuming, and
   before treating issue 6.8 (SES production access) as unblocked — a
   recent breach is exactly what that request gets scrutinized for.

## Kill switch

Each application in the mailer's service registry has its own SSM parameter.
For `insolvia_api` it is:

```text
/insolvia/<env>/mailer/insolvia-api-sending-enabled
```

(`aws_ssm_parameter.api_sending_enabled` in `infra/modules/mailer/main.tf`,
read by the sender Lambda's `_enabled()` check before every SES call.) Set the
value to anything other than `true`/`1`/`yes`/`enabled` to stop insolvia_api's
sends before they reach SES — the ingress Lambda keeps admitting messages onto
the send queue, they simply accumulate there (watch the queue-age alarm) until
sending is re-enabled. This account has exactly one mailer tenant today, so
this switch effectively pauses the whole platform's outbound mail.

## DLQ recovery

1. Inspect message identifiers and failure metadata only. Do not paste queue
   bodies or SES payloads into issues — they can contain private content.
2. Fix the underlying permission, validation, provider, or scan failure.
3. Redrive the DLQ into its source queue (send DLQ -> send queue, status DLQ
   -> status queue, feedback DLQ -> feedback queue).
4. Confirm the corresponding `-errors` alarm and queue-age alarm return to
   `OK`.

The sender marks a message `submitting` before calling SES. If the Lambda
stops in the ambiguous call window, it does not automatically retry. Wait for
SES feedback (via the feedback Lambda) to repair the accepted state. Escalate
a long-lived `submitting` record for manual provider-event review rather than
resetting it blindly.

## Suppression removal

Hard bounces and complaints are stored as irreversible recipient hashes in the
suppressions table and are also covered by the SES account-level suppression
list. Remove a suppression only after the address owner explicitly requests
mail again and the original cause is understood. Check both the mailer's
suppressions table and the SES account list — removing from one without the
other leaves the recipient effectively still suppressed.

## Attachments

Insolvia sends no attachments today (see `enable_attachment_scanning`'s
comment in `infra/modules/mailer/variables.tf`), so the `attachment_threat`
and `attachment_scan_failure` alarms do not exist in either environment while
that variable stays `false`. If it is flipped to `true`: only objects tagged
`GuardDutyMalwareScanStatus=NO_THREATS_FOUND` are readable by the sender.
Missing tags are pending and retryable. Threats, unsupported files, access
failures, and scan failures are terminally blocked. Quarantined content
expires after 14 days.

## Privacy

CloudWatch logs and DynamoDB records must contain only service IDs, message
IDs, categories, statuses, provider IDs, hashes, and timestamps. Never add
recipient, subject, body, filename, object key, presigned URL, or raw SES
event logging.
