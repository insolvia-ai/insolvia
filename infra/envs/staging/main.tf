# Staging environment:
#   • web hosting    -> staging-app.insolvia.ai
#   • backend API    -> staging-api.insolvia.ai
#   • marketing site -> staging-www.insolvia.ai
# References the shared zone + wildcard cert by name (not by remote state);
# both are infra/envs/shared in the insolvia-services repo, as is the mailer
# (staging-mailer-api / mailer-api.insolvia.ai), which this root's API calls.
# Auth (the Cognito pool, its domains and app clients) is that repo's too, and
# read through its SSM contract (/insolvia/<env>/auth/*) — module.auth below is the consumer half (ADR 0026).

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

# The shared per-service container repositories, created in insolvia-services' infra/envs/shared.
# Looked up rather than read from remote state, matching the zone/cert above
# (docs/reference/terraform.md: cross-layer references are data sources,
# never terraform_remote_state).
#
# This is a second reason the documented apply order — shared before any
# environment — is load-bearing rather than ceremonial: these lookups fail
# outright until `shared` has applied, exactly as the certificate lookup does.
#
# The backend's repositories are the `-dotnet` six since the switch (ADR
# 0028, plan 18.22) — created beside the Python ones by insolvia-services
# (#11), which removes the Python repositories after 18.23. `worker` is the
# .NET successor of `jobs`; `migrator` is new.
data "aws_ecr_repository" "service" {
  for_each = toset(["api-dotnet", "admin-api-dotnet", "worker-dotnet", "marketing", "mcp-dotnet", "filing-dotnet", "migrator-dotnet"])

  name = "insolvia-shared-${each.key}"
}

module "web_hosting" {
  source = "../../modules/web_hosting"

  project             = "insolvia"
  environment         = local.environment
  component           = "app"
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
  ecr_repository_url  = data.aws_ecr_repository.service["api-dotnet"].repository_url
  image_tag           = local.environment

  vpc_subnet_ids         = module.network.private_subnet_ids
  vpc_security_group_ids = [module.network.lambda_security_group_id]
  db_cluster_resource_id = module.aurora.cluster_resource_id

  tags = local.common_tags
}



# Admin portal hosting (#215): a second web_hosting instantiation — the module
# takes a `component` for exactly this, which is what keeps the two buckets
# apart (insolvia-<env>-app-* and insolvia-<env>-admin-*). Each component needs
# its own ARN pattern in the deploy role's S3 grant, and infra/envs/ci-trust is
# human-applied, so a THIRD instance is an IAM change first. Serves
# apps/insolvia_admin, built and synced by the admin deploy workflow.
module "admin_web_hosting" {
  source = "../../modules/web_hosting"

  project             = "insolvia"
  environment         = local.environment
  component           = "admin"
  domain_name         = var.admin_subdomain
  hosted_zone_id      = data.aws_route53_zone.main.zone_id
  acm_certificate_arn = data.aws_acm_certificate.wildcard.arn
  tags                = local.common_tags
}

# Admin service (#213): the cross-tenant provisioning surface (services/admin,
# ADR 0011), deployed the way the API is — same cert-lookup reuse, same
# image-before-apply bootstrap on the first apply (extend it with
# scripts/bootstrap-ecr-images.sh --services admin). Declared after
# api_service only by convention; its one cross-module grant attaches from
# modules/auth (admin_invite_role_name — the same one-action AdminCreateUser
# shape as the API's invite grant). Its firm data is Aurora's (ADR 0028).
module "admin_service" {
  source = "../../modules/admin_service"

  project             = "insolvia"
  environment         = local.environment
  insolvia_env        = "staging"
  domain_name         = var.admin_api_subdomain
  hosted_zone_id      = data.aws_route53_zone.main.zone_id
  acm_certificate_arn = data.aws_acm_certificate.wildcard.arn
  ecr_repository_url  = data.aws_ecr_repository.service["admin-api-dotnet"].repository_url
  image_tag           = local.environment
  firm_user_pool_id   = module.auth.user_pool_id
  google_client_id    = var.google_admin_client_id

  vpc_subnet_ids         = module.network.private_subnet_ids
  vpc_security_group_ids = [module.network.lambda_security_group_id]
  db_cluster_resource_id = module.aurora.cluster_resource_id

  # Staging's audit rows are test provisioning of test firms; the table must
  # be destroyable with the environment.
  audit_deletion_protection = false

  tags = local.common_tags
}

# MCP service (#262, ADR 0016): the remote MCP server (services/mcp) — a
# second protocol surface over the same case and firm stores, deployed the
# way the API is: same cert-lookup reuse, same image-before-apply bootstrap
# on the first apply (scripts/bootstrap-ecr-images.sh staging mcp). Its data is
# Aurora's (ADR 0028), reached as the `mcp` Postgres role, so this module owns
# the service stack and nothing else.
module "mcp_service" {
  source = "../../modules/mcp_service"

  project             = "insolvia"
  environment         = local.environment
  insolvia_env        = "staging"
  domain_name         = var.mcp_subdomain
  hosted_zone_id      = data.aws_route53_zone.main.zone_id
  acm_certificate_arn = data.aws_acm_certificate.wildcard.arn
  ecr_repository_url  = data.aws_ecr_repository.service["mcp-dotnet"].repository_url
  image_tag           = local.environment

  vpc_subnet_ids         = module.network.private_subnet_ids
  vpc_security_group_ids = [module.network.lambda_security_group_id]
  db_cluster_resource_id = module.aurora.cluster_resource_id

  tags = local.common_tags
}

# The mailer moved to the insolvia-services repo on 2026-10-11 (ADR 0025): its
# staging/prod resources are that repo's infra/envs/<env>, under the same
# module.mailer address, moved there by a state migration. Nothing here
# declares or references it; MAILER_API_URL below is built from the hostname.

# Publish the mailer's URL into the API's own SSM config namespace (issue
# 6.4) so the API Lambda can read it as MAILER_API_URL.
#
# CONSTRUCTED from var.mailer_subdomain, not read from the mailer: the mailer
# is the insolvia-services repo's (its infra/envs/<env> root), and the two
# repos reference each other by NAME, never by remote state. The hostname is
# the contract — that root serves exactly this name from the same variable
# default, so changing it means changing both. The dependency also only runs
# one way at deploy time now: the mailer looks up the API's role
# (insolvia-<env>-api-role); this root no longer waits on anything of the
# mailer's.
#
# Name follows the api_service module's own /insolvia/<env>/api/<kebab-key>
# convention exactly (see modules/api_service/main.tf's aws_ssm_parameter
# "config") so the deploy workflow's existing get-parameters-by-path step
# picks it up and derives it into MAILER_API_URL alongside INSOLVIA_ENV and
# WAITLIST_TABLE_NAME — no workflow change needed.
resource "aws_ssm_parameter" "mailer_api_url" {
  name  = "/insolvia/${local.environment}/api/mailer-api-url"
  type  = "String"
  value = "https://${var.mailer_subdomain}"
  tags  = local.common_tags
}

# Auth (#65): the CONSUMER half of the staging pool. The pool, its domains
# (staging-auth.insolvia.ai) and its app clients — including the localhost:3000
# dev origin staging registers for local web dev — are insolvia-services'
# (infra/envs/staging, module.auth) since ADR 0026, and so is the sign-in
# page's style. This reads them from its SSM contract (/insolvia/staging/auth/*,
# modules/auth_contract) and grants this repo's two Lambda roles their one
# action on the pool.
#
# The localhost:3000 callback and `npx expo start --web --port 3000` are still
# one contract — now across two repositories.
module "auth" {
  source = "../../modules/auth"

  project     = "insolvia"
  environment = local.environment

  # Lets POST /v1/firm/users mint a colleague's Cognito account. ONE action on
  # ONE pool — the module's own comment has the argument for why that is the
  # whole grant, and what it deliberately excludes.
  api_role_name = module.api_service.lambda_role_name

  # Provisioning a firm's first administrator (#213) — same one-action grant
  # on the admin service's own role.
  admin_invite_role_name = module.admin_service.lambda_role_name
}

# Publish the pool's issuer and web app client id into the API's own SSM
# config namespace (issue #79) so the API Lambda can read them as
# AUTH_ISSUER_URL and AUTH_CLIENT_ID and verify access tokens.
#
# Env-level, not inside module.api_service: module.api_service
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

resource "aws_ssm_parameter" "auth_user_pool_id" {
  # NOT redundant with auth-issuer-url, which ends in the same id. One value is
  # verified against and one is called; parsing an id out of a URL to make an
  # AWS API call would make that URL's format load-bearing in a way Cognito
  # never promised. See services/api's core/config.py.
  name  = "/insolvia/${local.environment}/api/auth-user-pool-id"
  type  = "String"
  value = module.auth.user_pool_id
  tags  = local.common_tags
}

resource "aws_ssm_parameter" "auth_client_id" {
  name  = "/insolvia/${local.environment}/api/auth-client-id"
  type  = "String"
  value = module.auth.web_client_id
  tags  = local.common_tags
}

# The client portal's app client (ADR 0023), the same namespace and the same
# derivation as auth-client-id above: the deploy workflow turns it into
# AUTH_PORTAL_CLIENT_ID, which is the ONE client id every /v1/portal/ route
# verifies — disjoint from AUTH_CLIENT_ID, and the API refuses to verify
# portal tokens at all if the two are ever equal. A public value, like the
# web client id: it rides in every portal sign-in redirect.
resource "aws_ssm_parameter" "auth_portal_client_id" {
  name  = "/insolvia/${local.environment}/api/auth-portal-client-id"
  type  = "String"
  value = module.auth.portal_client_id
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

# Async job pipeline (ADR 0018, issue #271): the queue the API enqueues
# accepted jobs onto, the worker Lambda that runs them, the DLQ and its
# alarms. Declared after module.api_service because it takes that module's
# role name (the enqueue grant attaches from the pipeline's side) and its
# alarms topic; the worker's CASE-TABLE grant attaches from module.case_store
# below (worker_role_name), completing the seam without either module
# referencing the other's resources.
#
# First apply in a fresh account needs the image-before-apply bootstrap
# documented at the top of modules/job_pipeline/main.tf
# (scripts/bootstrap-ecr-images.sh staging jobs).
module "job_pipeline" {
  source = "../../modules/job_pipeline"

  project            = "insolvia"
  environment        = local.environment
  insolvia_env       = "staging"
  ecr_repository_url = data.aws_ecr_repository.service["worker-dotnet"].repository_url
  image_tag          = local.environment
  api_role_name      = module.api_service.lambda_role_name
  alarms_topic_arn   = module.api_service.alarms_topic_arn

  vpc_subnet_ids         = module.network.private_subnet_ids
  vpc_security_group_ids = [module.network.lambda_security_group_id]
  db_cluster_resource_id = module.aurora.cluster_resource_id

  tags = local.common_tags
}

# The queue URL, into the API's SSM config namespace — env-level like the
# other parameters in this namespace (module.api_service never reads another
# module's outputs back, which is what keeps these roots acyclic), and the deploy workflow's
# get-parameters-by-path step derives it into JOB_QUEUE_URL with no workflow
# change. The WORKER reads the same namespace: it needs CASE_TABLE_NAME, which
# is already published below.
resource "aws_ssm_parameter" "job_queue_url" {
  name  = "/insolvia/${local.environment}/api/job-queue-url"
  type  = "String"
  value = module.job_pipeline.queue_url
  tags  = local.common_tags
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
  worker_role_name            = module.job_pipeline.worker_role_name
  deletion_protection         = false
  key_deletion_window_in_days = 7
  tags                        = local.common_tags
}

# ── Firms ───────────────────────────────────────────────────────
# Tenancy: which firm a signed-in user belongs to and what they may do. Takes
# the case store's key rather than minting one — firm membership decides who
# reads case data, and one deny should cover both.
module "firm_store" {
  source = "../../modules/firm_store"

  project                = "insolvia"
  environment            = local.environment
  kms_key_arn            = module.case_store.kms_key_arn
  point_in_time_recovery = false
  deletion_protection    = false
  tags                   = local.common_tags
}

# ── Filing credentials (ADR 0024, guardrail 3) ──────────────────
# The vault — the module says how its key policy keeps Decrypt to the filing
# worker's role alone. Staging is where the maintainer enrols the courts'
# TRAINING-database logins (ADR 0024 § Three environments), through the same
# enrolment screen; never seeded. Disposable like the rest of staging.
module "filing_credentials" {
  source = "../../modules/filing_credentials"

  project                     = "insolvia"
  environment                 = local.environment
  table_kms_key_arn           = module.case_store.kms_key_arn
  api_role_name               = module.api_service.lambda_role_name
  point_in_time_recovery      = false
  deletion_protection         = false
  key_deletion_window_in_days = 7
  tags                        = local.common_tags
}

# ── The filing queue (ADR 0024 PR 6, guardrail 1) ───────────────
# Where the attorney's per-filing approval puts a filing job — the API's
# role may send and nothing else, the filing worker's role (created by
# module.filing_credentials above) may consume and never send. Its consumer
# is module.filing_worker below (services/filing, ADR 0024 PR 7).
module "filing_queue" {
  source = "../../modules/filing_queue"

  project          = "insolvia"
  environment      = local.environment
  api_role_name    = module.api_service.lambda_role_name
  worker_role_name = module.filing_credentials.worker_role_name
  tags             = local.common_tags
}

# Into the API's SSM namespace, job-queue-url's way: the deploy workflow's
# get-parameters-by-path step derives it into FILING_QUEUE_URL with no
# workflow change.
resource "aws_ssm_parameter" "filing_queue_url" {
  name  = "/insolvia/${local.environment}/api/filing-queue-url"
  type  = "String"
  value = module.filing_queue.queue_url
  tags  = local.common_tags
}

# ── Case documents ──────────────────────────────────────────────
# Takes the case store's key rather than minting one: one key protects one
# case, rows and documents alike, and the deploy role's data-plane deny is
# written against that key's alias.
module "case_documents" {
  source = "../../modules/case_documents"

  project       = "insolvia"
  environment   = local.environment
  aws_region    = var.aws_region
  kms_key_arn   = module.case_store.kms_key_arn
  api_role_name = module.api_service.lambda_role_name
  # The packet worker writes its assembled zips here (issue #96) — the same
  # role case_store's worker grant names.
  worker_role_name = module.job_pipeline.worker_role_name

  # Both origins staging already admits elsewhere: the deployed app, and the
  # localhost dev server staging's pool registers (insolvia-services) and the API's own CORS
  # allowlist permits outside production. A developer running the app against
  # staging must be able to complete an upload, or "it works locally" and "it
  # works on staging" stop being the same statement.
  cors_allowed_origins = [
    "https://${var.subdomain}",
    "http://localhost:3000",
  ]

  # Synthetic documents only, and a teardown must not need the bucket emptied
  # by hand first. Prod inverts this.
  force_destroy = true
  tags          = local.common_tags
}

# ── The filing worker (ADR 0024 PR 7) ───────────────────────────
# services/filing's Lambda on the filing queue, under the role
# module.filing_credentials created for it. Staging has NO fake court (the
# fake is dev/CI only — services/filing/README.md says why) and no verified
# training database yet (ADR 0024 PR 10), so the fence allows no host here and
# every job hands back before a credential is opened. The kill switch is
# created off. First apply in a fresh account: the image bootstrap at the top
# of modules/filing_worker/main.tf.
module "filing_worker" {
  source = "../../modules/filing_worker"

  project                   = "insolvia"
  environment               = local.environment
  insolvia_env              = "staging"
  ecr_repository_url        = data.aws_ecr_repository.service["filing-dotnet"].repository_url
  image_tag                 = local.environment
  worker_role_name          = module.filing_credentials.worker_role_name
  worker_role_arn           = module.filing_credentials.worker_role_arn
  queue_arn                 = module.filing_queue.queue_arn
  dlq_name                  = module.filing_queue.dlq_name
  case_kms_key_arn          = module.case_store.kms_key_arn
  case_document_bucket_arn  = module.case_documents.bucket_arn
  case_document_bucket_name = module.case_documents.bucket_name
  alarms_topic_arn          = module.api_service.alarms_topic_arn

  # The switch (plan 18.22): the .NET worker in the VPC, against Aurora as
  # the `filing` role, naming both of its keys explicitly.
  vpc_subnet_ids         = module.network.private_subnet_ids
  vpc_security_group_ids = [module.network.lambda_security_group_id]
  db_cluster_resource_id = module.aurora.cluster_resource_id
  db_endpoint            = module.aurora.endpoint
  db_port                = module.aurora.port
  db_database_name       = module.aurora.database_name
  case_kms_key_alias     = module.case_store.kms_key_alias
  filing_kms_key_alias   = module.filing_credentials.kms_key_alias

  tags = local.common_tags
}

# ── MCP configuration namespace (#261/#262) ─────────────────────
# The /insolvia/<env>/mcp siblings of the api namespace, env-level because
# they reference module.auth: the MCP deploy step derives each into the
# service's environment (last segment, upper-cased, hyphens to underscores).
# The table names it once carried went with the switch (ADR 0028): the MCP
# host reads the database contract, /insolvia/<env>/db/*, instead.
resource "aws_ssm_parameter" "mcp_auth_issuer_url" {
  name  = "/insolvia/${local.environment}/mcp/auth-issuer-url"
  type  = "String"
  value = module.auth.issuer_url
  tags  = local.common_tags
}

# The client-id allowlist (#261): derived from the registered harness
# clients, so registering a harness in modules/auth IS adding it to the
# allowlist — never hand-listed. Comma-joined because a Lambda environment
# variable is one string; services/mcp's config splits it back.
resource "aws_ssm_parameter" "mcp_auth_client_ids" {
  name  = "/insolvia/${local.environment}/mcp/auth-client-ids"
  type  = "String"
  value = join(",", module.auth.mcp_client_ids)
  tags  = local.common_tags
}
# The document bucket's name, same namespace and same reasoning as the two
# above — the API derives it as CASE_DOCUMENT_BUCKET. The KEY ARN is
# deliberately not published here either: S3 resolves the key from the bucket's
# default encryption, so nothing in services/api ever names it.
resource "aws_ssm_parameter" "case_document_bucket" {
  name  = "/insolvia/${local.environment}/api/case-document-bucket"
  type  = "String"
  value = module.case_documents.bucket_name
  tags  = local.common_tags
}

# Case-data audit trail (issue 8.2): CloudTrail data events on the case table,
# into an insolvia-<env>-audit bucket under its own key. Declared after
# module.case_store because it records that module's table.
#
# The key is separate from the case key by necessity as well as design:
# ci-trust denies the deploy role kms:GenerateDataKey on alias/insolvia-*-cases,
# so a trail pointed at the case key fails at CreateTrail. The upside is that
# the pipeline can write this log and has no kms:Decrypt for audit keys
# anywhere, so it can never read back what it recorded.
# Staging keeps the minimum useful window — it holds synthetic cases, and the
# trail is here to prove the wiring works rather than to be evidence.

module "audit_trail" {
  source = "../../modules/audit_trail"

  project     = "insolvia"
  environment = local.environment

  # The case table, and its indexes via starts_with; the filing-credential
  # vault's table (ADR 0024, guardrail 3).
  data_resource_arns = [
    module.case_store.table_arn,
    module.filing_credentials.table_arn,
  ]

  # The vault KEY's seals and opens are management events (the module's
  # selector note) — this is what puts them in the retained record.
  include_management_events = true

  retention_days = 90
  tags           = local.common_tags
}

# ── The shared seed-fixture bucket ──────────────────────────────
# Owned by insolvia-services' envs/shared (module.dev_fixtures); resolved here the way every
# shared resource is — a hard-failing data lookup, never shared's state — so
# the seed step can read its name from this env's outputs alongside the
# tables it loads into. Named deterministically by the module, which is why a
# lookup by name is honest here.
data "aws_s3_bucket" "dev_fixtures" {
  bucket = "insolvia-shared-dev-fixtures-${var.aws_region}"
}

# ── Network and database (ADR 0028, plan 18.5) ──────────────────
# The VPC and the Aurora cluster the .NET backend runs on. Since the switch
# (18.22) every backend Lambda above runs in the private subnets under the
# Lambda security group, and logs in as its own Postgres role; the migrator
# below creates the schema and the roles before each alias shift. (Terraform
# orders by reference, not by position — the service modules above read
# module.network and module.aurora.)
#
# modules/network: two AZs of private subnets, one fck-nat instance for egress,
# free S3 and DynamoDB gateway endpoints. modules/aurora: Serverless v2
# PostgreSQL 17 under its own key, 5432 admitted from the Lambda security group
# only. Staging is disposable: no deletion protection, a short backup window, the 7-day key deletion window, and a 2-ACU ceiling.
module "network" {
  source = "../../modules/network"

  project     = "insolvia"
  environment = local.environment
  vpc_cidr    = "10.10.0.0/16"
  tags        = local.common_tags
}

module "aurora" {
  source = "../../modules/aurora"

  project                     = "insolvia"
  environment                 = local.environment
  vpc_id                      = module.network.vpc_id
  subnet_ids                  = module.network.private_subnet_ids
  lambda_security_group_id    = module.network.lambda_security_group_id
  min_capacity                = 0
  max_capacity                = 2
  seconds_until_auto_pause    = 900
  backup_retention_days       = 3
  deletion_protection         = false
  key_deletion_window_in_days = 7
  apply_immediately           = true
  tags                        = local.common_tags
}

# The database's SSM contract, /insolvia/<env>/db/*, in the convention the
# /api/* parameters above follow: written at env level, derived by a service's
# deploy step into its environment (last segment, upper-cased, hyphens to
# underscores — DB_ENDPOINT, DB_CLUSTER_RESOURCE_ID, …). None of these is a
# secret: the master credential is only ARN-referenced, and its value lives in
# the RDS-managed secret that only the migrator may read.
#
# cluster-resource-id is what an rds-db:connect ARN names, which is why it is
# published rather than the identifier.
resource "aws_ssm_parameter" "db" {
  for_each = {
    "endpoint"            = module.aurora.endpoint
    "port"                = tostring(module.aurora.port)
    "database-name"       = module.aurora.database_name
    "cluster-resource-id" = module.aurora.cluster_resource_id
    "master-secret-arn"   = module.aurora.master_secret_arn
  }

  name  = "/insolvia/${local.environment}/db/${each.key}"
  type  = "String"
  value = each.value
  tags  = local.common_tags
}

# ── The .NET hosts' configuration (ADR 0028, plan 18.22) ────────
# Each host's namespace is its complete configuration contract, derived on
# every deploy by .github/scripts/lambda-environment.sh (last segment,
# upper-cased, hyphens to underscores) together with the database contract
# /insolvia/<env>/db/* above (as INSOLVIA_DB_*). Env-level, like the auth and
# queue parameters, because each value is another module's output.
#
# The key aliases are explicit since the switch: the .NET hosts used to be
# able to derive them from CASE_TABLE_NAME, which names a DynamoDB table
# that no host reads now. backend/src/Insolvia.Aws/Sealing/SealingKeys.cs
# refuses any value not shaped alias/insolvia-<env>-cases /
# -filing-credentials, which is also what ci-trust's decrypt denies match.
resource "aws_ssm_parameter" "api_key_aliases" {
  for_each = {
    "insolvia-kms-case-key-alias"   = module.case_store.kms_key_alias
    "insolvia-kms-filing-key-alias" = module.filing_credentials.kms_key_alias
  }

  name  = "/insolvia/${local.environment}/api/${each.key}"
  type  = "String"
  value = each.value
  tags  = local.common_tags
}

# The pipeline worker's namespace (modules/job_pipeline writes insolvia-env
# and insolvia-db-username into it): the bucket it reads and writes packets
# in, and the case key it opens tax ids with for B121.
resource "aws_ssm_parameter" "worker_config" {
  for_each = {
    "case-document-bucket"        = module.case_documents.bucket_name
    "insolvia-kms-case-key-alias" = module.case_store.kms_key_alias
  }

  name  = "${module.job_pipeline.worker_ssm_parameter_prefix}/${each.key}"
  type  = "String"
  value = each.value
  tags  = local.common_tags
}

# ── The migrator (ADR 0028 § Runtime shape) ─────────────────────
# The one-shot Lambda CI invokes before every alias shift: migrations, the
# per-service roles and grants — and on staging, the seed (seeds/staging.json
# and the fixture versions it names), which replaced the Python seeder the
# staging seed role ran from a runner. modules/migrator owns the reasoning.
module "migrator" {
  source = "../../modules/migrator"

  project                  = "insolvia"
  environment              = local.environment
  insolvia_env             = "staging"
  ecr_repository_url       = data.aws_ecr_repository.service["migrator-dotnet"].repository_url
  image_tag                = local.environment
  vpc_subnet_ids           = module.network.private_subnet_ids
  vpc_security_group_ids   = [module.network.lambda_security_group_id]
  master_secret_arn        = module.aurora.master_secret_arn
  db_cluster_resource_id   = module.aurora.cluster_resource_id
  seed                     = true
  user_pool_arn            = module.auth.user_pool_arn
  fixture_bucket_arn       = data.aws_s3_bucket.dev_fixtures.arn
  case_document_bucket_arn = module.case_documents.bucket_arn
  case_kms_key_arn         = module.case_store.kms_key_arn
  tags                     = local.common_tags
}

# What the seed reaches outside the database (modules/migrator's header).
resource "aws_ssm_parameter" "migrator_config" {
  for_each = {
    "auth-user-pool-id"            = module.auth.user_pool_id
    "case-document-bucket"         = module.case_documents.bucket_name
    "insolvia-seed-fixture-bucket" = data.aws_s3_bucket.dev_fixtures.bucket
    "insolvia-kms-case-key-alias"  = module.case_store.kms_key_alias
  }

  name  = "${module.migrator.ssm_parameter_prefix}/${each.key}"
  type  = "String"
  value = each.value
  tags  = local.common_tags
}
