# Per-developer-machine development environment.
#
# One instance of this env exists PER DEVELOPER MACHINE, never shared and
# never touched by CI: every resource name carries the machine's short id
# (env suffix `dev-<machine_short_id>`), and every machine keeps its own
# state key — `insolvia/dev/<account-id>/<machine-id>/terraform.tfstate`,
# injected at init time by scripts/dev-aws-common.sh — so two developers can
# never collide on names or state. It is applied/destroyed exclusively by
# scripts/dev-aws-{setup,reset,destroy}.sh with the developer's own
# credentials (the andreas.savva IAM user); the GitHub OIDC deploy role never
# assumes it and no workflow applies it (the PR validate matrix only runs
# `terraform validate -backend=false` here, like every other env).
#
# Scope is deliberately tiny — only what local development actually consumes
# today (see docs/TERRAFORM_ARCHITECTURE.md):
#   • the waitlist DynamoDB table, so the compose stack can exercise the real
#     AWS adapter against a real table instead of dynamodb-local;
#   • a Cognito pool via modules/auth, prepping local auth work (outputs
#     only — nothing consumes it yet).
# No ECR/Lambda/API Gateway/S3: local dev runs the API via compose or the
# plain dev server, not Lambda.

locals {
  # dev-<machine_short_id> is this machine's environment name, slotting into
  # the repo-wide insolvia-<thing>-<env> convention.
  environment = "dev-${var.machine_short_id}"

  common_tags = {
    Project     = "insolvia"
    Environment = local.environment
    ManagedBy   = "terraform"
    # Ownership breadcrumbs, mirroring humbugg's dev env: enough to find the
    # human behind an orphaned resource without ever committing the UUID.
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
# registers for dev (`flutter run -d chrome --web-port 3000`); nothing
# deployed ever redirects here. Outputs only for now: this preps local auth
# work, the app does not consume it yet.

module "auth" {
  source = "../../modules/auth"

  project     = "insolvia"
  environment = local.environment

  web_origins = ["http://localhost:3000"]

  # Throwaway test accounts on a throwaway pool.
  deletion_protection = false

  tags = local.common_tags
}
