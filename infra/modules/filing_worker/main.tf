# ── The filing worker (ADR 0024 PR 7) ──────────────────────────
# The filing worker as an image Lambda consuming the filing queue — since the
# switch (ADR 0028, plan 18.22) backend/src/Insolvia.Filing in the VPC's
# private subnets, logging in to Aurora as the `filing` Postgres role (the
# vault's SELECT is that role's alone); services/filing before — the kill
# switch it reads before every submission, and EVERY grant its role holds on
# the case data — in one module, so "what can the filing worker touch" is one
# file to read and one test (tests/filing_worker.tftest.hcl) pins it whole.
#
# The role itself is modules/filing_credentials' (the vault key's policy must
# name it, so it is created there, ahead of the service) and arrives here by
# name and ARN. What it holds, all told:
#
#   from modules/filing_credentials   the vault: Decrypt under the vault
#                                     purpose (PR 4)
#   from modules/filing_queue         consume on the filing queue (PR 6)
#   from THIS module                  the documents bucket, the tax-id open
#                                     (Case Upload, PR 9), the kill switch,
#                                     its own logs, the VPC's network
#                                     interfaces and rds-db:connect as
#                                     `filing` (modules/vpc_lambda_access)
#
# No DynamoDB verb anywhere since ADR 0028: the case partition the worker
# reads and the filing records it writes are Aurora rows, reached as the
# `filing` Postgres role.
#
# The narrowest role in the account (ADR 0024, Risks): a compromise of it
# defeats the approval check, so each statement below says what it is for.
#
# ── In dev ──────────────────────────────────────────────────────
# ecr_repository_url = null: no Lambda, no event source mapping, no alarms —
# the local poller (services/filing entrypoints/filing_poller.py) consumes the
# real per-machine queue AS THIS ROLE (filing_credentials trusts the developer
# to assume it in dev only), so every grant below is exercised on a laptop.
#
# ── Bootstrap order (the first apply in a fresh account) ────────
# Like every image Lambda here: insolvia-services' infra/envs/shared applies
# first (creates insolvia-shared-filing-dotnet), run `scripts/bootstrap-ecr-images.sh <env> filing`,
# then apply the env. Later deploys are filing-<env>.yml pushing an image and
# calling update-function-code; Terraform ignores the image drift.

data "aws_region" "current" {}
data "aws_caller_identity" "current" {}

locals {
  # insolvia-<env>-filing — the component is the service. The function is
  # `-worker`, the kill switch an SSM parameter in the service's namespace.
  name          = "${var.project}-${var.environment}-filing"
  function_name = "${local.name}-worker"

  # The Postgres role this host logs in as — modules/api_service's
  # db_username note. The Lambda's environment below is Terraform's own here
  # (names only, no secret), so the literal is used directly.
  db_username = "filing"

  worker_count = var.ecr_repository_url == null ? 0 : 1
  alarm_count  = var.ecr_repository_url != null && var.alarms_topic_arn != null ? 1 : 0

  # ADR 0024: "one flag the maintainer can flip — refuses every submission in
  # that environment without a deploy". The worker reads it fresh at the start
  # of every run and again immediately before the final submit; only "true"
  # means on (adapters/aws/kill_switch.py fails closed).
  kill_switch_name = "/${var.project}/${var.environment}/filing/submissions-enabled"
  kill_switch_arn  = "arn:aws:ssm:${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}:parameter${local.kill_switch_name}"

  region = data.aws_region.current.region

  # The tax-id envelope's encryption-context purpose — must equal
  # insolvia_core.tax_ids.TAX_ID_PURPOSE and modules/case_store's
  # `tax_id_purpose`. A rename on one side does not fail an apply; every
  # Case Upload open starts failing with AccessDenied.
  tax_id_purpose = "debtor-tax-id"

  worker_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        # OPEN a debtor's sealed tax id, for the Case Upload file (ADR 0024
        # PR 9): Debtor.txt carries the full SSN, and the worker builds it in
        # memory at upload time and never stores it. Decrypt ALONE (it never
        # seals one, so no GenerateDataKey), and only under the tax-id
        # envelope's encryption context — the same fence as
        # modules/case_store's TaxIdKeyUse grants the API and the pipeline
        # worker, so this cannot decrypt a case document, a packet or the
        # vault. Every use is a `taxid.read` access row (purpose
        # `case_upload`) written by the code before it calls KMS.
        Sid      = "TaxIdOpen"
        Effect   = "Allow"
        Action   = ["kms:Decrypt"]
        Resource = var.case_kms_key_arn
        Condition = {
          StringEquals = { "kms:EncryptionContext:purpose" = local.tax_id_purpose }
        }
      },
      {
        # READ the approved packet's zip — the bytes it uploads, each checked
        # against the SHA-256 the approval bound. Packets only: the worker
        # never reads an uploaded source document.
        Sid      = "PacketRead"
        Effect   = "Allow"
        Action   = ["s3:GetObject"]
        Resource = "${var.case_document_bucket_arn}/cases/*/packets/*"
      },
      {
        # WRITE what the court sent back — the confirmation page and the
        # receipt PDF — under the filing's own prefix
        # (services/filing core/receipt.filing_object_key). Never a source
        # document's key, never a packet's.
        Sid      = "FilingReceiptWrite"
        Effect   = "Allow"
        Action   = ["s3:PutObject"]
        Resource = "${var.case_document_bucket_arn}/cases/*/filings/*"
      },
      {
        # The bucket's key, through S3 only: Decrypt for the packet read,
        # GenerateDataKey for the receipt write.
        Sid      = "CaseKeyThroughS3"
        Effect   = "Allow"
        Action   = ["kms:Decrypt", "kms:GenerateDataKey", "kms:DescribeKey"]
        Resource = var.case_kms_key_arn
        Condition = {
          StringEquals = { "kms:ViaService" = "s3.${local.region}.amazonaws.com" }
        }
      },
      {
        # The kill switch, read-only. Who may FLIP it is a human's IAM, not
        # this role's.
        Sid      = "KillSwitchRead"
        Effect   = "Allow"
        Action   = ["ssm:GetParameter"]
        Resource = local.kill_switch_arn
      },
    ]
  })
}

# ── The kill switch ─────────────────────────────────────────────
# Created OFF in every environment and never reverted by an apply: Terraform
# owns that the parameter exists, a human owns its value (ignore_changes).
# Prod also has no allowlisted court host (services/filing core/fence.py) — two
# independent reasons its worker hands back every job until ADR 0024 PR 10.
resource "aws_ssm_parameter" "kill_switch" {
  name        = local.kill_switch_name
  description = "Filing kill switch (ADR 0024): \"true\" lets the filing worker submit; anything else refuses every submission. Flip by hand; Terraform ignores the value."
  type        = "String"
  value       = "false"
  tags        = var.tags

  lifecycle {
    ignore_changes = [value]
  }
}

resource "aws_iam_role_policy" "worker" {
  name   = "${local.name}-worker-access"
  role   = var.worker_role_name
  policy = local.worker_policy
}

# ── The Lambda (deployed environments only) ─────────────────────

resource "aws_iam_role_policy_attachment" "worker_logs" {
  count = local.worker_count

  role       = var.worker_role_name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

# The VPC's network interfaces and rds-db:connect as `filing`.
module "vpc_access" {
  source = "../vpc_lambda_access"
  count  = local.worker_count

  name                   = local.function_name
  role_name              = var.worker_role_name
  db_username            = local.db_username
  db_cluster_resource_id = var.db_cluster_resource_id
}

resource "aws_lambda_function" "worker" {
  count = local.worker_count

  function_name = local.function_name
  role          = var.worker_role_arn
  package_type  = "Image"
  image_uri     = "${var.ecr_repository_url}:${var.image_tag}"
  # The ceiling on one filing attempt, and the worker's lease
  # (services/filing core/config.DEFAULT_LEASE_SECONDS — the two MUST agree):
  # a redelivery finding a claim older than this knows its attempt is dead.
  # Under the queue's visibility timeout (modules/filing_queue, 900), so a
  # message is never redelivered while its attempt can still be running.
  timeout     = var.timeout_seconds
  memory_size = var.memory_mb

  # The private subnets: Aurora, S3 through its gateway endpoint, KMS and SSM
  # through the NAT — and the courts, through the NAT's stable address
  # (modules/network's nat_public_ip, what a court would allow-list).
  vpc_config {
    subnet_ids         = coalesce(var.vpc_subnet_ids, [])
    security_group_ids = coalesce(var.vpc_security_group_ids, [])
  }

  # What backend/src/Insolvia.Filing reads (FilingConfig, FilingComposition,
  # Insolvia.Data's DatabaseSettings). AWS_REGION is Lambda's own reserved
  # variable and cannot be set here; the runtime provides it.
  environment {
    variables = {
      INSOLVIA_ENV                  = var.insolvia_env
      CASE_DOCUMENT_BUCKET          = var.case_document_bucket_name
      FILING_KILL_SWITCH_PARAMETER  = local.kill_switch_name
      INSOLVIA_DB_ENDPOINT          = coalesce(var.db_endpoint, "unset")
      INSOLVIA_DB_PORT              = tostring(var.db_port)
      INSOLVIA_DB_DATABASE_NAME     = coalesce(var.db_database_name, "unset")
      INSOLVIA_DB_USERNAME          = local.db_username
      INSOLVIA_KMS_CASE_KEY_ALIAS   = coalesce(var.case_kms_key_alias, "unset")
      INSOLVIA_KMS_FILING_KEY_ALIAS = coalesce(var.filing_kms_key_alias, "unset")
    }
  }

  # The deploy workflow owns the image (filing-<env>.yml); Terraform owns the
  # environment, which holds names only — no secret ever rides here.
  lifecycle {
    ignore_changes = [image_uri]

    precondition {
      condition     = var.vpc_subnet_ids != null && var.vpc_security_group_ids != null && var.db_cluster_resource_id != null && var.db_endpoint != null && var.db_database_name != null && var.case_kms_key_alias != null && var.filing_kms_key_alias != null
      error_message = "A deployed filing worker runs in the VPC against Aurora: pass the vpc_*, db_* and *_kms_key_alias inputs (module.network, module.aurora, the key aliases)."
    }
  }

  tags = var.tags
  depends_on = [
    aws_iam_role_policy_attachment.worker_logs,
    aws_iam_role_policy.worker,
    module.vpc_access,
  ]
}

resource "aws_cloudwatch_log_group" "worker" {
  count = local.worker_count

  name              = "/aws/lambda/${local.function_name}"
  retention_in_days = 30
  tags              = var.tags
}

# Batch size 1 and at most two concurrent invocations: a filing is one
# court session, and nothing gains from a burst against a court — while two,
# the event-source-mapping minimum, keeps one stuck run from holding the
# queue. Two consumers of ONE message is safe by construction (the record's
# conditional claim).
resource "aws_lambda_event_source_mapping" "filing" {
  count = local.worker_count

  event_source_arn = var.queue_arn
  function_name    = aws_lambda_function.worker[0].arn
  batch_size       = 1

  scaling_config {
    maximum_concurrency = 2
  }
}

# ── Alarms ──────────────────────────────────────────────────────
# Three, because each is a human's job: a job that never ran (DLQ), a worker
# that raised, and a filing nobody can know the outcome of — `outcome_unknown`
# is never retried, so the alarm is what makes somebody reconcile it.

resource "aws_cloudwatch_metric_alarm" "dlq_depth" {
  count = local.alarm_count

  alarm_name          = "${local.name}-dlq-depth"
  alarm_description   = "A filing job exhausted its deliveries without a filing record deciding it — read the DLQ."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "ApproximateNumberOfMessagesVisible"
  namespace           = "AWS/SQS"
  period              = 300
  statistic           = "Maximum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { QueueName = var.dlq_name }
  alarm_actions       = [var.alarms_topic_arn]
  ok_actions          = [var.alarms_topic_arn]
  tags                = var.tags
}

resource "aws_cloudwatch_metric_alarm" "worker_errors" {
  count = local.alarm_count

  alarm_name          = "${local.name}-worker-errors"
  alarm_description   = "The filing worker raised before a filing record existed."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "Errors"
  namespace           = "AWS/Lambda"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { FunctionName = aws_lambda_function.worker[0].function_name }
  alarm_actions       = [var.alarms_topic_arn]
  ok_actions          = [var.alarms_topic_arn]
  tags                = var.tags
}

resource "aws_cloudwatch_log_metric_filter" "outcome_unknown" {
  count = local.alarm_count

  name           = "${local.name}-outcome-unknown"
  log_group_name = aws_cloudwatch_log_group.worker[0].name
  pattern        = "{ $.outcome = \"outcome_unknown\" }"

  metric_transformation {
    name      = "FilingOutcomeUnknown"
    namespace = "Insolvia/Filing"
    value     = "1"
  }
}

resource "aws_cloudwatch_metric_alarm" "outcome_unknown" {
  count = local.alarm_count

  alarm_name          = "${local.name}-outcome-unknown"
  alarm_description   = "A filing stopped after the final-submit mark: the court may have it. Never retried — reconcile against the court's own case query."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "FilingOutcomeUnknown"
  namespace           = "Insolvia/Filing"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  alarm_actions       = [var.alarms_topic_arn]
  tags                = var.tags
}
