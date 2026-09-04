# The Insolvia MCP service (services/mcp, ADR 0016): the MCP SDK + Mangum
# Docker Lambda behind an API Gateway HTTP API, one instance per environment
# (#262). The house pattern for a Mangum service, copied from
# modules/api_service: HTTP API with a $default route to the Lambda, payload
# format 2.0, regional custom domain, no CloudFront. Owns the per-env MCP
# stack: Lambda + execution role, HTTP API + custom domain + DNS, the
# /insolvia/<env>/mcp SSM config namespace, and the CloudWatch alarms + SNS
# topic.
#
# NO DATA STORES OF ITS OWN, deliberately. This service is a second surface
# over the SAME case, access-log and firm tables the API composes; its
# execution role's grants attach from modules/case_store and
# modules/firm_store (mcp_role_name), matching what ADR 0011 did for the
# admin service — two execution roles with data-store access instead of one,
# each scoped to what its service exports.
#
# ITS OWN LAMBDA AND ITS OWN THROTTLES, and that is the ADR's blast-radius
# argument: agent traffic is bursty, retry-happy, and shaped by harness
# behaviour we do not control. A misbehaving harness exhausts THIS function's
# concurrency and trips THIS stage's throttling without an attorney's intake
# autosave failing beside it.
#
# ── Bootstrap order (read before the FIRST apply in a fresh environment) ────
# An Image-package Lambda cannot be created without an existing image. Same
# one-time dance as every service module here:
#
#   1. apply infra/envs/shared (creates insolvia-shared-mcp)
#   2. scripts/bootstrap-ecr-images.sh <env> mcp   (seeds <repo-url>:<env>)
#   3. terraform apply   (full)
#
# Every later deploy is push-then-update-function-code; Terraform ignores the
# image drift (see the lifecycle note on the Lambda).

locals {
  # insolvia-<env>-mcp — the Lambda/API/alarm name stem. Environment SECOND,
  # per the insolvia-aws-naming skill; `mcp` is the component because that is
  # the surface this serves (mcp.insolvia.ai).
  name = "${var.project}-${var.environment}-mcp"

  # The per-environment MCP config namespace, a sibling of /…/api — the
  # namespace-per-service shape modules/api_service's #70 comment reserved.
  ssm_prefix = "/${var.project}/${var.environment}/mcp"
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

resource "aws_iam_role" "mcp" {
  name               = "${local.name}-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_trust.json
  tags               = var.tags
}

resource "aws_iam_role_policy_attachment" "mcp_basic" {
  role       = aws_iam_role.mcp.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

# NO inline data policy here, deliberately: every data grant this role holds
# attaches from the module that owns the store (modules/case_store's
# mcp_case_access, modules/firm_store's mcp_firm_access), so what this
# service may reach is readable next to what the other principals may — one
# file per store, every principal's grant side by side.

# ── Lambda function ─────────────────────────────────────────────

resource "aws_lambda_function" "mcp" {
  function_name = local.name
  role          = aws_iam_role.mcp.arn
  package_type  = "Image"
  # The per-environment marker tag, not `:latest` — modules/api_service's
  # promotion-invariant argument, verbatim.
  image_uri   = "${var.ecr_repository_url}:${var.image_tag}"
  timeout     = 30
  memory_size = 512

  # Publish a numbered version on every Terraform-driven change so the alias
  # below always has a real version to point at.
  publish = true

  environment {
    variables = {
      INSOLVIA_ENV = var.insolvia_env
    }
  }

  # The deploy workflow owns BOTH image_uri and environment (re-derived from
  # the SSM namespace below on every deploy) — the api_service rule, verbatim.
  # The block above is only the seed for the very first apply.
  lifecycle { ignore_changes = [image_uri, environment] }

  tags       = var.tags
  depends_on = [aws_iam_role_policy_attachment.mcp_basic]
}

resource "aws_cloudwatch_log_group" "lambda" {
  name              = "/aws/lambda/${aws_lambda_function.mcp.function_name}"
  retention_in_days = 14
  tags              = var.tags
}

# ── Blue/green alias ────────────────────────────────────────────
# Same shape and same reasoning as api_service's `live` alias: the deploy
# workflow publishes a version, smoke-tests it by ARN, and only then repoints
# this — so a failed smoke test leaves the previous version serving.
resource "aws_lambda_alias" "live" {
  name             = "live"
  description      = "The version serving traffic. function_version is owned by the deploy workflow."
  function_name    = aws_lambda_function.mcp.function_name
  function_version = aws_lambda_function.mcp.version

  # Mandatory, not defensive — api_service's alias comment owns the argument.
  lifecycle {
    ignore_changes = [function_version]
  }
}

# ── HTTP API ────────────────────────────────────────────────────
# $default route -> the Lambda, payload format 2.0 — what Mangum consumes.
# The MCP SDK's Starlette app owns all routing (/mcp, the RFC 9728
# /.well-known documents), so API Gateway stays a dumb front door. The
# execute-api endpoint is disabled so the custom domain below is the only way
# in — which matters MORE here than for the API: the hostname is the
# canonical resource URI harnesses bind tokens to, and the service's own
# Host-header validation (transport security) admits exactly this domain.

resource "aws_apigatewayv2_api" "mcp" {
  name                         = local.name
  protocol_type                = "HTTP"
  disable_execute_api_endpoint = true
  tags                         = var.tags
}

resource "aws_apigatewayv2_integration" "lambda" {
  api_id                 = aws_apigatewayv2_api.mcp.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_alias.live.invoke_arn
  integration_method     = "POST"
  payload_format_version = "2.0"
  timeout_milliseconds   = 30000

  # Terraform infers NO dependency between an integration and a permission —
  # api_service's comment owns the 500-burst failure this prevents.
  depends_on = [aws_lambda_permission.mcp_live]
}

resource "aws_apigatewayv2_route" "default" {
  api_id    = aws_apigatewayv2_api.mcp.id
  route_key = "$default"
  target    = "integrations/${aws_apigatewayv2_integration.lambda.id}"
}

resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.mcp.id
  name        = "$default"
  auto_deploy = true

  # Rate limiting is a spec MUST for tool invocations, and mcp-surface.md
  # assigned the mechanism to this layer: stage-wide throttling, ahead of the
  # Lambda, so a retry-happy harness gets 429s before it consumes
  # concurrency. Half the API's numbers — agent traffic has no human waiting
  # on a spinner, and a throttled harness retries politely by contract —
  # while still far above what a working session generates.
  default_route_settings {
    throttling_burst_limit = 20
    throttling_rate_limit  = 10
  }

  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.mcp_access.arn
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

resource "aws_cloudwatch_log_group" "mcp_access" {
  name              = "/aws/apigateway/${local.name}"
  retention_in_days = 14
  tags              = var.tags
}

# Lambda evaluates the ALIAS's own resource policy — api_service's comment
# owns why the permission is alias-qualified.
resource "aws_lambda_permission" "mcp_live" {
  statement_id  = "AllowApiGatewayInvokeLive"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.mcp.function_name
  qualifier     = aws_lambda_alias.live.name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.mcp.execution_arn}/*/*"
}

# ── Custom domain + DNS ─────────────────────────────────────────
# Regional custom domain, no CloudFront — the mailer/api precedent. For this
# service the stable hostname is doubly load-bearing (ADR 0016): it is the
# canonical resource URI in the protected-resource metadata and the
# `resource` parameter harnesses send, so it must never follow the API's
# routing around.

resource "aws_apigatewayv2_domain_name" "mcp" {
  domain_name = var.domain_name

  domain_name_configuration {
    certificate_arn = var.acm_certificate_arn
    endpoint_type   = "REGIONAL"
    security_policy = "TLS_1_2"
  }

  tags = var.tags
}

resource "aws_apigatewayv2_api_mapping" "mcp" {
  api_id      = aws_apigatewayv2_api.mcp.id
  domain_name = aws_apigatewayv2_domain_name.mcp.id
  stage       = aws_apigatewayv2_stage.default.id
}

resource "aws_route53_record" "mcp" {
  zone_id = var.hosted_zone_id
  name    = var.domain_name
  type    = "A"

  alias {
    name                   = aws_apigatewayv2_domain_name.mcp.domain_name_configuration[0].target_domain_name
    zone_id                = aws_apigatewayv2_domain_name.mcp.domain_name_configuration[0].hosted_zone_id
    evaluate_target_health = false
  }
}

# ── Configuration namespace ─────────────────────────────────────
# Convention: every MCP config value lives at /insolvia/<env>/mcp/<key>; the
# deploy workflow resolves the namespace and injects it into the Lambda
# environment (which Terraform ignores above). Only insolvia-env is created
# HERE — the table names and auth values reference other modules' outputs and
# live in the env root, the same env-level placement (and the same
# cycle-avoidance reason) as /…/api/case-table-name.

resource "aws_ssm_parameter" "config" {
  for_each = {
    "insolvia-env" = var.insolvia_env
  }

  name  = "${local.ssm_prefix}/${each.key}"
  type  = "String"
  value = each.value
  tags  = var.tags
}

# ── Alarms ──────────────────────────────────────────────────────
# Its own SNS topic, matching api_service — subscriptions are a human step
# (this repo commits no real addresses).

resource "aws_sns_topic" "alarms" {
  name = "${local.name}-alarms"
  tags = var.tags
}

# The service maps every expected failure to a tool error or a 4xx, so an
# Errors datapoint means an unhandled exception or a crashed runtime.
resource "aws_cloudwatch_metric_alarm" "lambda_errors" {
  alarm_name          = "${local.name}-lambda-errors"
  alarm_description   = "The MCP Lambda raised an unhandled error."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "Errors"
  namespace           = "AWS/Lambda"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { FunctionName = aws_lambda_function.mcp.function_name }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}

# Throttles are EXPECTED here in a way they are not for the API — the stage
# throttling above exists to fire against a misbehaving harness — so the
# alarm is how 12.5 finds out a real harness is hitting the ceiling, not a
# sign the ceiling is wrong.
resource "aws_cloudwatch_metric_alarm" "lambda_throttles" {
  alarm_name          = "${local.name}-lambda-throttles"
  alarm_description   = "The MCP Lambda is being throttled — a harness is exceeding the stage limits."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "Throttles"
  namespace           = "AWS/Lambda"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { FunctionName = aws_lambda_function.mcp.function_name }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}

# HTTP API server errors — note the v2 metric name really is "5xx".
resource "aws_cloudwatch_metric_alarm" "mcp_5xx" {
  alarm_name          = "${local.name}-5xx"
  alarm_description   = "The MCP HTTP API returned server errors."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "5xx"
  namespace           = "AWS/ApiGateway"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { ApiId = aws_apigatewayv2_api.mcp.id }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}

# p99 end-to-end latency. get_case fans out into a dozen keyed queries, so
# the API's 2 s threshold holds here too — headroom for cold starts, which
# dominate the p99 at agent-session traffic levels.
resource "aws_cloudwatch_metric_alarm" "mcp_p99_latency" {
  alarm_name          = "${local.name}-p99-latency"
  alarm_description   = "The MCP HTTP API's p99 latency is sustained above 2 seconds."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 3
  datapoints_to_alarm = 2
  metric_name         = "Latency"
  namespace           = "AWS/ApiGateway"
  period              = 300
  extended_statistic  = "p99"
  threshold           = 2000
  treat_missing_data  = "notBreaching"
  dimensions          = { ApiId = aws_apigatewayv2_api.mcp.id }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}
