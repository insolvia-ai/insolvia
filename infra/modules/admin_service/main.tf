# The Insolvia admin service (#213): since the switch (ADR 0028, plan 18.22)
# the .NET host backend/src/Insolvia.Admin, in the VPC's private subnets —
# services/admin's Flask+Mangum image before — behind an API Gateway HTTP
# API, one instance per environment — the
# deploy shape of modules/api_service, which owns the long-form reasoning for
# every pattern here (image seeding, the live alias, ignore_changes on
# image_uri/environment, REGIONAL custom domain with no CloudFront, the SSM
# namespace contract, alarm choices). This header records only what DIFFERS:
#
#   - The admin audit is a Postgres table now (admin_audit, INSERT-only for
#     the `admin` role — backend/src/Insolvia.Migrator/Grants.cs: an audit
#     log its own subject can rewrite is not evidence). The DynamoDB audit
#     table this module held was deleted in plan 18.23.
#   - Its one cross-service grant arrives FROM ANOTHER MODULE'S seam:
#     modules/auth's admin_invite_role_name (AdminCreateUser on the firm pool
#     — create and RESEND are one action). The firm data is the database,
#     reached as the `admin` role (module.vpc_access below).
#   - No unqualified Lambda permission: this module was born on the alias, so
#     only the alias-qualified grant exists (api_service carries both only as
#     a migration artifact).
#   - Tighter stage throttling: an internal tool for a handful of staff.
#
# Bootstrap order is identical to the API's and matters on the FIRST apply in
# each environment: apply shared (creates the insolvia-admin repository) →
# seed an image under this environment's marker tag
# (scripts/bootstrap-ecr-images.sh) → apply. An Image Lambda cannot exist
# without an image.

locals {
  # insolvia-<env>-admin-api — the Lambda/API/alarm name stem.
  #
  # The component is `admin-api`, NOT `admin`, and the distinction is the whole
  # point of the naming skill's component rule: `admin` is the staff PORTAL
  # (the SPA bucket + CloudFront in module.admin_web_hosting, serving
  # admin.insolvia.ai), and this is the service behind it
  # (admin-api.insolvia.ai). They were both called `admin` and the pair was
  # unreadable.
  name = "${var.project}-${var.environment}-admin-api"

  # The Postgres role this host logs in as — modules/api_service's
  # db_username note.
  db_username = "admin"

  # The per-environment config namespace: /insolvia/<env>/admin-api — a sibling
  # of the API's /api namespace, exactly as that namespace's comment
  # anticipated. The last segment tracks the component name above; the deploy
  # workflow's get-parameters-by-path step reads this path literally.
  ssm_prefix = "/${var.project}/${var.environment}/admin-api"
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

resource "aws_iam_role" "admin" {
  name               = "${local.name}-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_trust.json
  tags               = var.tags
}

resource "aws_iam_role_policy_attachment" "admin_basic" {
  role       = aws_iam_role.admin.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

# The VPC's network interfaces and rds-db:connect as `admin` — the one
# owner every backend host shares (modules/vpc_lambda_access). The Cognito
# grant is attached by modules/auth's seam, against the role this module
# outputs. No DynamoDB grant any more (see the header).
module "vpc_access" {
  source = "../vpc_lambda_access"

  name                   = local.name
  role_name              = aws_iam_role.admin.name
  db_username            = local.db_username
  db_cluster_resource_id = var.db_cluster_resource_id
}

# ── Lambda function ─────────────────────────────────────────────
# The environment block is only the first-apply seed; the deploy workflow owns
# both image_uri and environment thereafter (ignore_changes below — the
# api_service module records why at length).

resource "aws_lambda_function" "admin" {
  function_name = local.name
  role          = aws_iam_role.admin.arn
  package_type  = "Image"
  image_uri     = "${var.ecr_repository_url}:${var.image_tag}"
  timeout       = 30
  # api_service's note: CPU follows memory, and a .NET cold start is CPU-bound.
  memory_size = 1024
  publish     = true

  # The private subnets; Google's JWKS and Cognito leave through the NAT.
  vpc_config {
    subnet_ids         = var.vpc_subnet_ids
    security_group_ids = var.vpc_security_group_ids
  }

  environment {
    variables = {
      INSOLVIA_ENV      = var.insolvia_env
      FIRM_USER_POOL_ID = var.firm_user_pool_id
      GOOGLE_CLIENT_ID  = var.google_client_id
    }
  }

  lifecycle { ignore_changes = [image_uri, environment] }

  tags = var.tags
  depends_on = [
    aws_iam_role_policy_attachment.admin_basic,
    module.vpc_access,
  ]
}

resource "aws_cloudwatch_log_group" "lambda" {
  name              = "/aws/lambda/${aws_lambda_function.admin.function_name}"
  retention_in_days = 14
  tags              = var.tags
}

# ── Blue/green alias ────────────────────────────────────────────

resource "aws_lambda_alias" "live" {
  name             = "live"
  description      = "The version serving traffic. function_version is owned by the deploy workflow."
  function_name    = aws_lambda_function.admin.function_name
  function_version = aws_lambda_function.admin.version

  lifecycle {
    ignore_changes = [function_version]
  }
}

# ── HTTP API ────────────────────────────────────────────────────

resource "aws_apigatewayv2_api" "admin" {
  name                         = local.name
  protocol_type                = "HTTP"
  disable_execute_api_endpoint = true
  tags                         = var.tags
}

resource "aws_apigatewayv2_integration" "lambda" {
  api_id                 = aws_apigatewayv2_api.admin.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_alias.live.invoke_arn
  integration_method     = "POST"
  payload_format_version = "2.0"
  timeout_milliseconds   = 30000

  depends_on = [aws_lambda_permission.admin_live]
}

resource "aws_apigatewayv2_route" "default" {
  api_id    = aws_apigatewayv2_api.admin.id
  route_key = "$default"
  target    = "integrations/${aws_apigatewayv2_integration.lambda.id}"
}

resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.admin.id
  name        = "$default"
  auto_deploy = true

  # Tighter than the API's 20/40: the callers are a handful of staff in a
  # browser. High enough that a portal screen fanning a few requests never
  # notices; low enough that a scripted flood 429s before running up Lambda
  # concurrency.
  default_route_settings {
    throttling_burst_limit = 20
    throttling_rate_limit  = 10
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

# Alias-qualified only — see the header.
resource "aws_lambda_permission" "admin_live" {
  statement_id  = "AllowApiGatewayInvokeLive"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.admin.function_name
  qualifier     = aws_lambda_alias.live.name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.admin.execution_arn}/*/*"
}

# ── Custom domain + DNS ─────────────────────────────────────────
# REGIONAL, no CloudFront — api_service's custom-domain note owns the
# reasoning, including the cert-region rule.

resource "aws_apigatewayv2_domain_name" "admin" {
  domain_name = var.domain_name

  domain_name_configuration {
    certificate_arn = var.acm_certificate_arn
    endpoint_type   = "REGIONAL"
    security_policy = "TLS_1_2"
  }

  tags = var.tags
}

resource "aws_apigatewayv2_api_mapping" "admin" {
  api_id      = aws_apigatewayv2_api.admin.id
  domain_name = aws_apigatewayv2_domain_name.admin.id
  stage       = aws_apigatewayv2_stage.default.id
}

resource "aws_route53_record" "admin" {
  zone_id = var.hosted_zone_id
  name    = var.domain_name
  type    = "A"

  alias {
    name                   = aws_apigatewayv2_domain_name.admin.domain_name_configuration[0].target_domain_name
    zone_id                = aws_apigatewayv2_domain_name.admin.domain_name_configuration[0].hosted_zone_id
    evaluate_target_health = false
  }
}

# ── Configuration namespace ─────────────────────────────────────
# /insolvia/<env>/admin/<key> — the deploy workflow resolves the namespace and
# injects it into the Lambda environment, so config changes ship without a
# Terraform apply. Every value the service reads has a parameter here; the
# Google client id is a PUBLIC value (it appears in every sign-in redirect),
# hence a plain String.

resource "aws_ssm_parameter" "config" {
  for_each = {
    "insolvia-env"         = var.insolvia_env
    "insolvia-db-username" = local.db_username
    "firm-user-pool-id"    = var.firm_user_pool_id
    "google-client-id"     = var.google_client_id
  }

  name  = "${local.ssm_prefix}/${each.key}"
  type  = "String"
  value = each.value
  tags  = var.tags
}

# ── Alarms ──────────────────────────────────────────────────────
# Same four as the API, same thresholds save the latency note: this service's
# hot route does a Scan, so p99 headroom matters slightly more, not less —
# 2s still holds at tens of firms.

resource "aws_sns_topic" "alarms" {
  name = "${local.name}-alarms"
  tags = var.tags
}

resource "aws_cloudwatch_metric_alarm" "lambda_errors" {
  alarm_name          = "${local.name}-lambda-errors"
  alarm_description   = "The admin Lambda raised an unhandled error."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "Errors"
  namespace           = "AWS/Lambda"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { FunctionName = aws_lambda_function.admin.function_name }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}

resource "aws_cloudwatch_metric_alarm" "lambda_throttles" {
  alarm_name          = "${local.name}-lambda-throttles"
  alarm_description   = "The admin Lambda is being throttled — requests are failing before the handler runs."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "Throttles"
  namespace           = "AWS/Lambda"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { FunctionName = aws_lambda_function.admin.function_name }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}

resource "aws_cloudwatch_metric_alarm" "api_5xx" {
  alarm_name          = "${local.name}-5xx"
  alarm_description   = "The admin HTTP API returned server errors."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "5xx"
  namespace           = "AWS/ApiGateway"
  period              = 300
  statistic           = "Sum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { ApiId = aws_apigatewayv2_api.admin.id }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}

resource "aws_cloudwatch_metric_alarm" "api_p99_latency" {
  alarm_name          = "${local.name}-p99-latency"
  alarm_description   = "The admin HTTP API's p99 latency is sustained above 2 seconds."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 3
  datapoints_to_alarm = 2
  metric_name         = "Latency"
  namespace           = "AWS/ApiGateway"
  period              = 300
  extended_statistic  = "p99"
  threshold           = 2000
  treat_missing_data  = "notBreaching"
  dimensions          = { ApiId = aws_apigatewayv2_api.admin.id }

  alarm_actions = [aws_sns_topic.alarms.arn]
  ok_actions    = [aws_sns_topic.alarms.arn]
  tags          = var.tags
}
