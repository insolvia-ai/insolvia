# The Insolvia mailer (services/mailer): a shared, multi-tenant transactional
# email microservice sitting behind a SigV4-authenticated HTTP API, callable
# only by other Insolvia services — today, exactly one: the API Lambda
# (module.api_service), sending welcome / email_verification / password_reset
# mail as insolvia_api.
#
# Ported from andreas-services/mailer/infra/modules/platform, de-humbugged:
# `humbugg` (that platform's one tenant) becomes `insolvia_api` (this
# platform's one tenant), and the SigV4 caller role is looked up by name
# instead of hard-coded. Same architecture: ingress Lambda admits a message
# and hands it to SQS, a sender Lambda drains the queue and calls SES, and a
# feedback Lambda drains SES bounce/complaint/delivery notifications back into
# the message store. Mirrors the andreas-services mailer platform module,
# which is also the house pattern api_service itself was built from — read
# that module's main.tf for the HTTP-API-behind-a-custom-domain shape this one
# reuses.
#
# Deliberately deferred to a later PR (issue 6.7): the ~9 CloudWatch metric
# alarms (Lambda errors, DLQ-not-empty, queue age, attachment threat/scan
# failure, SES bounce/complaint rate) and their alerting SNS topic that
# upstream's platform module ends with. This module ships the data plane
# only — queues, storage, SES config, IAM, the API, and the SNS->SQS feedback
# ingestion path (NOT an alerting topic; SES publishes bounce/complaint
# notifications here so the feedback Lambda can process them, alarms or not).
#
# ── Bootstrap order (read before the FIRST apply in a fresh account) ────────
# Same image-before-apply deadlock as api_service, times three Lambdas from
# one image:
#
#   1. terraform apply -target=module.mailer.aws_ecr_repository.mailer
#   2. build services/mailer (`docker build --target lambda`), tag
#      <repo-url>:latest, push it. Every later deploy just re-pushes and calls
#      update-function-code for ingress/sender/feedback — Terraform ignores
#      image_uri drift (see the lifecycle note on each Lambda below).
#   3. terraform apply   (full)

data "aws_caller_identity" "current" {}

data "aws_region" "current" {}

# The one caller registered in the service registry below. Looked up by name
# rather than hard-coded (upstream's `data.aws_iam_role.humbugg`), since the
# caller is this module's own sibling — module.api_service.lambda_role_name.
data "aws_iam_role" "caller" {
  name = var.caller_role_name
}

locals {
  # insolvia-mailer-<env> — reused across resource types that don't need a
  # purpose qualifier (ECR repo, HTTP API, SES configuration set), mirroring
  # how upstream's `local.name` was reused the same way.
  name = "${var.project}-mailer-${var.environment}"

  # Insolvia's traffic is all transactional auth mail (welcome,
  # email_verification, password_reset from PR1) — one configuration set is
  # enough. Upstream needed two (humbugg_product / humbugg_auth) because it
  # split product mail from auth mail across two tenants; this platform has
  # only ever had one.
  configuration_set = local.name

  # SES has no resource-level ARN for the SendEmail/SendRawEmail actions on
  # the identity itself, but resource-level scoping to the identity ARN (built
  # from the account + region, exactly like upstream's api_url pattern below)
  # is still tighter than upstream's Resource = "*". The identity itself lives
  # in infra/envs/shared (module.email) — this module only ever references it.
  ses_identity_arn = "arn:aws:ses:${data.aws_region.current.name}:${data.aws_caller_identity.current.account_id}:identity/insolvia.ai"

  # The mailer's one tenant. Matches ServiceConfig / parse_service_registry in
  # services/mailer/src/insolvia_mailer/core/config.py field-for-field —
  # sender_name, sender_address, allowed_categories, allowed_message_classes,
  # allowed_role_arns, configuration_set, send_queue_url, status_queue_url,
  # kill_switch_parameter are exactly the keys that function reads.
  insolvia_api_service = {
    sender_name             = "Insolvia"
    sender_address          = var.sender_address
    allowed_categories      = ["welcome", "email_verification", "password_reset"]
    allowed_message_classes = ["transactional"]
    allowed_role_arns       = [data.aws_iam_role.caller.arn]
    configuration_set       = local.configuration_set
    send_queue_url          = aws_sqs_queue.api_send.url
    status_queue_url        = aws_sqs_queue.api_status.url
    kill_switch_parameter   = aws_ssm_parameter.api_sending_enabled.name
  }

  # MAILER_SERVICE_REGISTRY_JSON — read by load_service_registry() in
  # adapters/aws/config.py, in all three Lambdas.
  service_registry_json = jsonencode({ insolvia_api = local.insolvia_api_service })

  # MAILER_CONFIGURATION_SET_REGISTRY_JSON — read by configuration_set_registry()
  # in adapters/aws/config.py, feedback Lambda only. Maps an SES configuration
  # set name back to the service that owns it, for feedback events that carry
  # no mailer-service-id tag (SES's own auth-mail sends outside this
  # platform's tagging, per the feedback Lambda's _event_identity fallback).
  # With exactly one configuration set, this is a one-entry map — kept as a
  # map (not simplified away) because the feedback Lambda's env var contract
  # expects one regardless of tenant count.
  configuration_set_registry_json = jsonencode({
    (local.configuration_set) = "insolvia_api"
  })
}

# ─── Container repository ────────────────────────────────────────────────────

resource "aws_ecr_repository" "mailer" {
  name                 = local.name
  image_tag_mutability = "MUTABLE"

  image_scanning_configuration {
    scan_on_push = true
  }

  tags = var.tags
}

resource "aws_ecr_lifecycle_policy" "mailer" {
  repository = aws_ecr_repository.mailer.name
  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "Keep the ten newest mailer images"
      selection = {
        tagStatus   = "any"
        countType   = "imageCountMoreThan"
        countNumber = 10
      }
      action = { type = "expire" }
    }]
  })
}

# ─── Private content storage ─────────────────────────────────────────────────
# Holds request manifests (14-day expiry) and, once attachments ship,
# uploaded attachment bytes pending scan. A fixed, deterministic name — like
# web_hosting/marketing_site's buckets, not upstream's bucket_prefix — since
# this repo's convention names S3 buckets exactly insolvia-<thing>-<env>.

resource "aws_s3_bucket" "content" {
  bucket        = "${var.project}-mailer-content-${var.environment}"
  force_destroy = false
  tags          = var.tags
}

resource "aws_s3_bucket_public_access_block" "content" {
  bucket                  = aws_s3_bucket.content.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "content" {
  bucket = aws_s3_bucket.content.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_cors_configuration" "content" {
  bucket = aws_s3_bucket.content.id

  cors_rule {
    allowed_methods = ["PUT"]
    allowed_origins = [var.cors_allowed_origin]
    allowed_headers = [
      "content-type",
      "x-amz-checksum-sha256",
      "x-amz-meta-*",
    ]
    expose_headers  = ["etag"]
    max_age_seconds = 300
  }
}

resource "aws_s3_bucket_versioning" "content" {
  bucket = aws_s3_bucket.content.id
  versioning_configuration {
    status = "Disabled"
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "content" {
  bucket = aws_s3_bucket.content.id

  rule {
    id     = "expire-mailer-content"
    status = "Enabled"
    filter {}
    expiration { days = 14 }
    abort_incomplete_multipart_upload { days_after_initiation = 1 }
  }
}

# The sender-reads-only-clean-attachments statement holds regardless of
# enable_attachment_scanning: if scanning is off, no object under
# attachments/* is ever tagged NO_THREATS_FOUND, so the sender role is denied
# read on all of them — moot today (no category sends attachments) and the
# safe default the day one does.
data "aws_iam_policy_document" "content_bucket" {
  statement {
    sid     = "DenyInsecureTransport"
    effect  = "Deny"
    actions = ["s3:*"]
    resources = [
      aws_s3_bucket.content.arn,
      "${aws_s3_bucket.content.arn}/*",
    ]
    principals {
      type        = "*"
      identifiers = ["*"]
    }
    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }

  statement {
    sid       = "SenderReadsOnlyCleanAttachments"
    effect    = "Deny"
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.content.arn}/attachments/*"]
    principals {
      type        = "AWS"
      identifiers = [aws_iam_role.sender.arn]
    }
    condition {
      test     = "StringNotEquals"
      variable = "s3:ExistingObjectTag/GuardDutyMalwareScanStatus"
      values   = ["NO_THREATS_FOUND"]
    }
  }
}

resource "aws_s3_bucket_policy" "content" {
  bucket = aws_s3_bucket.content.id
  policy = data.aws_iam_policy_document.content_bucket.json
}

# ─── DynamoDB ────────────────────────────────────────────────────────────────

resource "aws_dynamodb_table" "messages" {
  name         = "${var.project}-mailer-messages-${var.environment}"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "record_key"

  attribute {
    name = "record_key"
    type = "S"
  }

  ttl {
    attribute_name = "expires_at"
    enabled        = true
  }

  point_in_time_recovery { enabled = true }
  server_side_encryption { enabled = true }
  tags = var.tags
}

resource "aws_dynamodb_table" "suppressions" {
  name         = "${var.project}-mailer-suppressions-${var.environment}"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "recipient_hash"

  attribute {
    name = "recipient_hash"
    type = "S"
  }

  point_in_time_recovery { enabled = true }
  server_side_encryption { enabled = true }
  tags = var.tags
}

# ─── Queues ──────────────────────────────────────────────────────────────────
# De-humbugged: humbugg_send/_dlq -> api_send/_dlq, humbugg_status/_dlq ->
# api_status/_dlq — the "api" names the caller (insolvia_api), matching the
# service registry key.

resource "aws_sqs_queue" "api_send_dlq" {
  name                      = "${var.project}-mailer-api-send-dlq-${var.environment}"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
  tags                      = var.tags
}

resource "aws_sqs_queue" "api_send" {
  name                       = "${var.project}-mailer-api-send-${var.environment}"
  message_retention_seconds  = 86400
  visibility_timeout_seconds = 180
  sqs_managed_sse_enabled    = true
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.api_send_dlq.arn
    maxReceiveCount     = 5
  })
  tags = var.tags
}

resource "aws_sqs_queue_redrive_allow_policy" "api_send" {
  queue_url = aws_sqs_queue.api_send_dlq.id
  redrive_allow_policy = jsonencode({
    redrivePermission = "byQueue"
    sourceQueueArns   = [aws_sqs_queue.api_send.arn]
  })
}

resource "aws_sqs_queue" "api_status_dlq" {
  name                      = "${var.project}-mailer-api-status-dlq-${var.environment}"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
  tags                      = var.tags
}

resource "aws_sqs_queue" "api_status" {
  name                       = "${var.project}-mailer-api-status-${var.environment}"
  message_retention_seconds  = 1209600
  visibility_timeout_seconds = 180
  sqs_managed_sse_enabled    = true
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.api_status_dlq.arn
    maxReceiveCount     = 5
  })
  tags = var.tags
}

resource "aws_sqs_queue" "feedback_dlq" {
  name                      = "${var.project}-mailer-feedback-dlq-${var.environment}"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
  tags                      = var.tags
}

resource "aws_sqs_queue" "feedback" {
  name                       = "${var.project}-mailer-feedback-${var.environment}"
  message_retention_seconds  = 1209600
  visibility_timeout_seconds = 180
  sqs_managed_sse_enabled    = true
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.feedback_dlq.arn
    maxReceiveCount     = 5
  })
  tags = var.tags
}

# ─── SES feedback (data plane — kept; the ALERTING topic/alarms are not) ─────
# This SNS topic is SES's bounce/complaint/delivery notification channel,
# consumed by the feedback Lambda below. It is NOT the alerting topic PR3
# adds — that one pages a human; this one feeds the message-status pipeline
# and exists regardless of whether any alarm ever fires.

resource "aws_sns_topic" "feedback" {
  name = "${var.project}-mailer-feedback-${var.environment}"
  tags = var.tags
}

data "aws_iam_policy_document" "feedback_topic" {
  statement {
    sid       = "AllowSesPublish"
    effect    = "Allow"
    actions   = ["sns:Publish"]
    resources = [aws_sns_topic.feedback.arn]
    principals {
      type        = "Service"
      identifiers = ["ses.amazonaws.com"]
    }
    condition {
      test     = "StringEquals"
      variable = "AWS:SourceAccount"
      values   = [data.aws_caller_identity.current.account_id]
    }
  }
}

resource "aws_sns_topic_policy" "feedback" {
  arn    = aws_sns_topic.feedback.arn
  policy = data.aws_iam_policy_document.feedback_topic.json
}

data "aws_iam_policy_document" "feedback_queue" {
  statement {
    effect    = "Allow"
    actions   = ["sqs:SendMessage"]
    resources = [aws_sqs_queue.feedback.arn]
    principals {
      type        = "Service"
      identifiers = ["sns.amazonaws.com"]
    }
    condition {
      test     = "ArnEquals"
      variable = "aws:SourceArn"
      values   = [aws_sns_topic.feedback.arn]
    }
  }
}

resource "aws_sqs_queue_policy" "feedback" {
  queue_url = aws_sqs_queue.feedback.id
  policy    = data.aws_iam_policy_document.feedback_queue.json
}

resource "aws_sns_topic_subscription" "feedback" {
  topic_arn            = aws_sns_topic.feedback.arn
  protocol             = "sqs"
  endpoint             = aws_sqs_queue.feedback.arn
  raw_message_delivery = true
  depends_on           = [aws_sqs_queue_policy.feedback]
}

# One configuration set (see local.configuration_set) — Insolvia has only ever
# had one tenant/category family, unlike upstream's product/auth split.
resource "aws_sesv2_configuration_set" "mailer" {
  configuration_set_name = local.configuration_set
  reputation_options { reputation_metrics_enabled = true }
  sending_options { sending_enabled = true }
  suppression_options { suppressed_reasons = ["BOUNCE", "COMPLAINT"] }
}

resource "aws_sesv2_configuration_set_event_destination" "mailer" {
  configuration_set_name = aws_sesv2_configuration_set.mailer.configuration_set_name
  event_destination_name = "mailer-feedback"
  event_destination {
    enabled = true
    matching_event_types = [
      "SEND", "DELIVERY", "DELIVERY_DELAY", "BOUNCE", "COMPLAINT", "REJECT",
    ]
    sns_destination { topic_arn = aws_sns_topic.feedback.arn }
  }
  depends_on = [aws_sns_topic_policy.feedback]
}

# ─── Lambda roles ────────────────────────────────────────────────────────────

data "aws_iam_policy_document" "lambda_trust" {
  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "ingress" {
  name               = "${var.project}-mailer-ingress-role-${var.environment}"
  assume_role_policy = data.aws_iam_policy_document.lambda_trust.json
  tags               = var.tags
}

resource "aws_iam_role" "sender" {
  name               = "${var.project}-mailer-sender-role-${var.environment}"
  assume_role_policy = data.aws_iam_policy_document.lambda_trust.json
  tags               = var.tags
}

resource "aws_iam_role" "feedback" {
  name               = "${var.project}-mailer-feedback-role-${var.environment}"
  assume_role_policy = data.aws_iam_policy_document.lambda_trust.json
  tags               = var.tags
}

resource "aws_iam_role_policy_attachment" "ingress_basic" {
  role       = aws_iam_role.ingress.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

resource "aws_iam_role_policy_attachment" "sender_basic" {
  role       = aws_iam_role.sender.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

resource "aws_iam_role_policy_attachment" "feedback_basic" {
  role       = aws_iam_role.feedback.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

resource "aws_iam_role_policy" "ingress" {
  name = "${var.project}-mailer-ingress-${var.environment}"
  role = aws_iam_role.ingress.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:UpdateItem"]
        Resource = aws_dynamodb_table.messages.arn
      },
      {
        Effect   = "Allow"
        Action   = ["s3:GetObject", "s3:PutObject"]
        Resource = "${aws_s3_bucket.content.arn}/*"
      },
      {
        Effect   = "Allow"
        Action   = ["sqs:SendMessage"]
        Resource = aws_sqs_queue.api_send.arn
      },
    ]
  })
}

resource "aws_iam_role_policy" "sender" {
  name = "${var.project}-mailer-sender-${var.environment}"
  role = aws_iam_role.sender.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["dynamodb:GetItem", "dynamodb:UpdateItem"]
        Resource = [aws_dynamodb_table.messages.arn, aws_dynamodb_table.suppressions.arn]
      },
      {
        Effect   = "Allow"
        Action   = ["s3:GetObject", "s3:GetObjectTagging", "s3:DeleteObject"]
        Resource = "${aws_s3_bucket.content.arn}/*"
      },
      {
        Effect   = "Allow"
        Action   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes"]
        Resource = aws_sqs_queue.api_send.arn
      },
      {
        Effect   = "Allow"
        Action   = ["sqs:SendMessage"]
        Resource = aws_sqs_queue.api_status.arn
      },
      {
        Effect   = "Allow"
        Action   = ["ssm:GetParameter"]
        Resource = aws_ssm_parameter.api_sending_enabled.arn
      },
      # Scoped to the insolvia.ai identity ARN (built above) AND the exact
      # From address — tighter than upstream's Resource = "*". SendRawEmail is
      # what SESv2's send_email(Content={"Raw": ...}) actually maps to (the
      # sender always sends raw, per services/mailer sender_lambda.py);
      # SendEmail is granted alongside it for parity with upstream and in case
      # a non-raw send path is ever added.
      {
        Effect   = "Allow"
        Action   = ["ses:SendEmail", "ses:SendRawEmail"]
        Resource = local.ses_identity_arn
        Condition = {
          StringEquals = { "ses:FromAddress" = var.sender_address }
        }
      },
    ]
  })
}

resource "aws_iam_role_policy" "feedback" {
  name = "${var.project}-mailer-feedback-${var.environment}"
  role = aws_iam_role.feedback.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:UpdateItem"]
        Resource = [aws_dynamodb_table.messages.arn, aws_dynamodb_table.suppressions.arn]
      },
      {
        Effect   = "Allow"
        Action   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes"]
        Resource = aws_sqs_queue.feedback.arn
      },
      {
        Effect   = "Allow"
        Action   = ["sqs:SendMessage"]
        Resource = aws_sqs_queue.api_status.arn
      },
      {
        Effect   = "Allow"
        Action   = ["cloudwatch:PutMetricData"]
        Resource = "*"
        Condition = {
          StringEquals = { "cloudwatch:namespace" = "Mailer" }
        }
      },
    ]
  })
}

# ─── GuardDuty Malware Protection for S3 (optional, cost-gated) ─────────────
# See var.enable_attachment_scanning. Every resource in this section is
# count-gated on it and does not exist at all when it is false (the default).

data "aws_iam_policy_document" "guardduty_trust" {
  count = var.enable_attachment_scanning ? 1 : 0

  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["malware-protection-plan.guardduty.amazonaws.com"]
    }
    condition {
      test     = "StringEquals"
      variable = "aws:SourceAccount"
      values   = [data.aws_caller_identity.current.account_id]
    }
  }
}

resource "aws_iam_role" "guardduty" {
  count = var.enable_attachment_scanning ? 1 : 0

  name               = "${var.project}-mailer-guardduty-role-${var.environment}"
  assume_role_policy = data.aws_iam_policy_document.guardduty_trust[0].json
  tags               = var.tags
}

resource "aws_iam_role_policy" "guardduty" {
  count = var.enable_attachment_scanning ? 1 : 0

  name = "${var.project}-mailer-guardduty-${var.environment}"
  role = aws_iam_role.guardduty[0].id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = [
          "s3:GetBucketLocation", "s3:GetBucketNotification", "s3:PutBucketNotification",
          "s3:GetBucketTagging", "s3:ListBucket",
        ]
        Resource = aws_s3_bucket.content.arn
      },
      {
        Effect = "Allow"
        Action = [
          "s3:GetObject", "s3:GetObjectVersion", "s3:GetObjectTagging", "s3:PutObjectTagging",
        ]
        Resource = "${aws_s3_bucket.content.arn}/attachments/*"
      },
      {
        Effect = "Allow"
        Action = [
          "events:PutRule", "events:DeleteRule", "events:PutTargets", "events:RemoveTargets",
        ]
        Resource = "arn:aws:events:${data.aws_region.current.name}:${data.aws_caller_identity.current.account_id}:rule/DO-NOT-DELETE-AmazonGuardDutyMalwareProtectionS3*"
      },
      {
        Effect   = "Allow"
        Action   = ["events:DescribeRule", "events:ListTargetsByRule"]
        Resource = "*"
      },
      {
        Effect   = "Allow"
        Action   = ["guardduty:SendSecurityTelemetry"]
        Resource = "*"
      },
    ]
  })
}

resource "aws_guardduty_malware_protection_plan" "attachments" {
  count = var.enable_attachment_scanning ? 1 : 0

  role = aws_iam_role.guardduty[0].arn

  protected_resource {
    s3_bucket {
      bucket_name     = aws_s3_bucket.content.id
      object_prefixes = ["attachments/"]
    }
  }

  actions {
    tagging { status = "ENABLED" }
  }

  tags       = var.tags
  depends_on = [aws_iam_role_policy.guardduty, aws_s3_bucket_policy.content]
}

locals {
  guardduty_alarm_statuses = var.enable_attachment_scanning ? {
    threat      = "THREATS_FOUND"
    unsupported = "UNSUPPORTED"
    denied      = "ACCESS_DENIED"
    failed      = "FAILED"
  } : {}
}

resource "aws_cloudwatch_event_rule" "guardduty_result" {
  for_each = local.guardduty_alarm_statuses

  name = "${var.project}-mailer-guardduty-${each.key}-${var.environment}"
  event_pattern = jsonencode({
    source      = ["aws.guardduty"]
    detail-type = ["GuardDuty Malware Protection Object Scan Result"]
    detail = {
      s3ObjectDetails = {
        bucketName = [aws_s3_bucket.content.id]
      }
      scanResultDetails = {
        scanResultStatus = [each.value]
      }
    }
  })
  tags = var.tags
}

resource "aws_cloudwatch_event_target" "guardduty_result" {
  for_each = local.guardduty_alarm_statuses

  rule  = aws_cloudwatch_event_rule.guardduty_result[each.key].name
  arn   = aws_lambda_function.feedback.arn
  input = jsonencode({ guardduty_status = each.value })
}

resource "aws_lambda_permission" "guardduty_events" {
  for_each = local.guardduty_alarm_statuses

  statement_id  = "AllowGuardDutyEvent${title(each.key)}"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.feedback.function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.guardduty_result[each.key].arn
}

# ─── Lambda functions ────────────────────────────────────────────────────────
# One image, three entrypoints (services/mailer/Dockerfile's `lambda` target
# builds the same image for all three; image_config.command selects the
# handler). environment stays Terraform-managed (NOT ignored) — every value
# below is deterministic, computed from this module's own resources, so
# there's nothing for a deploy workflow to re-derive out-of-band the way
# api_service's SSM-sourced INSOLVIA_ENV/WAITLIST_TABLE_NAME are. Only
# image_uri is ignored, because the deploy workflow owns Lambda code updates.

resource "aws_lambda_function" "ingress" {
  function_name = "${var.project}-mailer-ingress-${var.environment}"
  role          = aws_iam_role.ingress.arn
  package_type  = "Image"
  image_uri     = "${aws_ecr_repository.mailer.repository_url}:latest"
  timeout       = 30
  memory_size   = 512
  image_config { command = ["insolvia_mailer.entrypoints.api_lambda.handler"] }
  environment {
    variables = {
      MAILER_CONTENT_BUCKET        = aws_s3_bucket.content.id
      MAILER_MESSAGES_TABLE        = aws_dynamodb_table.messages.name
      MAILER_SUPPRESSIONS_TABLE    = aws_dynamodb_table.suppressions.name
      MAILER_SERVICE_REGISTRY_JSON = local.service_registry_json
    }
  }
  lifecycle { ignore_changes = [image_uri] }
  tags       = var.tags
  depends_on = [aws_iam_role_policy_attachment.ingress_basic, aws_iam_role_policy.ingress]
}

resource "aws_lambda_function" "sender" {
  function_name                  = "${var.project}-mailer-sender-${var.environment}"
  role                           = aws_iam_role.sender.arn
  package_type                   = "Image"
  image_uri                      = "${aws_ecr_repository.mailer.repository_url}:latest"
  timeout                        = 120
  memory_size                    = 1024
  reserved_concurrent_executions = 5
  image_config { command = ["insolvia_mailer.entrypoints.sender_lambda.handler"] }
  environment {
    variables = {
      MAILER_CONTENT_BUCKET        = aws_s3_bucket.content.id
      MAILER_MESSAGES_TABLE        = aws_dynamodb_table.messages.name
      MAILER_SUPPRESSIONS_TABLE    = aws_dynamodb_table.suppressions.name
      MAILER_SERVICE_REGISTRY_JSON = local.service_registry_json
    }
  }
  lifecycle { ignore_changes = [image_uri] }
  tags       = var.tags
  depends_on = [aws_iam_role_policy_attachment.sender_basic, aws_iam_role_policy.sender]
}

resource "aws_lambda_function" "feedback" {
  function_name = "${var.project}-mailer-feedback-${var.environment}"
  role          = aws_iam_role.feedback.arn
  package_type  = "Image"
  image_uri     = "${aws_ecr_repository.mailer.repository_url}:latest"
  timeout       = 120
  memory_size   = 512
  image_config { command = ["insolvia_mailer.entrypoints.feedback_lambda.handler"] }
  environment {
    variables = {
      MAILER_CONTENT_BUCKET                  = aws_s3_bucket.content.id
      MAILER_MESSAGES_TABLE                  = aws_dynamodb_table.messages.name
      MAILER_SUPPRESSIONS_TABLE              = aws_dynamodb_table.suppressions.name
      MAILER_SERVICE_REGISTRY_JSON           = local.service_registry_json
      MAILER_CONFIGURATION_SET_REGISTRY_JSON = local.configuration_set_registry_json
    }
  }
  lifecycle { ignore_changes = [image_uri] }
  tags       = var.tags
  depends_on = [aws_iam_role_policy_attachment.feedback_basic, aws_iam_role_policy.feedback]
}

resource "aws_cloudwatch_log_group" "lambda" {
  for_each = {
    ingress  = aws_lambda_function.ingress.function_name
    sender   = aws_lambda_function.sender.function_name
    feedback = aws_lambda_function.feedback.function_name
  }
  name              = "/aws/lambda/${each.value}"
  retention_in_days = 14
  tags              = var.tags
}

resource "aws_lambda_event_source_mapping" "sender" {
  event_source_arn        = aws_sqs_queue.api_send.arn
  function_name           = aws_lambda_function.sender.arn
  batch_size              = 5
  function_response_types = ["ReportBatchItemFailures"]
}

resource "aws_lambda_event_source_mapping" "feedback" {
  event_source_arn        = aws_sqs_queue.feedback.arn
  function_name           = aws_lambda_function.feedback.arn
  batch_size              = 10
  function_response_types = ["ReportBatchItemFailures"]
}

# ─── IAM-authenticated HTTP API ───────────────────────────────────────────────
# Same shape as api_service's HTTP API (that module was built from this one's
# upstream original) — REGIONAL custom domain, no CloudFront, for the same
# reasons documented there. The one difference: every route here requires
# AWS_IAM auth, since the only legitimate callers are other Insolvia service
# Lambdas signing with SigV4, never a browser.

resource "aws_apigatewayv2_api" "mailer" {
  name                         = local.name
  protocol_type                = "HTTP"
  disable_execute_api_endpoint = true
  tags                         = var.tags
}

resource "aws_apigatewayv2_integration" "ingress" {
  api_id                 = aws_apigatewayv2_api.mailer.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.ingress.invoke_arn
  integration_method     = "POST"
  payload_format_version = "2.0"
  timeout_milliseconds   = 30000
}

resource "aws_apigatewayv2_route" "messages" {
  api_id             = aws_apigatewayv2_api.mailer.id
  route_key          = "POST /v1/services/{service_id}/messages"
  authorization_type = "AWS_IAM"
  target             = "integrations/${aws_apigatewayv2_integration.ingress.id}"
}

resource "aws_apigatewayv2_route" "attachment_uploads" {
  api_id             = aws_apigatewayv2_api.mailer.id
  route_key          = "POST /v1/services/{service_id}/attachment-uploads"
  authorization_type = "AWS_IAM"
  target             = "integrations/${aws_apigatewayv2_integration.ingress.id}"
}

resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.mailer.id
  name        = "$default"
  auto_deploy = true

  default_route_settings {
    throttling_burst_limit = 40
    throttling_rate_limit  = 20
  }

  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.api.arn
    format = jsonencode({
      requestId      = "$context.requestId"
      routeKey       = "$context.routeKey"
      status         = "$context.status"
      responseLength = "$context.responseLength"
      integrationMs  = "$context.integrationLatency"
    })
  }
  tags = var.tags
}

resource "aws_cloudwatch_log_group" "api" {
  name              = "/aws/apigateway/${local.name}"
  retention_in_days = 14
  tags              = var.tags
}

resource "aws_lambda_permission" "api" {
  statement_id  = "AllowApiGatewayInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.ingress.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.mailer.execution_arn}/*/*"
}

resource "aws_apigatewayv2_domain_name" "mailer" {
  domain_name = var.domain_name
  domain_name_configuration {
    certificate_arn = var.acm_certificate_arn
    endpoint_type   = "REGIONAL"
    security_policy = "TLS_1_2"
  }
  tags = var.tags
}

resource "aws_apigatewayv2_api_mapping" "mailer" {
  api_id      = aws_apigatewayv2_api.mailer.id
  domain_name = aws_apigatewayv2_domain_name.mailer.id
  stage       = aws_apigatewayv2_stage.default.id
}

resource "aws_route53_record" "mailer" {
  zone_id = var.hosted_zone_id
  name    = var.domain_name
  type    = "A"
  alias {
    name                   = aws_apigatewayv2_domain_name.mailer.domain_name_configuration[0].target_domain_name
    zone_id                = aws_apigatewayv2_domain_name.mailer.domain_name_configuration[0].hosted_zone_id
    evaluate_target_health = false
  }
}

# The one caller's grant: execute-api:Invoke on exactly the two routes
# insolvia_api is allowed to hit. De-humbugged from
# `aws_iam_role_policy.humbugg_invoke`; `data.aws_iam_role.caller` (looked up
# by var.caller_role_name) replaces upstream's hard-coded humbugg role lookup.
resource "aws_iam_role_policy" "api_invoke" {
  name = "invoke-${local.name}"
  role = data.aws_iam_role.caller.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect = "Allow"
      Action = "execute-api:Invoke"
      Resource = [
        "${aws_apigatewayv2_api.mailer.execution_arn}/*/POST/v1/services/insolvia_api/messages",
        "${aws_apigatewayv2_api.mailer.execution_arn}/*/POST/v1/services/insolvia_api/attachment-uploads",
      ]
    }]
  })
}

# ─── Configuration ────────────────────────────────────────────────────────────
# De-humbugged from `humbugg_exchange_email_enabled`: one kill switch per
# caller, generically named for the tenant it gates. sender_lambda._enabled()
# reads this parameter's value ("true"/"1"/"yes"/"enabled" all mean go) before
# every send.

resource "aws_ssm_parameter" "api_sending_enabled" {
  name  = "/${var.project}/${var.environment}/mailer/insolvia-api-sending-enabled"
  type  = "String"
  value = "true"
  tags  = var.tags
}
