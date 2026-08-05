# Per-developer-machine development environment.
#
# One instance of this env exists PER DEVELOPER MACHINE, never shared and
# never touched by CI: every resource name carries the machine's short id
# (env suffix `dev-<machine_short_id>`), and every machine keeps its own
# state key — `insolvia/dev/<account-id>/<machine-id>/terraform.tfstate`,
# injected at init time by scripts/dev-aws-common.sh — so two developers can
# never collide on names or state. It is applied/destroyed exclusively by
# scripts/dev-aws-{setup,reset,destroy}.sh with the developer's own
# credentials (the developer's own IAM user); the GitHub OIDC deploy role never
# assumes it and no workflow applies it (the PR validate matrix only runs
# `terraform validate -backend=false` here, like every other env).
#
# Scope is deliberately tiny — only what local development actually consumes
# today (see docs/reference/terraform.md):
#   • the waitlist DynamoDB table — local dev's actual database (there is no
#     local emulator; the compose stack's API talks straight to this table);
#   • the case store via modules/case_store — the same module staging and prod
#     instantiate, so local development exercises the real encrypted path
#     rather than an approximation of it;
#   • a Cognito pool via modules/auth, prepping local auth work (outputs
#     only — nothing consumes it yet);
#   • OPTIONALLY the case-data audit trail (-var=enable_audit_trail=true),
#     off by default because a per-developer trail is cost with no evidentiary
#     value — but standable-up, so modules/audit_trail can be exercised on a
#     laptop rather than first met on staging.
# No ECR/Lambda/API Gateway/S3: local dev runs the API via compose or the
# plain dev server, not Lambda, and the app runs on the Expo dev server.

locals {
  # dev-<machine_short_id> is this machine's environment name, slotting into
  # the repo-wide insolvia-<thing>-<env> convention.
  environment = "dev-${var.machine_short_id}"

  common_tags = {
    Project     = "insolvia"
    Environment = local.environment
    ManagedBy   = "terraform"
    # Ownership breadcrumbs: enough to find the human behind an orphaned
    # resource without ever committing the UUID.
    DeveloperMachineId = var.machine_id
    DeveloperPrincipal = var.aws_principal_arn
    MachineName        = var.machine_name
  }
}

# ── Waitlist storage ────────────────────────────────────────────
# Same schema as modules/api_service's table (generic PK/SK string keys,
# on-demand billing) so the service's DynamoDB adapter behaves identically
# against it. Two deliberate differences from the deployed table:
#   • PITR is OFF — this is throwaway dev data a developer wipes with
#     dev-aws-reset.sh; paying for recovery of it would be noise.
#   • No IAM grant — the developer's own credentials are the principal, so
#     there is no execution role to scope PutItem to.

resource "aws_dynamodb_table" "waitlist" {
  name         = "insolvia-waitlist-${local.environment}"
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

  server_side_encryption { enabled = true }
  tags = local.common_tags
}

# ── Case store (issue 8.2) ──────────────────────────────────────
# The SAME module staging and prod use, not a hand-rolled copy — which is the
# whole point. The table's keys, its owner index and its customer-managed key
# behave here exactly as they do deployed, so the DynamoDB adapter, the GSI
# query behind GET /v1/cases, and any KMS permission mistake all surface on a
# laptop instead of on staging.
#
# Three deliberate differences from the deployed instances, all for the same
# reason the waitlist table above diverges — this is throwaway data on a
# machine, not a case file:
#   • PITR off — a developer wipes this with dev-aws-reset.sh.
#   • Deletion protection off — dev-aws-destroy.sh has to be able to remove it.
#   • The minimum 7-day key deletion window, so a destroy does not leave a
#     30-day pending key (and a monthly charge) behind on every teardown.
#
# api_role_name is null: there is no Lambda here. The developer's own IAM user
# is the principal, and it reaches the key through the key policy's root
# delegation, exactly as an admin would.
module "case_store" {
  source = "../../modules/case_store"

  project                     = "insolvia"
  environment                 = local.environment
  api_role_name               = null
  point_in_time_recovery      = false
  deletion_protection         = false
  key_deletion_window_in_days = 7
  tags                        = local.common_tags
}

# ── Firms ───────────────────────────────────────────────────────
# The same module staging and prod instantiate, on the same case key, so local
# development resolves a caller through the real sparse index rather than a
# stub. api_role_name is null for the same reason the case store's is: there is
# no Lambda here.
module "firm_store" {
  source = "../../modules/firm_store"

  project                = "insolvia"
  environment            = local.environment
  aws_region             = var.aws_region
  kms_key_arn            = module.case_store.kms_key_arn
  api_role_name          = null
  point_in_time_recovery = false
  deletion_protection    = false
  tags                   = local.common_tags
}
# ── Case documents ──────────────────────────────────────────────
# The same module staging and prod instantiate, encrypting under the same case
# key, so local development exercises the real bucket policy — TLS-only,
# KMS-only writes, ACLs off — rather than a folder on disk. api_role_name is
# null here for the same reason the case store's is: there is no Lambda.
module "case_documents" {
  source = "../../modules/case_documents"

  project       = "insolvia"
  environment   = local.environment
  aws_region    = var.aws_region
  kms_key_arn   = module.case_store.kms_key_arn
  api_role_name = null
  # A developer's bucket must be destroyable without emptying it by hand.
  force_destroy = true
  tags          = local.common_tags
}

# ── Auth ────────────────────────────────────────────────────────
# The same module staging and prod instantiate, with the machine environment
# name. The Cognito hosted-domain prefix the module derives
# (insolvia-dev-<machine_short_id>) is GLOBALLY unique across all of AWS —
# the machine short id in it is what makes a per-developer pool safe to
# create at all. Registers only the localhost web origin staging also
# registers for dev (`npx expo start --web --port 3000` — the port is pinned
# to match, since Cognito callback URLs are exact-match); nothing deployed
# ever redirects here. Outputs only for now: this preps local auth work, the
# app does not consume it yet.

module "auth" {
  source = "../../modules/auth"

  project     = "insolvia"
  environment = local.environment

  web_origins = ["http://localhost:3000"]

  # Throwaway test accounts on a throwaway pool.
  deletion_protection = false

  tags = local.common_tags
}

# ── API auth configuration ──────────────────────────────────────
# The same /insolvia/<env>/api/<kebab-key> parameters staging and prod publish
# (issue #79), under this machine's own environment name. No Lambda reads them
# here — there is none in dev — but they keep the namespace identical across
# every environment, so "where does AUTH_ISSUER_URL come from" has one answer
# rather than two. For the local API, the values are also available directly
# as this env's auth_issuer_url / auth_web_client_id outputs; export them into
# services/api/.env to run the compose stack or the dev server against this
# machine's pool.

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

# Same reasoning as the auth parameters above: nothing reads it here, but
# keeping the namespace identical across every environment means "where does
# CASE_TABLE_NAME come from" has one answer rather than two. The value a
# developer actually consumes is the case_table_name output, which
# dev-aws-setup.sh writes into services/api/.env.
resource "aws_ssm_parameter" "case_table_name" {
  name  = "/insolvia/${local.environment}/api/case-table-name"
  type  = "String"
  value = module.case_store.table_name
  tags  = local.common_tags
}

# The access log's table name, same namespace and same reasoning as
# case-table-name above — the API derives it as CASE_ACCESS_LOG_TABLE_NAME.
resource "aws_ssm_parameter" "case_access_log_table_name" {
  name  = "/insolvia/${local.environment}/api/case-access-log-table-name"
  type  = "String"
  value = module.case_store.access_log_table_name
  tags  = local.common_tags
}

# ── Case-data audit trail (issue 8.2), opt-in ───────────────────
# Off unless -var=enable_audit_trail=true. See the variable's own description
# for why the default is off and why the module is still worth being able to
# stand up here: it is how a change to modules/audit_trail gets tested before
# staging sees it, rather than after.
#
# Retention is the module's 90-day floor — the shortest the module allows,
# since nothing here is evidence.
module "audit_trail" {
  count  = var.enable_audit_trail ? 1 : 0
  source = "../../modules/audit_trail"

  project                     = "insolvia"
  environment                 = local.environment
  data_resource_arns          = [module.case_store.table_arn]
  retention_days              = 90
  key_deletion_window_in_days = 7
  tags                        = local.common_tags
}
