# Staging environment:
#   • web hosting    -> staging-app.insolvia.ai
#   • backend API    -> staging-api.insolvia.ai
#   • mailer API     -> staging-mailer-api.insolvia.ai
#   • marketing site -> staging-www.insolvia.ai
# References the shared zone + wildcard cert by name (not by remote state).

locals {
  environment = "staging"
  common_tags = {
    Project     = "insolvia"
    Environment = local.environment
    ManagedBy   = "terraform"
  }
}

data "aws_route53_zone" "main" {
  name = var.domain_name
}

data "aws_acm_certificate" "wildcard" {
  provider    = aws.us_east_1
  domain      = "*.${var.domain_name}"
  statuses    = ["ISSUED"]
  most_recent = true
}

module "web_hosting" {
  source = "../../modules/web_hosting"

  project             = "insolvia"
  environment         = local.environment
  domain_name         = var.subdomain
  hosted_zone_id      = data.aws_route53_zone.main.zone_id
  acm_certificate_arn = data.aws_acm_certificate.wildcard.arn
  tags                = local.common_tags
}

# Backend API (#62, #63): ECR + Docker Lambda + HTTP API + waitlist table.
# The cert lookup above is shared with CloudFront on purpose: an API Gateway
# REGIONAL custom domain needs its cert in the API's own region, and since
# everything here is us-east-1 the one wildcard cert serves both fronts — no
# second lookup, no second cert.
#
# First apply in a fresh account needs the image-before-apply bootstrap
# documented at the top of modules/api_service/main.tf.
module "api_service" {
  source = "../../modules/api_service"

  project             = "insolvia"
  environment         = local.environment
  insolvia_env        = "staging"
  domain_name         = var.api_subdomain
  hosted_zone_id      = data.aws_route53_zone.main.zone_id
  acm_certificate_arn = data.aws_acm_certificate.wildcard.arn
  tags                = local.common_tags
}

# Mailer (issues 6.2, 6.3): the shared transactional-email microservice, with
# exactly one registered caller — the API Lambda above. caller_role_name feeds
# module.api_service's own execution role back into the mailer's SigV4
# allowlist, which is why this module is declared after it. Same cert-lookup
# reuse rationale as api_service: one REGIONAL wildcard cert, us-east-1,
# serves every custom domain in this account.
#
# enable_attachment_scanning is false: no category this platform sends today
# carries attachments, so GuardDuty Malware Protection for S3 would be a real
# monthly cost with nothing to scan (see modules/mailer/variables.tf).
#
# First apply in a fresh account needs the same image-before-apply bootstrap
# as api_service, documented at the top of modules/mailer/main.tf.
module "mailer" {
  source = "../../modules/mailer"

  project                    = "insolvia"
  environment                = local.environment
  domain_name                = var.mailer_subdomain
  hosted_zone_id             = data.aws_route53_zone.main.zone_id
  acm_certificate_arn        = data.aws_acm_certificate.wildcard.arn
  caller_role_name           = module.api_service.lambda_role_name
  sender_address             = "no-reply@insolvia.ai"
  cors_allowed_origin        = "https://${var.subdomain}"
  enable_attachment_scanning = false
  tags                       = local.common_tags
}

# Publish the mailer's URL into the API's own SSM config namespace (issue
# 6.4) so the API Lambda can read it as MAILER_API_URL. This is an env-level
# resource, deliberately NOT inside module.api_service: module.mailer already
# depends on module.api_service (it reads api_service's lambda_role_name to
# build its SigV4 caller allowlist), so having api_service read
# module.mailer.api_url back would be a dependency cycle. An env-level
# resource referencing both modules' outputs has no such problem.
#
# Name follows the api_service module's own /insolvia/<env>/api/<kebab-key>
# convention exactly (see modules/api_service/main.tf's aws_ssm_parameter
# "config") so the deploy workflow's existing get-parameters-by-path step
# picks it up and derives it into MAILER_API_URL alongside INSOLVIA_ENV and
# WAITLIST_TABLE_NAME — no workflow change needed.
resource "aws_ssm_parameter" "mailer_api_url" {
  name  = "/insolvia/${local.environment}/api/mailer-api-url"
  type  = "String"
  value = module.mailer.api_url
  tags  = local.common_tags
}

# Auth (#65): staging Cognito user pool + web/desktop app clients.
#
# web_origins carries a localhost dev origin ON STAGING ONLY: Cognito callback
# URLs are exact-match (no wildcard host or port), so local Flutter web dev
# against staging auth must pin its port —
#
#   flutter run -d chrome --web-port 3000
#
# http://localhost is one of Cognito's three permitted plain-HTTP loopback
# hosts. Prod registers no dev origins — nothing running on a laptop should be
# able to complete a prod sign-in.
module "auth" {
  source = "../../modules/auth"

  project     = "insolvia"
  environment = local.environment

  web_origins = [
    "https://${var.subdomain}",
    "http://localhost:3000",
  ]

  # Staging pool holds only test accounts; keep it destroyable.
  deletion_protection = false

  tags = local.common_tags
}

# ── Marketing site: staging-www.insolvia.ai ─────────────────────
# The marketing site DOES have a staging environment (issue #45 revisited —
# the original "production + PR previews only" decision D2 is reversed; see
# docs/MVP_PLAN.md). What changed: the site is the public face of the SES
# production-access request (issue #80 / 6.8), which reviews a live privacy
# policy and a working unsubscribe path. Those pages have to be exercisable
# somewhere before prod serves them, and PR previews — the thing D2 leaned on
# — were never built.
#
# apex_domain is deliberately null: a zone has exactly one apex and prod owns
# it. Passing insolvia.ai here would make staging claim prod's CloudFront
# alias and prod's A/AAAA records. See modules/marketing_site/variables.tf.
#
# Nothing here is indexable: app/lib/seo.ts allowlists exactly
# www.insolvia.ai, so every staging response ships noindex and staging
# robots.txt is Disallow: / (issue #48). That is what makes a second public
# copy of the site safe for SEO.
#
# First apply in a fresh account needs the image-before-apply bootstrap
# documented at the top of modules/marketing_site/main.tf.
module "marketing_site" {
  source = "../../modules/marketing_site"

  project             = "insolvia"
  environment         = local.environment
  www_domain          = var.marketing_subdomain
  apex_domain         = null
  hosted_zone_id      = data.aws_route53_zone.main.zone_id
  acm_certificate_arn = data.aws_acm_certificate.wildcard.arn
  image_tag           = var.marketing_image_tag

  # Staging serves; prod is parked (infra/envs/prod/main.tf site_enabled).
  site_enabled = true

  # The SSR waitlist action brokers through the API (docs/adr/0001) — the
  # staging API, so staging submissions never touch the prod table.
  api_base_url = "https://${module.api_service.domain_name}"
  tags         = local.common_tags
}
