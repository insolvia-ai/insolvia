# Cognito auth — the CONSUMER half. The user pool, its hosted domains, its
# app clients and the sign-in page's style are insolvia-services' (its
# infra/modules/auth) — auth is a shared service, ADR 0026. What stays here is
# what belongs to this app rather than to the login service:
#
#   • the AdminCreateUser GRANTS on this repo's API and admin Lambda roles — a
#     consumer grants its own role access to the pool, so insolvia-services
#     never needs one of our role names, and a fresh account has no cycle;
#   • modules/auth_contract — the SSM parameters insolvia-services publishes —
#     whose outputs this module passes through under the names it always had
#     (user_pool_id, web_client_id, …).
#
# Called as module "auth" by staging and prod; the grants kept their
# addresses (module.auth.aws_iam_role_policy.*) through both moves. A
# developer machine's infra/envs/dev calls modules/auth_contract directly: it
# grants nothing (no Lambda).

module "contract" {
  source = "../auth_contract"

  project     = var.project
  environment = var.environment
}

locals {
  # The pool's NAME is the invite grants' policy-name prefix, unchanged from
  # when this module created the pool, so the grants keep their names.
  pool_name = "${var.project}-${var.environment}-users"

  user_pool_arn = module.contract.user_pool_arn
}

# ── The API's invite grant ──────────────────────────────────────
# Self-signup is off (`allow_admin_create_user_only` above), so when a firm
# admin adds a colleague through POST /v1/firm/users the API has to mint the
# pool account. This is the grant that lets it, and it is deliberately the
# narrowest one that works.
#
# WHAT THIS WIDENS, stated plainly because it is a real increase in what a
# compromised API Lambda can do: it can create accounts in this pool. Three
# things bound that.
#
#   - ONE ACTION. AdminCreateUser and nothing else. No AdminSetUserPassword,
#     no AdminInitiateAuth, no AdminDeleteUser, no AdminUpdateUserAttributes,
#     no AdminAddUserToGroup. Cognito emails the temporary password to the
#     invited address and nothing in the service ever sees it, so creating an
#     account is not a way to BECOME one — which is the difference between a
#     provisioning grant and an impersonation primitive.
#   - ONE POOL. Scoped to this pool's ARN, not a wildcard. The account's other
#     pools — a future customer-facing one, another environment's — are out of
#     reach.
#   - AN ACCOUNT ALONE GRANTS NOTHING. A pool user with no firm-user row can
#     sign in and is refused by every route (services/api's current_accessor).
#     Reaching data needs a row in insolvia-<env>-firms, which is a different
#     grant on a different table.
#
# The alternative considered and rejected: a separate, narrower invite Lambda,
# so the API role holds no Cognito write at all. It is the stronger design and
# it costs a second function, a second deploy path and an internal call for one
# endpoint. Revisit if this grant ever needs a second action — that is the
# signal that the invite flow has outgrown living here.
# The name in var.api_role_name is used directly, not looked up — see
# modules/case_store's comment for why a plan-time data lookup deadlocks a
# bootstrap apply.
resource "aws_iam_role_policy" "api_invite" {
  count = var.api_role_name == null ? 0 : 1

  name = "${local.pool_name}-invite"
  role = var.api_role_name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "InviteFirmUser"
        Effect   = "Allow"
        Action   = ["cognito-idp:AdminCreateUser"]
        Resource = local.user_pool_arn
        Condition = {
          Bool = { "aws:SecureTransport" = "true" }
        }
      },
    ]
  })
}

# ── The admin service's invite grant (#213, ADR 0011) ───────────
# The same one-action shape as api_invite above, on the admin service's own
# role: provisioning a firm's first administrator and re-sending a stranded
# invitation are BOTH AdminCreateUser (resend is MessageAction=RESEND), so
# the deliberately narrow grant survives a second consumer without widening —
# the "second action" revisit trigger recorded on api_invite is not tripped.
# Everything api_invite's comment says about what one action on one pool
# cannot become (an impersonation primitive) holds here unchanged.
resource "aws_iam_role_policy" "admin_invite" {
  count = var.admin_invite_role_name == null ? 0 : 1

  name = "${local.pool_name}-admin-invite"
  role = var.admin_invite_role_name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "ProvisionFirstAdministrator"
        Effect   = "Allow"
        Action   = ["cognito-idp:AdminCreateUser"]
        Resource = local.user_pool_arn
        Condition = {
          Bool = { "aws:SecureTransport" = "true" }
        }
      },
    ]
  })
}
