# ── Async job pipeline (ADR 0018, issue #271) ───────────────────
# The orchestration half of the pipelines beside the lambdalith (ADR 0015):
# one SQS queue the API enqueues accepted jobs onto, one image-packaged worker
# Lambda consuming it, and the DLQ + alarms that make failure loud. The job
# RECORD lives in the case table (modules/case_store — the worker's grant is
# attached there, from that module's side, via worker_role_name), and the API
# is the only status surface a client ever sees.
#
# One instance per environment. In infra/envs/dev the WORKER half is absent
# (ecr_repository_url = null): there is no Lambda in dev at all, and the
# queue+DLQ alone are the point — the local API enqueues to a real queue and
# entrypoints/worker_poller.py consumes it, so the seam runs for real on a
# laptop and only the managed SQS→Lambda delivery loop itself is
# cloud-only (the approximation ADR 0018 writes down).
#
# ── Bootstrap order (read before the FIRST apply in a fresh account) ────────
# Same image-before-apply deadlock as every image Lambda here: the worker
# function cannot be created until an image exists in insolvia-shared-jobs.
# Once per environment:
#
#   1. apply infra/envs/shared            (creates insolvia-shared-jobs)
#   2. scripts/bootstrap-ecr-images.sh <env> jobs
#      (builds services/api/Dockerfile --target worker and pushes :<env>)
#   3. terraform apply                    (full)
#
# Every later deploy is api-<env>.yml pushing and update-function-code —
# Terraform ignores the image drift (see the lifecycle note on the Lambda).

locals {
  # insolvia-<env>-jobs — the component is `jobs`, named for what this
  # serves (the case-job pipeline), per the insolvia-aws-naming skill.
  name = "${var.project}-${var.environment}-jobs"

  # The worker half exists only where an image repository is supplied —
  # everywhere deployed, never in dev.
  worker_count = var.ecr_repository_url == null ? 0 : 1

  # Alarms additionally need somewhere to go; dev passes neither.
  alarm_count = var.ecr_repository_url != null && var.alarms_topic_arn != null ? 1 : 0
}

# ── Queues ──────────────────────────────────────────────────────
# The message body is identifiers only — job id, case id, kind — never case
# data; services/api/core/jobs.py owns that contract and its tests pin it.
# That is why SSE-SQS (the SQS-owned key) is sufficient here and the
# environment's case CMK is deliberately NOT used: nothing GLBA-scope ever
# transits the queue, and pointing it at the case key would also trip
# ci-trust's DenyCaseDataDecryption fence for the deploy role, exactly as the
# audit trail module documents for its bucket.

resource "aws_sqs_queue" "jobs_dlq" {
  name = "${local.name}-dlq"
  # The maximum. A message lands here only after every retry failed; it is
  # the debugging record, and the alarm below is what makes it get read.
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
  tags                      = var.tags
}

resource "aws_sqs_queue" "jobs" {
  name = local.name
  # 6× the worker's timeout, per AWS's own guidance for Lambda event source
  # mappings: the poller's in-flight batch window must comfortably outlive
  # the slowest invocation, or a still-running job's message is redelivered
  # mid-run. (The conditional status writes in core/jobs.py make that safe,
  # but "rare race" beats "designed-in race".)
  visibility_timeout_seconds = 6 * var.worker_timeout_seconds
  # Messages this old are stale: the job record already shows failed (or the
  # DLQ has the message). 24h matches the mailer's send queue.
  message_retention_seconds = 86400
  sqs_managed_sse_enabled   = true
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.jobs_dlq.arn
    # Three attempts total. Jobs are minutes-long and workers mark the job
    # failed before re-raising, so the preparer sees an honest status after
    # attempt one — retries are for transient infrastructure, and anything
    # three attempts cannot fix wants a human, via the DLQ alarm.
    maxReceiveCount = 3
  })
  tags = var.tags
}

resource "aws_sqs_queue_redrive_allow_policy" "jobs" {
  queue_url = aws_sqs_queue.jobs_dlq.id
  redrive_allow_policy = jsonencode({
    redrivePermission = "byQueue"
    sourceQueueArns   = [aws_sqs_queue.jobs.arn]
  })
}

# ── Worker execution role ───────────────────────────────────────
# Its own role, not the API's, so the worker's reach is its own: today the
# queue plus (via modules/case_store, worker_role_name) the case table.
# Created even in dev? No — the whole worker half is counted out there; the
# developer's own IAM user is the principal for the poller.

data "aws_iam_policy_document" "lambda_trust" {
  count = local.worker_count

  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "worker" {
  count = local.worker_count

  name               = "${local.name}-worker-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_trust[0].json
  tags               = var.tags
}

resource "aws_iam_role_policy_attachment" "worker_basic" {
  count = local.worker_count

  role       = aws_iam_role.worker[0].name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

# Consume-only, on this environment's queue only. Send stays with the API
# role's grant below; the worker never enqueues today — a chained job (a
# worker accepting follow-on work) is a capability to grant in a diff that
# says so, per the same rule every omitted grant in this repo follows.
resource "aws_iam_role_policy" "worker_queue" {
  count = local.worker_count

  # `-consume` is the GRANT this policy carries, per the naming skill's IAM
  # policy pattern.
  name = "${local.name}-worker-consume"
  role = aws_iam_role.worker[0].id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = [
          "sqs:ReceiveMessage",
          "sqs:DeleteMessage",
          "sqs:GetQueueAttributes",
        ]
        Resource = aws_sqs_queue.jobs.arn
      },
    ]
  })
}

# The API's send grant, attached from this side onto the API role — the
# case_store/mailer seam pattern (a name in, an aws_iam_role_policy here), so
# api_service never has to know this module exists and no reference runs the
# other way.
resource "aws_iam_role_policy" "api_enqueue" {
  count = var.api_role_name == null ? 0 : 1

  name = "${local.name}-api-enqueue"
  role = var.api_role_name
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["sqs:SendMessage"]
        Resource = aws_sqs_queue.jobs.arn
      },
    ]
  })
}

# ── Worker Lambda ───────────────────────────────────────────────

resource "aws_lambda_function" "worker" {
  count = local.worker_count

  function_name = "${local.name}-worker"
  role          = aws_iam_role.worker[0].arn
  package_type  = "Image"
  # The per-environment marker tag on the shared insolvia-shared-jobs repo —
  # the same seed-only meaning as api_service's (that module's comment owns
  # the argument against :latest under a shared repository).
  image_uri = "${var.ecr_repository_url}:${var.image_tag}"
  # The ceiling every job attempt runs under. 15 minutes is Lambda's own
  # maximum — a job that cannot fit it is ADR 0018's first reopen trigger,
  # not a reason to chunk work invisibly.
  timeout     = var.worker_timeout_seconds
  memory_size = var.worker_memory_mb

  # No `publish`, no alias, unlike api_service — and that is a difference
  # with a reason, not an omission. The alias machinery exists so an API
  # deploy can smoke-test a version before API Gateway routes traffic to it;
  # a queue consumer has no router to protect. A bad worker build marks jobs
  # failed, SQS retries them, and rollback is update-function-code to the
  # previous image — the queue itself buffers the gap.

  environment {
    variables = {
      INSOLVIA_ENV = var.insolvia_env
    }
  }

  # The deploy workflow owns both, exactly as it does for the API Lambda: it
  # pushes an image and injects the environment it resolves from the same
  # /insolvia/<env>/api SSM namespace (CASE_TABLE_NAME is what the worker
  # actually reads). The block above is only the first-apply seed — which is
  # also why a freshly bootstrapped worker cannot run a job until the first
  # api-<env>.yml deploy injects its environment.
  lifecycle { ignore_changes = [image_uri, environment] }

  tags = var.tags
  depends_on = [
    aws_iam_role_policy_attachment.worker_basic,
    aws_iam_role_policy.worker_queue,
  ]
}

resource "aws_cloudwatch_log_group" "worker" {
  count = local.worker_count

  name              = "/aws/lambda/${aws_lambda_function.worker[0].function_name}"
  retention_in_days = 14
  tags              = var.tags
}

# Batch size 1: jobs are minutes-long, so batching buys nothing and would
# make one job's crash retry its batch-mates (ReportBatchItemFailures exists
# for that, but a contract nothing needs is a contract nothing tests —
# services/api's handle_sqs_event documents the same choice from its side).
resource "aws_lambda_event_source_mapping" "jobs" {
  count = local.worker_count

  event_source_arn = aws_sqs_queue.jobs.arn
  function_name    = aws_lambda_function.worker[0].arn
  batch_size       = 1
}

# ── Alarms ──────────────────────────────────────────────────────
# Two, and the DLQ one is the pipeline's real pager: a message parked there
# means a job exhausted its retries and a preparer is looking at a `failed`
# status nobody is working. Same manual-subscription rule as every topic here
# (the topic arrives via var.alarms_topic_arn — api_service owns it).

resource "aws_cloudwatch_metric_alarm" "dlq_depth" {
  count = local.alarm_count

  alarm_name          = "${local.name}-dlq-depth"
  alarm_description   = "A pipeline job exhausted its retries — its message is parked on the DLQ."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "ApproximateNumberOfMessagesVisible"
  namespace           = "AWS/SQS"
  period              = 300
  statistic           = "Maximum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { QueueName = aws_sqs_queue.jobs_dlq.name }

  alarm_actions = [var.alarms_topic_arn]
  ok_actions    = [var.alarms_topic_arn]
  tags          = var.tags
}

# Worker errors are EXPECTED in bounded numbers — an infrastructure failure
# re-raises on purpose so SQS retries (core/jobs.run_job) — so this alarms on
# any error like the API does, but a single blip self-clears via ok_actions.
resource "aws_cloudwatch_metric_alarm" "worker_errors" {
  count = local.alarm_count

  alarm_name          = "${local.name}-worker-errors"
  alarm_description   = "The pipeline worker Lambda raised — a job attempt failed (SQS will retry up to maxReceiveCount)."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "Errors"
  namespace           = "AWS/Lambda"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { FunctionName = aws_lambda_function.worker[0].function_name }

  alarm_actions = [var.alarms_topic_arn]
  ok_actions    = [var.alarms_topic_arn]
  tags          = var.tags
}
