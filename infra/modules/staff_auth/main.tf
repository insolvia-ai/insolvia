# Cognito auth for INSOLVIA STAFF (#209): one pool per environment
# (insolvia-staff-<env>), a Cognito-provided hosted domain, and one public PKCE
# app client for the admin portal SPA. The staff pool is what makes the admin
# service's trust boundary structural: it verifies tokens against THIS pool's
# issuer, the tenant API verifies against insolvia-users-<env>, and a firm
# user's token cannot reach an admin route because the issuer check itself
# refuses it — there is no role check to get wrong.
#
# A SEPARATE MODULE, not a second instantiation of modules/auth, deliberately.
# That module hard-codes the customer pool's name, carries the API's invite
# grant, and — the decisive part — every conditional added to it plans against
# the three real pools holding the customer identity store, where a slip is a
# pool REPLACEMENT that deletes every account (its own username_configuration
# comment records what that costs). The two pools also genuinely diverge: MFA
# is required here, no custom domain will ever exist here, and none of the
# invite machinery applies. What the modules deliberately SHARE is the branding
# document: tool/reconcile-cognito-branding.ts generates
# managed-login-settings.json into both module directories from the same
# tokens, and `npm run tokens:check` gates both, so the staff sign-in page
# cannot drift from the app's.
#
# Divergences from modules/auth, each a decision rather than an omission:
#
#   - `mfa_configuration = "ON"` — TOTP required, not optional. A staff account
#     provisions firms and suspends tenants; it is the highest-privilege
#     credential in the product. Required MFA is free on ESSENTIALS, and the
#     managed login runs the enrollment flow at first sign-in, so no settings
#     UI is needed on our side. (The customer pool keeps OPTIONAL until the app
#     grows an enrollment surface — its module explains.)
#   - NO custom-domain seam at all. The prefix domain
#     (insolvia-staff-<env>.auth.<region>.amazoncognito.com) is fully
#     functional and this is an internal tool for a handful of staff; a custom
#     domain's 15-20 minute create/delete and per-pool DNS record buy a nicer
#     address bar for people who already work here. Adding the seam back later
#     is copying two resources from modules/auth.
#   - NO invite grant seam. Staff accounts are created by a human running
#     scripts/dev-aws-create-staff-user.sh (dev) or the runbook's console
#     procedure (staging/prod) — no service ever holds a Cognito write on this
#     pool. The admin service's AdminCreateUser grant targets the CUSTOMER
#     pool and lives with it (modules/auth), arriving with the service's infra.

data "aws_region" "current" {}

# ── User pool ───────────────────────────────────────────────────

resource "aws_cognito_user_pool" "staff" {
  name = "${var.project}-staff-${var.environment}"

  # Staff accounts are provisioned by the maintainer, one at a time. Self
  # sign-up on the pool that administers every tenant would be absurd; this is
  # the same posture as the customer pool, for stronger reasons.
  admin_create_user_config {
    allow_admin_create_user_only = true
  }

  username_attributes      = ["email"]
  auto_verified_attributes = ["email"]

  # Case-insensitive from birth. The customer pools had to be REPLACED to gain
  # this (issue #179) because the block is immutable; this pool starts with it
  # and never faces that choice.
  username_configuration {
    case_sensitive = false
  }

  deletion_protection = var.deletion_protection ? "ACTIVE" : "INACTIVE"

  # Same tier and reasoning as modules/auth: ESSENTIALS is the modern baseline
  # (token revocation, refresh rotation, managed login v2, required MFA); PLUS
  # threat protection stays deferred at this account size.
  user_pool_tier = "ESSENTIALS"

  password_policy {
    minimum_length                   = 12
    require_lowercase                = true
    require_uppercase                = true
    require_numbers                  = true
    require_symbols                  = false
    temporary_password_validity_days = 7
  }

  # REQUIRED, not optional — see the header. TOTP only; SMS is the weaker
  # factor and needs an SNS setup this account does not carry.
  mfa_configuration = "ON"
  software_token_mfa_configuration {
    enabled = true
  }

  account_recovery_setting {
    recovery_mechanism {
      name     = "verified_email"
      priority = 1
    }
  }

  tags = var.tags
}

# ── Hosted domain ───────────────────────────────────────────────
# Prefix domain only — insolvia-staff-<env>.auth.<region>.amazoncognito.com —
# and no custom-domain seam on purpose (see the header). managed_login_version
# and the depends_on carry modules/auth's hard-won ordering rule: a client on
# managed login with no branding style serves "Login pages unavailable", not a
# default, and sign-in is simply down.
resource "aws_cognito_user_pool_domain" "staff" {
  domain                = "${var.project}-staff-${var.environment}"
  user_pool_id          = aws_cognito_user_pool.staff.id
  managed_login_version = 2

  depends_on = [aws_cognito_managed_login_branding.web]
}

# ── Managed login branding ──────────────────────────────────────
# GENERATED — do not hand-edit. Same document, same rules, same generator as
# modules/auth's copy: tool/reconcile-cognito-branding.ts rewrites the colour
# slots of BOTH modules' managed-login-settings.json from the installed
# @insolvia-ai/tokens (`npm run tokens`), and `npm run tokens:check` fails CI
# when either has drifted. Colours only, no assets — modules/auth's branding
# comment owns the full argument.
resource "aws_cognito_managed_login_branding" "web" {
  user_pool_id = aws_cognito_user_pool.staff.id
  client_id    = aws_cognito_user_pool_client.web.id

  settings = file("${path.module}/managed-login-settings.json")
}

# ── App client ──────────────────────────────────────────────────
# The admin portal SPA: public client, authorization-code + PKCE, same redirect
# contract as the app (<origin>/auth/callback, sign-out to the origin root) so
# the portal reuses the app's session code unchanged. All client hardening
# mirrors modules/auth's web client — the comments there own the reasoning
# (no ALLOW_USER_PASSWORD_AUTH; no ALLOW_REFRESH_TOKEN_AUTH because rotation
# owns the refresh path and Cognito rejects the pair).
#
# Refresh validity is 1 DAY, not the app's 30: the portal keeps tokens in
# memory only (no persisted refresh token — ADR 0011), so a long validity
# would protect a token that dies with the tab anyway, and a short one bounds
# a leaked token's life on the highest-privilege client in the product.

locals {
  web_callback_urls = [for o in var.web_origins : "${o}/auth/callback"]
  web_logout_urls   = var.web_origins
}

resource "aws_cognito_user_pool_client" "web" {
  name         = "${var.project}-staff-web-${var.environment}"
  user_pool_id = aws_cognito_user_pool.staff.id

  generate_secret = false # public client — a browser bundle keeps no secret

  allowed_oauth_flows_user_pool_client = true
  allowed_oauth_flows                  = ["code"]
  allowed_oauth_scopes                 = ["openid", "email", "profile"]
  supported_identity_providers         = ["COGNITO"]

  callback_urls = local.web_callback_urls
  logout_urls   = local.web_logout_urls

  explicit_auth_flows = [
    "ALLOW_USER_SRP_AUTH",
  ]

  prevent_user_existence_errors = "ENABLED"
  enable_token_revocation       = true

  access_token_validity  = 1
  id_token_validity      = 1
  refresh_token_validity = 1
  token_validity_units {
    access_token  = "hours"
    id_token      = "hours"
    refresh_token = "days"
  }

  refresh_token_rotation {
    feature                    = "ENABLED"
    retry_grace_period_seconds = 30
  }
}
