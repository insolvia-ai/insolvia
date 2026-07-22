# The Insolvia backend API (services/api): a Flask+Mangum Docker Lambda behind
# an API Gateway HTTP API, one instance per environment (#62, #63). Owns the
# whole per-env API stack: ECR repo, Lambda + execution role, HTTP API +
# custom domain + DNS, the waitlist DynamoDB table (moved here from the
# marketing site per docs/adr/0001), the /insolvia/<env>/api SSM config
# namespace (#70), and the CloudWatch alarms + SNS topic (#69).
#
# Mirrors the andreas-services mailer platform module (the house pattern for a
# Mangum service): HTTP API with a $default route to the Lambda, payload
# format 2.0, regional custom domain, no CloudFront. Issue #62's title says
# "CloudFront + API GW", but the mailer precedent and an API's actual needs
# say otherwise — see the custom-domain section below for the deviation note.
#
# ── Bootstrap order (read before the FIRST apply in a fresh account) ────────
# An Image-package Lambda cannot be created without an existing image: the
# apply deadlocks — Terraform owns the ECR repo, but `aws_lambda_function`
# fails ("Source image ... does not exist") until an image has been pushed,
# and the deploy workflow that pushes images targets a repo Terraform has not
# created yet. Break the cycle in three steps, once per environment:
#
#   1. terraform apply -target=module.api_service.aws_ecr_repository.api
#   2. build services/api (`docker build --target lambda`), tag it
#      <repo-url>:latest, and push it
#   3. terraform apply   (full)
#
# Every later deploy is just push-then-update-function-code; Terraform ignores
# the image drift (see the lifecycle note on the Lambda).

locals {
  # insolvia-api-<env> — the repo/Lambda/API/alarm name stem.
  name = "${var.project}-api-${var.environment}"

  # The per-environment API config namespace (#70). Same shape as the
  # inbound-forwarding parameter (/insolvia/shared/...): /<project>/<env>/...,
  # with an /api segment so later services can claim sibling namespaces.
  ssm_prefix = "/${var.project}/${var.environment}/api"
}

# ── Container repository ────────────────────────────────────────
# One repo per environment (insolvia-api-staging / insolvia-api-prod), matching
# the insolvia-<thing>-<env> naming convention. #63's "separate ECR tags per
# environment" is satisfied by separate repos: staging and prod never share an
# image reference, so a prod deploy can never pick up a staging build.

resource "aws_ecr_repository" "api" {
  name                 = local.name
  image_tag_mutability = "MUTABLE"

  image_scanning_configuration {
    scan_on_push = true
  }

  tags = var.tags
}

resource "aws_ecr_lifecycle_policy" "api" {
  repository = aws_ecr_repository.api.name
  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "Keep the ten newest API images"
      selection = {
        tagStatus   = "any"
        countType   = "imageCountMoreThan"
        countNumber = 10
      }
      action = { type = "expire" }
    }]
  })
}

# ── Waitlist storage ────────────────────────────────────────────
# Moved here from the marketing site: the review of the marketing waitlist
# rejected the SSR Lambda holding a DynamoDB grant, and docs/adr/0001 makes
# the API the only application principal with data-store access. Named
# insolvia-waitlist-<env> — deliberately NOT insolvia-marketing-waitlist-*,
# which still exists until the marketing_site module is stripped of it in a
# follow-up PR; the two must coexist without colliding during the migration.
#
# Schema matches insolvia_api.core.waitlist.record_item exactly: constant
# "WAITLIST" partition, "<submittedAt>#<id>" sort key, so rows read back
# time-ordered with a single Query. Separate tables per environment is a #63
# requirement: staging must never be able to read (or pollute) prod.

resource "aws_dynamodb_table" "waitlist" {
  name         = "${var.project}-waitlist-${var.environment}"
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
  name = "${local.name}-policy"
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
  image_uri     = "${aws_ecr_repository.api.repository_url}:latest"
  timeout       = 30
  memory_size   = 512

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
  integration_uri        = aws_lambda_function.api.invoke_arn
  integration_method     = "POST"
  payload_format_version = "2.0"
  timeout_milliseconds   = 30000
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

resource "aws_lambda_permission" "api" {
  statement_id  = "AllowApiGatewayInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.api.function_name
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
# docs/TERRAFORM_ARCHITECTURE.md), so the shared *.insolvia.ai wildcard
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
#
# Future secrets (#65/#70 consumers) slot in as SecureString siblings with
# `lifecycle { ignore_changes = [value] }`, exactly like the
# /insolvia/shared/inbound-forward-to parameter — Terraform creates the slot,
# a human or CI owns the value, and nothing secret is ever committed.

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
