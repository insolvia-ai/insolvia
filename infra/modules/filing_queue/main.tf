# ── The filing queue (ADR 0024 PR 6, guardrail 1) ──────────────
# The filing worker's own queue (ADR 0018's pattern, ADR 0024's "its own
# image Lambda consuming its own SQS queue"): the attorney's per-filing
# APPROVAL is the one producer, services/filing (ADR 0024 PR 7) the one
# consumer. A filing job is never put on the case-job pipeline's queue
# (modules/job_pipeline) — that queue is fed by every preparer's "assemble"
# and "review" buttons, and a filing must not share a door with them.
#
# What is here, and what is deliberately not:
#
#   * the queue and its DLQ, in every environment (dev included — the dev
#     proof enqueues on the real per-machine queue);
#   * the API's SEND grant, attached from this side by role NAME (the
#     case_store/mailer seam pattern) — `sqs:SendMessage` on this queue and
#     nothing else. Null in dev, where the developer plays the API;
#   * the filing worker's CONSUME grant, attached the same way onto the role
#     modules/filing_credentials creates (`insolvia-<env>-filing-role`) —
#     receive, delete, read attributes. Present now so PR 7 adds a Lambda and
#     an event source mapping and no new IAM;
#   * NO event source mapping and NO Lambda. There is no consumer until
#     PR 7: an approved job waits here, and expires (below).
#
# THE MESSAGE CARRIES IDENTIFIERS ONLY — the approval id, its filing id and
# the case id (services/api/core/filing_approval.filing_job_message owns the
# shape, and its tests pin the key set). Never a credential, never a
# document, never case data. That is why SSE-SQS (the SQS-owned key) is
# enough here, for job_pipeline's reason: the case CMK would also trip
# ci-trust's DenyCaseDataDecryption fence for the deploy role.

locals {
  # insolvia-<env>-filing — the component is `filing`, the same component as
  # the worker's role (insolvia-<env>-filing-role) and the vault it opens:
  # this is the filing worker's queue, named for what it serves.
  name = "${var.project}-${var.environment}-filing"

  enqueue_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "FilingQueueSend"
        Effect   = "Allow"
        Action   = ["sqs:SendMessage"]
        Resource = aws_sqs_queue.filing.arn
      },
    ]
  })

  consume_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "FilingQueueConsume"
        Effect = "Allow"
        Action = [
          "sqs:ReceiveMessage",
          "sqs:DeleteMessage",
          "sqs:ChangeMessageVisibility",
          "sqs:GetQueueAttributes",
        ]
        Resource = aws_sqs_queue.filing.arn
      },
    ]
  })
}

resource "aws_sqs_queue" "filing_dlq" {
  name = "${local.name}-dlq"
  # The maximum, job_pipeline's reason: a message here is a filing job the
  # worker could not even start three times running — the record somebody
  # reads to find out why. Its approval has long expired by then, so nothing
  # here can ever be redriven into a filing (the consume refuses an expired
  # approval).
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
  tags                      = var.tags
}

resource "aws_sqs_queue" "filing" {
  name = local.name

  # How long a received job stays hidden before SQS hands it out again. The
  # worker's real timeout is PR 7's to set; this is the module's ceiling
  # until then, and it is deliberately well inside the approval's lifetime
  # (services/api/core/filing_approval.APPROVAL_TTL_SECONDS, one hour) so a
  # crashed attempt is retried while its approval can still be consumed.
  visibility_timeout_seconds = var.visibility_timeout_seconds

  # TWICE THE APPROVAL'S LIFETIME. A job older than its approval is a no-op —
  # the consume refuses an expired approval — so keeping it longer only keeps
  # dead letters; keeping it a little past expiry is what lets an operator
  # see a backlog that built up while no worker was running (which, until
  # ADR 0024 PR 7, is always).
  message_retention_seconds = var.message_retention_seconds

  sqs_managed_sse_enabled = true

  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.filing_dlq.arn
    # Three receives, job_pipeline's number. A redelivered job is safe by
    # construction (the consume is a single-use conditional write, and the
    # worker's state machine is PR 7's) — retries are for a worker that died
    # before it claimed anything.
    maxReceiveCount = 3
  })

  tags = var.tags
}

resource "aws_sqs_queue_redrive_allow_policy" "filing" {
  queue_url = aws_sqs_queue.filing_dlq.id
  redrive_allow_policy = jsonencode({
    redrivePermission = "byQueue"
    sourceQueueArns   = [aws_sqs_queue.filing.arn]
  })
}

# The API's send grant — the ONLY principal that may put a filing job on
# this queue, and in code the only call site is the approval
# (services/api tests/unit/test_filing_approval.py pins that).
resource "aws_iam_role_policy" "api_enqueue" {
  count = var.api_role_name == null ? 0 : 1

  # `-enqueue` is the GRANT, per the naming skill's IAM policy pattern.
  name   = "${local.name}-enqueue"
  role   = var.api_role_name
  policy = local.enqueue_policy
}

# The filing worker's consume grant. The worker never sends: a job it could
# put back on its own queue would be a filing nobody approved.
resource "aws_iam_role_policy" "worker_consume" {
  name   = "${local.name}-consume"
  role   = var.worker_role_name
  policy = local.consume_policy
}
