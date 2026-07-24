# Marketing site hosting (www + apex) — SSR on Lambda behind CloudFront.
#
# Unlike `web_hosting` (static-only, for the Flutter app), the marketing site
# is server-side rendered: a React Router v7 app whose Docker-image Lambda
# (public.ecr.aws/lambda/nodejs:22, `server/index.handler` via
# @react-router/architect) renders every HTML response, while the hashed
# client build is served from a private S3 bucket. One CloudFront
# distribution fronts both:
#
#   viewer ── CloudFront ──┬── /assets/*  → S3 (private, OAC)
#                          └── everything → API Gateway HTTP API → SSR Lambda
#
# On an environment that owns the apex (prod), the distribution carries BOTH
# the www and apex aliases and a viewer-request CloudFront Function 301s apex
# → www (path + query preserved), so no second distribution is needed. Where
# `apex_domain` is null (staging — a zone has exactly one apex and prod owns
# it) the alias, the DNS records, and the redirect branch are all omitted.
# Mirrors andreas-services/website/infra/modules/{hosting,compute,data},
# collapsed into the single-concern module this repo prefers.
#
# FIRST APPLY (bootstrap): the Lambda cannot be created until an image exists
# in the ECR repository, and the repository is created here. Bootstrap once:
#
#   terraform apply -target=module.marketing_site.aws_ecr_repository.ssr
#   # build + docker push <repo_url>:<tag>
#   terraform apply
#
# After creation the deploy workflow owns the running image via
# `aws lambda update-function-code --image-uri` (image_uri is ignore_changes).

locals {
  name_prefix   = "${var.project}-marketing"
  bucket_name   = "${local.name_prefix}-assets-${var.environment}"
  ecr_name      = "${local.name_prefix}-${var.environment}"
  function_name = "${local.name_prefix}-ssr-${var.environment}"

  # Does this environment own the apex? Drives three things that must agree:
  # the CloudFront alias list, the apex A/AAAA records, and whether the
  # viewer-request function emits its 301 branch at all.
  serves_apex = var.apex_domain != null
}

# No waitlist table here. The SSR action brokers submissions through the
# API's POST /v1/waitlist (per docs/adr/0001 — no client holds AWS access),
# and the table lives with modules/api_service as insolvia-waitlist-<env>.

# ── ECR — the SSR Lambda's Docker image ─────────────────────────
resource "aws_ecr_repository" "ssr" {
  name                 = local.ecr_name
  image_tag_mutability = "MUTABLE"

  image_scanning_configuration {
    scan_on_push = true
  }

  tags = var.tags
}

resource "aws_ecr_lifecycle_policy" "ssr" {
  repository = aws_ecr_repository.ssr.name
  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "Keep last 10 images"
      selection    = { tagStatus = "any", countType = "imageCountMoreThan", countNumber = 10 }
      action       = { type = "expire" }
    }]
  })
}

# ── SSR Lambda execution role ───────────────────────────────────
# Logs only. The marketing Lambda holds NO AWS data-plane access at all
# (docs/adr/0001): waitlist submissions leave it as an HTTPS POST to the API,
# so a compromised marketing Lambda can reach no stored PII.
data "aws_iam_policy_document" "lambda_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "ssr" {
  name               = local.function_name
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
  tags               = var.tags
}

data "aws_iam_policy_document" "ssr" {
  statement {
    sid       = "Logs"
    actions   = ["logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents"]
    resources = ["${aws_cloudwatch_log_group.ssr.arn}:*"]
  }
}

resource "aws_iam_role_policy" "ssr" {
  name   = local.function_name
  role   = aws_iam_role.ssr.id
  policy = data.aws_iam_policy_document.ssr.json
}

resource "aws_cloudwatch_log_group" "ssr" {
  name              = "/aws/lambda/${local.function_name}"
  retention_in_days = 14
  tags              = var.tags
}

# ── SSR Lambda ──────────────────────────────────────────────────
resource "aws_lambda_function" "ssr" {
  function_name = local.function_name
  role          = aws_iam_role.ssr.arn
  package_type  = "Image"
  image_uri     = "${aws_ecr_repository.ssr.repository_url}:${var.image_tag}"
  timeout       = 30
  memory_size   = 512

  environment {
    variables = {
      NODE_ENV = "production"
      # Contract with apps/insolvia_marketing/app/lib/waitlist.server.ts:
      # unset means "log instead of submit", so this variable IS the switch
      # that makes the waitlist real. The SSR action POSTs here rather than
      # touching AWS itself (docs/adr/0001).
      INSOLVIA_API_BASE_URL = var.api_base_url
    }
  }

  tags = var.tags

  # The deploy workflow rolls the image forward with
  # `aws lambda update-function-code`; Terraform must not roll it back on the
  # next apply. Environment stays Terraform-owned (unlike the reference) so
  # the INSOLVIA_API_BASE_URL contract lives here, not in a workflow.
  lifecycle {
    ignore_changes = [image_uri]
  }

  depends_on = [aws_cloudwatch_log_group.ssr]
}

# ── HTTP API fronting the SSR Lambda ────────────────────────────
# $default proxy, payload format 2.0 — the event shape the
# @react-router/architect handler consumes. CloudFront uses this API's
# endpoint as the SSR origin (same mechanism as the reference repo), which
# avoids the OAC-to-Function-URL signing path entirely.
resource "aws_apigatewayv2_api" "ssr" {
  name          = local.function_name
  protocol_type = "HTTP"
  tags          = var.tags
}

resource "aws_apigatewayv2_integration" "ssr" {
  api_id                 = aws_apigatewayv2_api.ssr.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.ssr.invoke_arn
  integration_method     = "POST"
  payload_format_version = "2.0"
  timeout_milliseconds   = 30000
}

resource "aws_apigatewayv2_route" "ssr_default" {
  api_id    = aws_apigatewayv2_api.ssr.id
  route_key = "$default"
  target    = "integrations/${aws_apigatewayv2_integration.ssr.id}"
}

resource "aws_apigatewayv2_stage" "ssr" {
  api_id      = aws_apigatewayv2_api.ssr.id
  name        = "$default"
  auto_deploy = true
  tags        = var.tags
}

resource "aws_lambda_permission" "ssr_apigw" {
  statement_id  = "AllowAPIGatewayInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.ssr.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.ssr.execution_arn}/*/*"
}

# ── Assets bucket (private, OAC) ────────────────────────────────
# Holds the hashed client build (build/client/assets/*), served at /assets/*.
resource "aws_s3_bucket" "assets" {
  bucket = local.bucket_name
  tags   = var.tags
}

resource "aws_s3_bucket_public_access_block" "assets" {
  bucket                  = aws_s3_bucket.assets.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_cloudfront_origin_access_control" "assets" {
  name                              = "${local.bucket_name}-oac"
  origin_access_control_origin_type = "s3"
  signing_behavior                  = "always"
  signing_protocol                  = "sigv4"
}

# ── CloudFront policies ─────────────────────────────────────────
data "aws_cloudfront_cache_policy" "disabled" {
  name = "Managed-CachingDisabled"
}

data "aws_cloudfront_cache_policy" "optimized" {
  name = "Managed-CachingOptimized"
}

# Forward all viewer headers/cookies/query-strings to the SSR origin EXCEPT
# Host and Authorization. Host must be the API Gateway domain or the HTTP API
# cannot route the request; the viewer's Host still reaches the app because
# the viewer-request function below re-carries it as X-Forwarded-Host (which
# `allExcept` forwards). Excluding Authorization keeps the door open to
# switching this origin to an OAC-signed Function URL later without CloudFront
# silently dropping its SigV4 signature.
resource "aws_cloudfront_origin_request_policy" "ssr" {
  name    = "${local.function_name}-origin-request"
  comment = "All viewer data except Host + Authorization"

  headers_config {
    header_behavior = "allExcept"
    headers {
      items = ["host", "authorization"]
    }
  }
  cookies_config {
    cookie_behavior = "all"
  }
  query_strings_config {
    query_string_behavior = "all"
  }
}

# Viewer-request function, two jobs:
#
# 1. Apex 301: `insolvia.ai/*` → `https://www.insolvia.ai/*`, path and query
#    preserved. One distribution carries both aliases, so this function IS
#    the apex redirect — no second distribution. Omitted entirely when this
#    environment does not own the apex (local.serves_apex): there is no apex
#    alias on the distribution, so no request can ever arrive with that Host,
#    and emitting a branch comparing against an empty string would redirect
#    nothing while reading as if it did.
#
# 2. X-Forwarded-Host — an app contract, not a nicety. The origin request
#    policy cannot forward the viewer Host (API Gateway needs its own), so
#    this function copies it into X-Forwarded-Host before it is lost.
#    apps/insolvia_marketing reads it in two places: the noindex logic
#    (app/lib/seo.ts) treats any host other than www.insolvia.ai as
#    non-production, and the waitlist action (app/routes/waitlist.tsx)
#    records the serving host on each submission. Without this header every
#    production page ships noindex. Overwriting also stops a viewer from
#    spoofing the header.
resource "aws_cloudfront_function" "viewer_request" {
  name    = "${local.name_prefix}-viewer-request-${var.environment}"
  runtime = "cloudfront-js-2.0"
  comment = local.serves_apex ? "301 ${var.apex_domain} -> ${var.www_domain}; viewer Host -> X-Forwarded-Host" : "viewer Host -> X-Forwarded-Host"
  publish = true
  code = <<-EOT
    function handler(event) {
      var request = event.request;
      var host = request.headers.host.value;
    ${local.serves_apex ? <<-APEX
      if (host === "${var.apex_domain}") {
        var qs = "";
        var keys = Object.keys(request.querystring);
        for (var i = 0; i < keys.length; i++) {
          var entry = request.querystring[keys[i]];
          var values = entry.multiValue ? entry.multiValue : [entry];
          for (var j = 0; j < values.length; j++) {
            qs += (qs === "" ? "?" : "&") + keys[i] + "=" + values[j].value;
          }
        }
        return {
          statusCode: 301,
          statusDescription: "Moved Permanently",
          headers: { "location": { "value": "https://${var.www_domain}" + request.uri + qs } }
        };
      }
    APEX
: ""}
      request.headers["x-forwarded-host"] = { value: host };
      return request;
    }
  EOT
}

# ── CloudFront distribution ─────────────────────────────────────
resource "aws_cloudfront_distribution" "site" {
  enabled         = var.site_enabled
  is_ipv6_enabled = true
  comment         = "${var.project} ${var.environment} marketing (SSR + assets)"
  price_class     = "PriceClass_100"
  aliases         = local.serves_apex ? [var.www_domain, var.apex_domain] : [var.www_domain]
  tags            = var.tags

  # SSR HTTP API (default origin)
  origin {
    domain_name = replace(aws_apigatewayv2_api.ssr.api_endpoint, "https://", "")
    origin_id   = "ssr-lambda"

    custom_origin_config {
      http_port              = 80
      https_port             = 443
      origin_protocol_policy = "https-only"
      origin_ssl_protocols   = ["TLSv1.2"]
    }
  }

  # S3 static assets
  origin {
    domain_name              = aws_s3_bucket.assets.bucket_regional_domain_name
    origin_id                = "s3-assets"
    origin_access_control_id = aws_cloudfront_origin_access_control.assets.id
  }

  default_cache_behavior {
    allowed_methods          = ["GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE"]
    cached_methods           = ["GET", "HEAD"]
    target_origin_id         = "ssr-lambda"
    viewer_protocol_policy   = "redirect-to-https"
    compress                 = true
    cache_policy_id          = data.aws_cloudfront_cache_policy.disabled.id
    origin_request_policy_id = aws_cloudfront_origin_request_policy.ssr.id

    function_association {
      event_type   = "viewer-request"
      function_arn = aws_cloudfront_function.viewer_request.arn
    }
  }

  # Hashed client assets: cache forever at the edge. The function runs here
  # too so apex asset URLs also 301 where an apex exists (the X-Forwarded-Host
  # it sets is inert on this behavior — CachingOptimized forwards no headers
  # to S3).
  ordered_cache_behavior {
    path_pattern           = "/assets/*"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    target_origin_id       = "s3-assets"
    viewer_protocol_policy = "redirect-to-https"
    compress               = true
    cache_policy_id        = data.aws_cloudfront_cache_policy.optimized.id

    function_association {
      event_type   = "viewer-request"
      function_arn = aws_cloudfront_function.viewer_request.arn
    }
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    acm_certificate_arn      = var.acm_certificate_arn
    ssl_support_method       = "sni-only"
    minimum_protocol_version = "TLSv1.2_2021"
  }
}

# ── Bucket policy: only this distribution may read ──────────────
data "aws_iam_policy_document" "assets" {
  statement {
    sid       = "AllowCloudFrontOAC"
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.assets.arn}/*"]

    principals {
      type        = "Service"
      identifiers = ["cloudfront.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "AWS:SourceArn"
      values   = [aws_cloudfront_distribution.site.arn]
    }
  }
}

resource "aws_s3_bucket_policy" "assets" {
  bucket = aws_s3_bucket.assets.id
  policy = data.aws_iam_policy_document.assets.json
}

# ── DNS: www (+ apex where owned) → CloudFront (A and AAAA; IPv6 on) ──
resource "aws_route53_record" "www" {
  for_each = toset(["A", "AAAA"])

  zone_id = var.hosted_zone_id
  name    = var.www_domain
  type    = each.value

  alias {
    name                   = aws_cloudfront_distribution.site.domain_name
    zone_id                = aws_cloudfront_distribution.site.hosted_zone_id
    evaluate_target_health = false
  }
}

# Only the environment that owns the apex publishes these. Two environments
# both creating insolvia.ai A/AAAA aliases would fight over one record set.
resource "aws_route53_record" "apex" {
  for_each = local.serves_apex ? toset(["A", "AAAA"]) : toset([])

  zone_id = var.hosted_zone_id
  name    = var.apex_domain
  type    = each.value

  alias {
    name                   = aws_cloudfront_distribution.site.domain_name
    zone_id                = aws_cloudfront_distribution.site.hosted_zone_id
    evaluate_target_health = false
  }
}
