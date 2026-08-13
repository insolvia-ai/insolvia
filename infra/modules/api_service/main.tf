# The Insolvia backend API (services/api): a Flask+Mangum Docker Lambda behind
# an API Gateway HTTP API, one instance per environment (#62, #63). Owns the
# whole per-env API stack: Lambda + execution role, HTTP API +
# custom domain + DNS, the waitlist DynamoDB table
# (per docs/adr/0001), the /insolvia/<env>/api SSM config
# namespace (#70), and the CloudWatch alarms + SNS topic (#69).
#
# The house pattern for a Mangum service: HTTP API with a $default route to the
# Lambda, payload format 2.0, regional custom domain, no CloudFront. Issue #62's
# title says
# "CloudFront + API GW", but the mailer precedent and an API's actual needs
# say otherwise — see the custom-domain section below for the deviation note.
#
# ── Bootstrap order (read before the FIRST apply in a fresh account) ────────
# An Image-package Lambda cannot be created without an existing image:
# `aws_lambda_function` fails ("Source image ... does not exist") until one has
# been pushed. The repository itself is not part of the deadlock — it
# lives in infra/envs/shared and is applied before any environment (see the
# deployment order in docs/reference/terraform.md) — so the cycle is
# just "seed an image, then apply", once per environment:
#
#   1. apply infra/envs/shared (creates insolvia-shared-api)
#   2. build services/api
#      (`docker build --platform linux/amd64 --provenance=false --target lambda`),
#      tag it <repo-url>:<env> — this environment's moving marker tag, which is
#      what `var.image_tag` seeds the Lambda from; there is no `:latest`,
#      because under a shared repository it would mean "whatever any
#      environment pushed last" — and push it. Both flags matter when building
#      locally: the Lambda is x86_64, so an Apple Silicon default build ships
#      an arm64 image; and Docker Desktop's provenance attestations produce an
#      OCI index that CreateFunction rejects with "The image manifest, config
#      or layer media type ... is not supported". (CI's plain BuildKit on
#      amd64 runners emits neither, which is why the workflow build needs no
#      flags.)
#   3. terraform apply   (full)
#
# Every later deploy is just push-then-update-function-code; Terraform ignores
# the image drift (see the lifecycle note on the Lambda).

locals {
  # insolvia-<env>-api — the Lambda/API/alarm name stem. Environment is the
  # SECOND segment, per the insolvia-aws-naming skill; `api` is the component
  # because that is the surface this serves (api.insolvia.ai).
  name = "${var.project}-${var.environment}-api"

  # The per-environment API config namespace (#70): /<project>/<env>/..., with
  # an /api segment so later services can claim sibling namespaces.
  ssm_prefix = "/${var.project}/${var.environment}/api"
}

# ── Container repository ────────────────────────────────────────
# This module does NOT own a repository. `insolvia-shared-api` is a single repo
# shared by every environment, created in infra/envs/shared and passed in as
# `var.ecr_repository_url`.
#
# One repo per environment ("so a prod deploy can never pick up a staging
# build") is deliberately rejected — a shared repo is the whole point
# of the promotion pipeline: prod deploys the exact digest staging validated,
# which requires one place both environments can name it. Environment
# isolation lives elsewhere — separate Lambdas, roles, tables, SSM
# namespaces and Cognito pools — not in separate image stores. The image is
# environment-agnostic by construction: this service reads its entire
# environment from SSM at deploy time (see the Lambda's lifecycle note), so
# nothing environment-specific is baked into a layer.
#
# See infra/envs/shared/main.tf for the repository and its retention policy.

# ── One-time state migration (transitional) ─────────────────────
# The two resources above were DELETED from config, but they still live in the
# staging and prod state files until each has applied once. Deleting the config
# without these blocks would make the next apply plan a real DESTROY of
# insolvia-api-<env> — a repository that still holds the image the RUNNING
# Lambda pulls from. `aws_ecr_repository` has no `force_delete`, so that
# destroy fails on RepositoryNotEmptyException and leaves a half-applied env.
#
# `removed { ... destroy = false }` makes it a state-only forget: Terraform
# drops the resource without calling AWS, so no ecr:DeleteRepository is needed
# and CI can run it. The live repositories stay put, still holding the previous
# images — which is the rollback path until each environment has redeployed
# from the shared repo.
#
# Declared in the MODULE, so one block drains both staging's and prod's state.
# Mirrors the ci-trust extraction in infra/envs/shared/main.tf.
#
# These blocks are one-time. Delete them ONLY after BOTH staging and prod have
# applied — removing them while prod's state still holds the repo puts the
# DESTROY back. The orphaned insolvia-api-<env> repositories are then deleted
# out of band, after a soak.
removed {
  from = aws_ecr_repository.api
  lifecycle {
    destroy = false
  }
}

removed {
  from = aws_ecr_lifecycle_policy.api
  lifecycle {
    destroy = false
  }
}

# ── Waitlist storage ────────────────────────────────────────────
# The API owns the waitlist table: docs/adr/0001 makes the API the only
# application principal with data-store access (an SSR Lambda holding a
# DynamoDB grant was reviewed and rejected). Named insolvia-<env>-waitlist —
# deliberately NOT insolvia-marketing-waitlist-*: the table belongs to the
# API, not to any one client of it.
#
# Schema matches insolvia_api.core.waitlist.record_item exactly: constant
# "WAITLIST" partition, "<submittedAt>#<id>" sort key, so rows read back
# time-ordered with a single Query. Separate tables per environment is a #63
# requirement: staging must never be able to read (or pollute) prod.

resource "aws_dynamodb_table" "waitlist" {
  name         = "${var.project}-${var.environment}-waitlist"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "PK"
  range_key    = "SK"

  attribute {
    name = "PK"
    type = "S"
  }

  attribute {
    name = "SK"
    type = "S"
  }

  point_in_time_recovery { enabled = true }
  server_side_encryption { enabled = true }
  tags = var.tags
}

# ── Lambda execution role ───────────────────────────────────────

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

resource "aws_iam_role" "api" {
  name               = "${local.name}-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_trust.json
  tags               = var.tags
}

resource "aws_iam_role_policy_attachment" "api_basic" {
  role       = aws_iam_role.api.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

# PutItem ONLY, on this environment's table only. The waitlist is append-only
# by design: the service records signups and nothing else, so the role gets no
# read, update, or delete — a compromised API cannot enumerate the list, and
# staging can never reach the prod table because each environment's role names
# exactly its own table ARN (#63).
#
# When the service starts reading runtime secrets from SSM (#65/#70), grant
# ssm:GetParameter here on the specific parameters it reads — per-parameter,
# like the mailer's kill-switch grant, not the whole ${local.ssm_prefix} tree.
resource "aws_iam_role_policy" "api" {
  # `-data` is the GRANT this policy carries, per the naming skill's IAM policy
  # pattern ([project]-[env]-[component]-[grant]). "-policy" said only that a
  # policy is a policy.
  name = "${local.name}-data"
  role = aws_iam_role.api.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["dynamodb:PutItem"]
        Resource = aws_dynamodb_table.waitlist.arn
      },
    ]
  })
}

# ── Lambda function ─────────────────────────────────────────────

resource "aws_lambda_function" "api" {
  function_name = local.name
  role          = aws_iam_role.api.arn
  package_type  = "Image"
  # The per-environment marker tag, not `:latest`. Under a shared repository
  # `:latest` would mean "whatever any environment pushed last", so a
  # from-scratch prod apply would seed the prod Lambda from a staging build —
  # a promotion-invariant violation in the one code path nobody watches.
  # `staging` / `prod` are moving tags CI repoints at each deploy, so this seed
  # means "the last image THIS environment ran".
  image_uri   = "${var.ecr_repository_url}:${var.image_tag}"
  timeout     = 30
  memory_size = 512

  # Publish a numbered version on every Terraform-driven change, so the alias
  # below always has a real version to point at (an alias cannot target
  # $LATEST). The deploy workflow publishes its own versions with
  # `update-function-code --publish`.
  publish = true

  environment {
    variables = {
      INSOLVIA_ENV        = var.insolvia_env
      WAITLIST_TABLE_NAME = aws_dynamodb_table.waitlist.name
    }
  }

  # The deploy workflow owns BOTH image_uri and environment, so Terraform must
  # ignore drift on both (issue #62's hard-won rule, straight from the mailer).
  # This deliberately differs from the marketing site's Lambda, where Terraform
  # keeps `environment`: here every value the workflow injects comes from the
  # SSM parameters this module writes below (#70), so the workflow re-deriving
  # the environment on each deploy is drift-safe — Terraform still owns the
  # values, just one indirection away. The block above is only the seed for
  # the very first apply.
  lifecycle { ignore_changes = [image_uri, environment] }

  tags       = var.tags
  depends_on = [aws_iam_role_policy_attachment.api_basic, aws_iam_role_policy.api]
}

resource "aws_cloudwatch_log_group" "lambda" {
  name              = "/aws/lambda/${aws_lambda_function.api.function_name}"
  retention_in_days = 14
  tags              = var.tags
}

# ── Blue/green alias ────────────────────────────────────────────
# `live` is what API Gateway invokes. The deploy workflow publishes a new
# version, smoke-tests THAT version directly by ARN, and only then repoints
# this alias — so a failed smoke test leaves the previous version serving
# instead of leaving a broken build live (deploy-first-test-after would have
# no way back).
#
# It also makes rollback near-instant and ECR-independent: a published version
# is an immutable snapshot Lambda stores itself, so
# `aws lambda update-alias --function-version <previous>` reverts in seconds
# with no image pull. (That does NOT relax the shared repo's retention rule —
# re-deploying an old commit still needs its image.)
resource "aws_lambda_alias" "live" {
  name             = "live"
  description      = "The version serving traffic. function_version is owned by the deploy workflow."
  function_name    = aws_lambda_function.api.function_name
  function_version = aws_lambda_function.api.version

  # MANDATORY, not defensive. Terraform never sees the workflow's publishes
  # (image_uri is ignored above), so its copy of `version` is stale by
  # construction. Without this, every apply would yank the alias back to an old
  # version — and since each deploy workflow applies BEFORE it pushes, that is
  # a self-inflicted rollback on literally every deploy.
  lifecycle {
    ignore_changes = [function_version]
  }
}

# ── HTTP API ────────────────────────────────────────────────────
# $default route -> the Lambda, payload format 2.0 — what Mangum consumes.
# Flask owns all routing, so API Gateway stays a dumb front door. Everything is
# public today (/health, POST /v1/waitlist); Cognito authorizers arrive with
# #65. The execute-api endpoint is disabled so the custom domain below is the
# only way in — one hostname to allowlist, throttle, and reason about.

resource "aws_apigatewayv2_api" "api" {
  name                         = local.name
  protocol_type                = "HTTP"
  disable_execute_api_endpoint = true
  tags                         = var.tags
}

resource "aws_apigatewayv2_integration" "lambda" {
  api_id                 = aws_apigatewayv2_api.api.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_alias.live.invoke_arn
  integration_method     = "POST"
  payload_format_version = "2.0"
  timeout_milliseconds   = 30000

  # Terraform infers NO dependency between an integration and a permission.
  # Without this, the same apply can repoint the integration at the alias
  # before the alias-qualified permission exists — a burst of real 500s that
  # the plan gives no hint of.
  depends_on = [aws_lambda_permission.api_live]
}

resource "aws_apigatewayv2_route" "default" {
  api_id    = aws_apigatewayv2_api.api.id
  route_key = "$default"
  target    = "integrations/${aws_apigatewayv2_integration.lambda.id}"
}

resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.api.id
  name        = "$default"
  auto_deploy = true

  # Stage-wide throttling is the unauthenticated waitlist endpoint's abuse
  # control: 20 requests/second sustained with bursts to 40 is orders of
  # magnitude above real marketing traffic, but caps a scripted flood before it
  # runs up Lambda concurrency or fills the table. Excess requests get 429
  # before ever invoking the Lambda. Same numbers as the mailer's stage.
  default_route_settings {
    throttling_burst_limit = 40
    throttling_rate_limit  = 20
  }

  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.api_access.arn
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

resource "aws_cloudwatch_log_group" "api_access" {
  name              = "/aws/apigateway/${local.name}"
  retention_in_days = 14
  tags              = var.tags
}

# The ORIGINAL unqualified permission. Superseded by `api_live` below, but kept
# until every environment serves through the alias — adding `qualifier` to this
# resource would force replacement, and Terraform's destroy-then-create leaves a
# window with no permission at all. A second resource with a different
# statement_id has no such window. Delete this once both environments are on the
# alias.
resource "aws_lambda_permission" "api" {
  statement_id  = "AllowApiGatewayInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.api.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.api.execution_arn}/*/*"
}

# Lambda evaluates the ALIAS's own resource policy, so a grant on the
# unqualified function ARN does not authorize invoking <function>:live. Without
# this, flipping the integration to the alias returns 500s immediately.
resource "aws_lambda_permission" "api_live" {
  statement_id  = "AllowApiGatewayInvokeLive"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.api.function_name
  qualifier     = aws_lambda_alias.live.name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.api.execution_arn}/*/*"
}

# ── Custom domain + DNS ─────────────────────────────────────────
# API Gateway custom domain with a Route53 alias, directly — NO CloudFront in
# front, deviating from issue #62's title. The mailer (the reference for this
# module) fronts its API the same way, and an API gains nothing from an edge
# cache: responses are uncacheable POSTs and per-caller GETs, TLS and DNS are
# already handled here, and throttling lives on the stage above. CloudFront
# would add a hop, a second cache config to reason about, and no capability.
#
# Cert note: a REGIONAL API Gateway domain needs its ACM cert in the API's own
# region — unlike CloudFront, which demands us-east-1 regardless of where the
# origin runs. Everything in this account is us-east-1 (see
# docs/reference/terraform.md), so the shared *.insolvia.ai wildcard
# satisfies both consumers and the envs reuse the exact same
# data.aws_acm_certificate lookup they already had for CloudFront. If the API
# ever moves region, it needs a wildcard cert issued in that region — the
# lookup, not just the reference, has to move with it.

resource "aws_apigatewayv2_domain_name" "api" {
  domain_name = var.domain_name

  domain_name_configuration {
    certificate_arn = var.acm_certificate_arn
    endpoint_type   = "REGIONAL"
    security_policy = "TLS_1_2"
  }

  tags = var.tags
}

resource "aws_apigatewayv2_api_mapping" "api" {
  api_id      = aws_apigatewayv2_api.api.id
  domain_name = aws_apigatewayv2_domain_name.api.id
  stage       = aws_apigatewayv2_stage.default.id
}

resource "aws_route53_record" "api" {
  zone_id = var.hosted_zone_id
  name    = var.domain_name
  type    = "A"

  alias {
    name                   = aws_apigatewayv2_domain_name.api.domain_name_configuration[0].target_domain_name
    zone_id                = aws_apigatewayv2_domain_name.api.domain_name_configuration[0].hosted_zone_id
    evaluate_target_health = false
  }
}

# ── Configuration namespace (#70) ───────────────────────────────
# Convention: every API config value lives at /insolvia/<env>/api/<key>. The
# deploy workflow resolves this namespace and injects it into the Lambda
# environment (which Terraform ignores above), so config changes ship without
# a Terraform apply. Only values the service actually reads today are created
# — the namespace is the contract, not a parameter graveyard:
#
#   insolvia-env         -> INSOLVIA_ENV (staging|production; note prod's
#                           infra env name is "prod" but the app-level value
#                           is "production" — this parameter is where that
#                           mapping is authoritatively recorded)
#   waitlist-table-name  -> WAITLIST_TABLE_NAME
#   unsubscribe-secret   -> UNSUBSCRIBE_SECRET (SecureString, below)
#
# Future secrets (#65/#70 consumers) slot in as SecureString siblings with
# `lifecycle { ignore_changes = [value] }` — Terraform creates the slot, a human
# or CI owns the value, and nothing secret is ever committed.

resource "aws_ssm_parameter" "config" {
  for_each = {
    "insolvia-env"        = var.insolvia_env
    "waitlist-table-name" = aws_dynamodb_table.waitlist.name
  }

  name  = "${local.ssm_prefix}/${each.key}"
  type  = "String"
  value = each.value
  tags  = var.tags
}

# ── Unsubscribe signing key (#80) ───────────────────────────────
# HMAC key for the tokens in every transactional email's unsubscribe link
# (services/api core/unsubscribe.py). Only this service ever holds it: it
# mints the tokens and it verifies them. The mailer neither has it nor needs
# it — it trusts the API's SigV4 identity and records the outcome.
#
# Generated rather than human-supplied, unlike the "a human or CI owns the
# value" pattern above, and the trade-off is deliberate. Generated means the
# value lands in Terraform state (S3, encrypted, and this repo's state bucket
# is not public); human-supplied would mean the SES production-access request
# blocks on somebody remembering to set a parameter, and a missing key makes
# every send drop its unsubscribe link. For a key whose entire authority is
# "may stop mail to one address", unattended-and-present beats
# manual-and-maybe-absent.
#
# ROTATION invalidates every unsubscribe link already sitting in someone's
# inbox — verification is exact, and old tokens stop verifying the moment the
# key changes. So do not rotate casually: an unsubscribe link that has stopped
# working is a compliance problem. `keepers` is empty for exactly that reason,
# and `ignore_changes` keeps a manual rotation (a new value written straight
# to SSM) from being reverted by the next apply.
resource "random_password" "unsubscribe_secret" {
  length  = 64
  special = false
}

resource "aws_ssm_parameter" "unsubscribe_secret" {
  name  = "${local.ssm_prefix}/unsubscribe-secret"
  type  = "SecureString"
  value = random_password.unsubscribe_secret.result
  tags  = var.tags

  lifecycle {
    ignore_changes = [value]
  }
}

# ── Alarms (#69) ────────────────────────────────────────────────
# One SNS topic per environment as the alarm target. Subscriptions are NOT
# managed here: an email subscription needs a human to click the confirmation
# link (a Terraform-managed one would sit "pending" forever), and this repo is
# public — it commits no real addresses (see CLAUDE.md). Subscribe by hand
# once, against the topic ARN in this module's outputs.

resource "aws_sns_topic" "alarms" {
  name = "${local.name}-alarms"
  tags = var.tags
}

# Any Lambda error is worth a look: the service catches expected failures
# (validation, CORS) and returns 4xx, so an Errors datapoint means an
# unhandled exception or a crashed runtime.
resource "aws_cloudwatch_metric_alarm" "lambda_errors" {
  alarm_name          = "${local.name}-lambda-errors"
  alarm_description   = "The API Lambda raised an unhandled error."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "Errors"
  namespace           = "AWS/Lambda"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { FunctionName = aws_lambda_function.api.function_name }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}

# Throttles fire when the account/function concurrency ceiling is hit —
# either real load the stage throttling let through, or another function
# starving this one. Never expected at this service's traffic.
resource "aws_cloudwatch_metric_alarm" "lambda_throttles" {
  alarm_name          = "${local.name}-lambda-throttles"
  alarm_description   = "The API Lambda is being throttled — requests are failing before the handler runs."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "Throttles"
  namespace           = "AWS/Lambda"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { FunctionName = aws_lambda_function.api.function_name }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}

# HTTP API server errors. Note the metric name really is "5xx" — HTTP APIs
# (v2) use "5xx"/"4xx", unlike REST APIs' "5XXError".
resource "aws_cloudwatch_metric_alarm" "api_5xx" {
  alarm_name          = "${local.name}-5xx"
  alarm_description   = "The HTTP API returned server errors."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "5xx"
  namespace           = "AWS/ApiGateway"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { ApiId = aws_apigatewayv2_api.api.id }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}

# p99 end-to-end latency. 2s is generous for a Flask handler doing one PutItem
# — the headroom is for cold starts, which land in the p99 at this traffic
# level. Two of three periods must breach so a single cold-start spike in an
# otherwise idle 5-minute window doesn't page.
resource "aws_cloudwatch_metric_alarm" "api_p99_latency" {
  alarm_name          = "${local.name}-p99-latency"
  alarm_description   = "The HTTP API's p99 latency is sustained above 2 seconds."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 3
  datapoints_to_alarm = 2
  metric_name         = "Latency"
  namespace           = "AWS/ApiGateway"
  period              = 300
  extended_statistic  = "p99"
  threshold           = 2000
  treat_missing_data  = "notBreaching"
  dimensions          = { ApiId = aws_apigatewayv2_api.api.id }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}
