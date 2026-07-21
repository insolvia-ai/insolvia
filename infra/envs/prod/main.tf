# Production environment:
#   • app.insolvia.ai — Flutter web app (static, module.web_hosting)
#   • www.insolvia.ai + apex — marketing site (SSR, module.marketing_site)
#   • api.insolvia.ai — backend API (module.api_service)
# References the shared zone + wildcard cert by name (not by remote state).

locals {
  environment = "prod"
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
# insolvia_env is "production", not local.environment: the infra env is named
# "prod" but the service validates INSOLVIA_ENV against staging|production and
# would crash on "prod" (services/api core/config.py).
#
# First apply in a fresh account needs the image-before-apply bootstrap
# documented at the top of modules/api_service/main.tf.
module "api_service" {
  source = "../../modules/api_service"

  project             = "insolvia"
  environment         = local.environment
  insolvia_env        = "production"
  domain_name         = var.api_subdomain
  hosted_zone_id      = data.aws_route53_zone.main.zone_id
  acm_certificate_arn = data.aws_acm_certificate.wildcard.arn
  tags                = local.common_tags
}

# Auth (#65): production Cognito user pool + web/desktop app clients.
# Production registers ONLY the real app origin — no localhost dev callbacks
# (those live on staging), so nothing running on a laptop can complete a prod
# sign-in. deletion_protection is the point of the variable: this pool holds
# the real attorney accounts.
module "auth" {
  source = "../../modules/auth"

  project     = "insolvia"
  environment = local.environment

  web_origins = ["https://${var.subdomain}"]

  deletion_protection = true

  tags = local.common_tags
}

# ── Marketing site: www.insolvia.ai + apex 301 (issues #43, #47) ─
# The marketing site has NO staging environment (decision D2), so this module
# is instantiated here only. The wildcard cert looked up above carries the
# apex as a SAN (see infra/envs/shared), so one cert covers both aliases.
module "marketing_site" {
  source = "../../modules/marketing_site"

  project             = "insolvia"
  environment         = local.environment
  www_domain          = "www.${var.domain_name}"
  apex_domain         = var.domain_name
  hosted_zone_id      = data.aws_route53_zone.main.zone_id
  acm_certificate_arn = data.aws_acm_certificate.wildcard.arn
  image_tag           = var.marketing_image_tag
  # The SSR waitlist action brokers through the API (docs/adr/0001).
  api_base_url = "https://${module.api_service.domain_name}"
  tags         = local.common_tags
}
