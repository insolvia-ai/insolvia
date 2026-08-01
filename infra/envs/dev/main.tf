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
#   • a Cognito pool via modules/auth, prepping local auth work (outputs
#     only — nothing consumes it yet).
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
