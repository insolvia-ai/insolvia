# Production environment:
#   • app.insolvia.ai — Expo web SPA (static, module.web_hosting)
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
# monthly cost with nothing to scan (see modules/mailer/variables.tf). Set
# identically in staging — this is not an environment-specific decision.
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

  # LIVE, and deliberately live while the account is still in the SES sandbox
  # (issue #80 / 6.8 tracks production access). Sandbox does not mean "cannot
  # send" — it means SES only delivers to VERIFIED identities, and
  # module.email in infra/envs/shared verifies the whole insolvia.ai DOMAIN.
  # Every address at insolvia.ai is therefore already a valid recipient, so the
  # real send path — SigV4 auth, queue, sender Lambda, SES call, feedback
  # handling — is exercisable in prod today against an internal address. Mail
  # to third-party domains is what the sandbox rejects, and that starts working
  # the moment production access is granted, with no infrastructure change.
  tags = local.common_tags
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
# WAITLIST_TABLE_NAME — no workflow change needed. Note local.environment is
# "prod" here (the infra env name), matching what api-prod.yml queries
# (/insolvia/prod/api) even though the app-level INSOLVIA_ENV value is
# "production".
resource "aws_ssm_parameter" "mailer_api_url" {
  name  = "/insolvia/${local.environment}/api/mailer-api-url"
  type  = "String"
  value = module.mailer.api_url
  tags  = local.common_tags
}

# Auth (#65): production Cognito user pool + the web SPA app client.
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
# upper-cased, hyphens to underscores) — no workflow change needed.
# local.environment is "prod" here, matching what api-prod.yml queries. Both
# are plain Strings, not SecureStrings: an issuer URL and an app client id are
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

# ── Marketing site: www.insolvia.ai + apex 301 (issues #43, #47) ─
# Staging runs this module too now (infra/envs/staging — decision D2's
# prod-only carve-out was reversed in Milestone 6). What stays exclusive to
# prod is the APEX: a zone has exactly one, staging passes apex_domain = null,
# and only this instantiation creates the apex alias, the apex A/AAAA records,
# and the 301. The wildcard cert looked up above carries the apex as a SAN
# (see infra/envs/shared), so one cert covers both aliases.
module "marketing_site" {
  source = "../../modules/marketing_site"

  project             = "insolvia"
  environment         = local.environment
  www_domain          = "www.${var.domain_name}"
  apex_domain         = var.domain_name
  hosted_zone_id      = data.aws_route53_zone.main.zone_id
  acm_certificate_arn = data.aws_acm_certificate.wildcard.arn
  ecr_repository_url  = data.aws_ecr_repository.service["marketing"].repository_url
  image_tag           = var.marketing_image_tag

  # OFFLINE, deliberately: the site is parked until we have an engaged MyCase.
  # Nothing is destroyed — CloudFront just stops serving. Set back to true and
  # re-apply this env to bring www.insolvia.ai back; no rebuild is needed.
  site_enabled = false

  # The SSR waitlist action brokers through the API (docs/adr/0001).
  api_base_url = "https://${module.api_service.domain_name}"
  tags         = local.common_tags
}

# Case data store (issue 8.2): the first GLBA-scope persistent store — a
# customer-managed key and the case table behind it. Declared after
# module.api_service because it takes that module's execution role name and
# attaches the table grant onto it from its own side (modules/case_store).
#
# Prod inverts staging's two settings: deletion protection on, and the maximum
# key deletion window. Scheduling this key's deletion makes every case
# permanently unreadable — PITR does not help, because the restore is
# encrypted under the same key — so 30 days of second thoughts is the point.
module "case_store" {
  source = "../../modules/case_store"

  project                     = "insolvia"
  environment                 = local.environment
  api_role_name               = module.api_service.lambda_role_name
  deletion_protection         = true
  key_deletion_window_in_days = 30
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
