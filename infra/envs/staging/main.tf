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

# The shared per-service container repositories, created in infra/envs/shared.
# Looked up rather than read from remote state, matching the zone/cert above
# (docs/reference/terraform.md: cross-layer references are data sources,
# never terraform_remote_state).
#
# This is a second reason the documented apply order — shared before any
# environment — is load-bearing rather than ceremonial: these lookups fail
# outright until `shared` has applied, exactly as the certificate lookup does.
data "aws_ecr_repository" "service" {
  for_each = toset(["api", "marketing", "mailer"])

  name = "insolvia-${each.key}"
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
  ecr_repository_url  = data.aws_ecr_repository.service["api"].repository_url
  image_tag           = local.environment
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
  ecr_repository_url         = data.aws_ecr_repository.service["mailer"].repository_url
  image_tag                  = local.environment
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

# Auth (#65): staging Cognito user pool + the web SPA app client.
#
# web_origins carries a localhost dev origin ON STAGING ONLY: Cognito callback
# URLs are exact-match (no wildcard host or port), so local web dev against
# staging auth must PIN ITS PORT — which is why the Expo dev server is started
# on 3000 rather than Metro's default 8081:
#
#   npx expo start --web --port 3000
#
# The port below and that flag are one contract; changing either alone breaks
# local sign-in. http://localhost is one of Cognito's three permitted
# plain-HTTP loopback hosts. Prod registers no dev origins — nothing running on
# a laptop should be able to complete a prod sign-in.
module "auth" {
  source = "../../modules/auth"

  project     = "insolvia"
  environment = local.environment

  web_origins = [
    "https://${var.subdomain}",
    "http://localhost:3000",
  ]

  # Sign-in on our own domain: staging-auth.insolvia.ai.
  #
  # This depends on something that is NOT visible from here, so it is written
  # down: Cognito refuses to create a custom domain unless the PARENT domain
  # resolves to an IP, and the parent of staging-auth.insolvia.ai is the apex.
  # The apex resolves only because prod's marketing distribution is enabled
  # (`site_enabled = true` in infra/envs/prod/main.tf) — a DISABLED CloudFront
  # distribution serves no DNS, which is what previously made this fail with
  # "Was not able to resolve a DNS A record for the parent domain".
  #
  # So parking prod's marketing site again would break STAGING sign-in, which
  # is not a connection anyone would guess. Verify with `dig +short
  # insolvia.ai A` — reading the Route53 record is NOT the same check, because
  # an alias to a disabled distribution is a record that resolves to nothing.
  #
  # Added alongside the Cognito prefix domain, not replacing it (see the module):
  # the app moves to this hostname when its next build reads the `auth_domain`
  # output, and the prefix keeps serving until then, so there is no window where
  # sign-in is down.
  #
  # First apply takes 15-20 minutes — Cognito provisions its own CloudFront
  # distribution — and Terraform blocks for it.
  custom_domain   = "staging-auth.${var.domain_name}"
  certificate_arn = data.aws_acm_certificate.wildcard.arn
  hosted_zone_id  = data.aws_route53_zone.main.zone_id

  # Staging pool holds only test accounts; keep it destroyable.
  deletion_protection = false

  tags = local.common_tags
}

# One-time adoption of the branding style that already exists in staging.
#
# It was created by hand (`aws cognito-idp create-managed-login-branding`) to
# restore sign-in: managed login was switched on before any style existed, and
# an app client with no style does not fall back to defaults — Cognito serves
# "Login pages unavailable" and sign-in is simply down. That style is what the
# console branding editor has been editing since.
#
# An `import` block rather than a CLI `terraform import`, because staging is
# only ever applied by CI (infra/CLAUDE.md) and an import block is declarative:
# CI adopts the existing style on the next apply instead of trying to CREATE
# one, which AWS rejects with ManagedLoginBrandingExistsException when the
# client already has a style.
#
# Staging-only. Prod has no style yet, so prod's apply creates one from the same
# committed settings — no import, and nothing to adopt.
#
# Safe to delete once this has applied; it is idempotent, so leaving it costs
# nothing but noise.
import {
  to = module.auth.aws_cognito_managed_login_branding.web
  id = "us-east-1_M3y3AxIit,b978dad0-ece6-4262-9484-caa7b9bc8d73"
}

# Publish the pool's issuer and web app client id into the API's own SSM
# config namespace (issue #79) so the API Lambda can read them as
# AUTH_ISSUER_URL and AUTH_CLIENT_ID and verify access tokens.
#
# Env-level for the same reason mailer_api_url above is: module.api_service
# knows nothing about module.auth, and having it read these back would couple
# two modules that are otherwise independent. An env-level resource
# referencing module.auth's outputs has no such problem.
#
# Same /insolvia/<env>/api/<kebab-key> convention, so the deploy workflow's
# existing get-parameters-by-path step picks them up (last path segment,
# upper-cased, hyphens to underscores) — no workflow change needed. Both are
# plain Strings, not SecureStrings: an issuer URL and an app client id are
# public values that appear in every sign-in redirect the browser makes.
resource "aws_ssm_parameter" "auth_issuer_url" {
  name  = "/insolvia/${local.environment}/api/auth-issuer-url"
  type  = "String"
  value = module.auth.issuer_url
  tags  = local.common_tags
}

resource "aws_ssm_parameter" "auth_client_id" {
  name  = "/insolvia/${local.environment}/api/auth-client-id"
  type  = "String"
  value = module.auth.web_client_id
  tags  = local.common_tags
}

# ── Marketing site: staging-www.insolvia.ai ─────────────────────
# The marketing site DOES have a staging environment (issue #45 revisited —
# the original "production + PR previews only" decision D2 is reversed; see
# docs/plan.md). What changed: the site is the public face of the SES
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
  ecr_repository_url  = data.aws_ecr_repository.service["marketing"].repository_url
  image_tag           = var.marketing_image_tag

  # Staging serves the REAL site — it is where the marketing site is reviewed
  # before launch. Production serves a holding page (site_mode there).
  site_enabled = true
  site_mode    = "full"

  # The SSR waitlist action brokers through the API (docs/adr/0001) — the
  # staging API, so staging submissions never touch the prod table.
  api_base_url = "https://${module.api_service.domain_name}"
  tags         = local.common_tags
}

# Case data store (issue 8.2): the first GLBA-scope persistent store — a
# customer-managed key and the case table behind it. Declared after
# module.api_service because it takes that module's execution role name and
# attaches the table grant onto it from its own side (modules/case_store).
#
# Staging is disposable on purpose: synthetic cases only, no deletion
# protection, and the minimum key deletion window so a teardown does not leave
# a 30-day-pending key behind. Prod inverts both.
module "case_store" {
  source = "../../modules/case_store"

  project                     = "insolvia"
  environment                 = local.environment
  api_role_name               = module.api_service.lambda_role_name
  deletion_protection         = false
  key_deletion_window_in_days = 7
  tags                        = local.common_tags
}

# Publish the case table name into the API's SSM config namespace, env-level
# for the same reason mailer_api_url and the auth parameters above are:
# module.case_store already depends on module.api_service, so having
# api_service read the table name back would be a dependency cycle.
#
# Same /insolvia/<env>/api/<kebab-key> convention, so the deploy workflow's
# get-parameters-by-path step derives it into CASE_TABLE_NAME with no workflow
# change. The key ARN is deliberately NOT published: the SDK resolves the key
# from the table, so nothing in services/api ever names it. That is NOT the
# same as the API role needing no key permissions — it does, and
# modules/case_store explains why.
resource "aws_ssm_parameter" "case_table_name" {
  name  = "/insolvia/${local.environment}/api/case-table-name"
  type  = "String"
  value = module.case_store.table_name
  tags  = local.common_tags
}
