# Cognito auth for the Insolvia app (#65): one user pool per environment
# (insolvia-users-<env>), a Cognito-provided hosted domain, and one public
# PKCE app client for the web SPA. The house style is email-as-username,
# admin-only creation, and SRP-only explicit flows, on the OAuth
# authorization-code + PKCE flows a browser SPA actually needs.
#
# A desktop or mobile client, if one is ever added, must register a CUSTOM
# SCHEME — `insolvia://auth/callback` — and NOT an RFC 8252 §7.3 loopback
# redirect (`http://127.0.0.1:<port>/callback`): the loopback pattern needs
# the app to bind an HTTP listener on 127.0.0.1, which a React Native runtime
# has no way to do without a native module, whereas a custom scheme is exactly
# what `expo-auth-session` + `app.json`'s `scheme` already produce on every
# platform, web included. Cognito accepts a custom scheme in `callback_urls`
# with no special handling.
#
# The API does NOT consume any of this yet — the waitlist stays public, and
# JWT verification arrives with the first authenticated endpoint. This module
# only establishes the seam: the `issuer_url` output is what that endpoint
# will validate tokens against.

data "aws_region" "current" {}

# ── User pool ───────────────────────────────────────────────────

resource "aws_cognito_user_pool" "main" {
  name = "${var.project}-users-${var.environment}"

  # Self-signup is DISABLED, deliberately: Insolvia's users are attorneys and
  # their staff, provisioned by us (aws cognito-idp admin-create-user, or the
  # admin tooling that grows around it) when a firm onboards. A public
  # sign-up form on a bankruptcy-filing platform would be an invitation to
  # junk accounts, not a growth channel.
  admin_create_user_config {
    allow_admin_create_user_only = true
  }

  # Email is the username. auto_verified_attributes lets an admin-created
  # user's verified email drive account recovery below.
  username_attributes      = ["email"]
  auto_verified_attributes = ["email"]

  # ACTIVE on prod (via the variable): deleting the pool deletes every
  # attorney account, so prod requires a two-step (flip this off, then
  # destroy) with a plan diff at each step.
  deletion_protection = var.deletion_protection ? "ACTIVE" : "INACTIVE"

  # ESSENTIALS is the default plan for new pools; pinned explicitly so a
  # future provider default change can't silently move the pool (and the
  # bill). Threat protection ("advanced security", the reference module's
  # era called it) now requires the PLUS plan at extra per-MAU cost — not
  # "cheap", so deferred until there are real accounts worth protecting.
  # ESSENTIALS already includes the modern auth baseline (token revocation,
  # refresh-token rotation below).
  user_pool_tier = "ESSENTIALS"

  # Same shape as the reference module: 12+ chars, mixed case + digits,
  # symbols not forced (length beats charset-composition rules; forcing
  # symbols mostly forces "Password1!"). The temporary password an admin
  # provisions with lives a week — onboarding is human-paced.
  password_policy {
    minimum_length                   = 12
    require_lowercase                = true
    require_uppercase                = true
    require_numbers                  = true
    require_symbols                  = false
    temporary_password_validity_days = 7
  }

  # Optional TOTP: attorneys can (and should) enroll an authenticator app,
  # but MFA is not forced at the door while the product has no settings UI
  # to manage enrollment. No SMS MFA — it needs an SNS/SMS setup and is the
  # weaker factor anyway.
  mfa_configuration = "OPTIONAL"
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
# Cognito-provided prefix domain: insolvia-<env>.auth.<region>.amazoncognito.com
# hosts the /oauth2/authorize, /oauth2/token, and sign-in pages. A custom domain
# (auth.insolvia.ai) is still deferred — it is a separate decision from branding
# and needs a CI-role IAM change first: Cognito provisions its own CloudFront
# distribution with the CALLER's credentials, so the deploy role would need
# cloudfront:UpdateDistribution, and that grant can only be applied by a human
# (see the insolvia-deploy-role-permissions skill). Branding, below, needs
# neither DNS nor new IAM and works on this prefix domain today.
#
# managed_login_version = 2 selects MANAGED LOGIN over the classic hosted UI.
# AWS calls the classic UI "a first-generation version of the managed login
# services" with "a simpler design and fewer features"; managed login is what
# carries the branding editor, dark mode, localisation, and the Terms/Privacy
# links at sign-up. It is available from the ESSENTIALS feature tier, which
# this pool is already on — no tier change, no extra cost.
#
# Two consequences worth knowing before touching this line:
#   • Switching branding version signs everyone out. AWS does not preserve
#     sessions across the switch, and it takes up to ~4 minutes to take effect.
#   • The rendered markup changes completely, so the staging E2E's hosted-UI
#     field selectors must be re-derived from the real page (see e2e/).
resource "aws_cognito_user_pool_domain" "main" {
  domain                = "${var.project}-${var.environment}"
  user_pool_id          = aws_cognito_user_pool.main.id
  managed_login_version = 2
}

# ── Managed login branding — STILL CONSOLE-OWNED ────────────────
# The branding style itself (colours, logo, dark mode) is still absent from this
# module, but the provider is no longer the reason:
# `aws_cognito_managed_login_branding` first shipped in AWS provider **v6.12.0**,
# and this repo now pins `~> 6.12` (infra/CLAUDE.md), so the resource validates
# today.
#
# What remains is that the style is DATA whose authoritative copy lives in the
# console's branding editor — the one part of auth that is not under IaC.
# Writing `settings` from scratch here would not codify the branding someone
# designed; it would overwrite it on the next apply. Two things follow:
#
#   • AWS documents that an app client created through the API — which is what
#     Terraform does — starts with NO branding style: "managed login isn't
#     available for an app client created with an AWS SDK until you create one
#     with a CreateManagedLoginBranding request." Opening the branding editor
#     and saving creates that style; until someone does, these pages render
#     Cognito's stock defaults.
#   • To codify it, export first — the console work is not thrown away.
#     `DescribeManagedLoginBrandingByClient` with `ReturnMergedResources`
#     exports the whole style as JSON, which becomes this module's `settings`
#     plus `asset` blocks (images committed here and shipped with filebase64 —
#     up to 40 assets, 2 MB each; Cognito stores and serves them, nothing is
#     hosted by us).

# ── App client ──────────────────────────────────────────────────
# An OAuth public client (RFC 6749 §2.1): no secret, because a browser bundle
# can be unpacked and anything embedded in it read out. It uses the
# authorization-code grant; PKCE (RFC 7636) is the client's obligation —
# Cognito's authorize endpoint accepts and enforces a code_challenge when one
# is sent, but offers no server-side "require PKCE" toggle, so the app
# implementation MUST send one (`expo-auth-session` does by default).
#
# Refresh-token rotation is ENABLED: each refresh returns a new refresh token
# and retires the old one, so a stolen refresh token stops working as soon as
# the legitimate client refreshes. The 30 s grace period keeps a flaky network
# from locking the client out when a rotation response is lost in transit and
# the client retries with the "old" token.

locals {
  # The web SPA's redirect contract: the app must handle the code exchange at
  # <origin>/auth/callback and land sign-outs on the origin root. Derived
  # here, per origin, so staging's localhost dev origin gets the same paths
  # as the real one.
  #
  # Cognito matches callback URLs EXACTLY — no wildcard host, path, or port.
  # That is why the dev origin has to pin a port (see the callers), and why a
  # future native client would register `insolvia://auth/callback` as its own
  # literal entry rather than anything pattern-shaped.
  web_callback_urls = [for o in var.web_origins : "${o}/auth/callback"]
  web_logout_urls   = var.web_origins
}

# The web SPA (app.insolvia.ai / staging-app.insolvia.ai, Expo web).
resource "aws_cognito_user_pool_client" "web" {
  name         = "${var.project}-web-${var.environment}"
  user_pool_id = aws_cognito_user_pool.main.id

  generate_secret = false # public client — see the header comment

  allowed_oauth_flows_user_pool_client = true
  allowed_oauth_flows                  = ["code"]
  allowed_oauth_scopes                 = ["openid", "email", "profile"]
  supported_identity_providers         = ["COGNITO"]

  callback_urls = local.web_callback_urls
  logout_urls   = local.web_logout_urls

  # Sign-in happens on the hosted domain, so the client needs no
  # password-carrying SDK flow — SRP for completeness. Deliberately NO
  # ALLOW_USER_PASSWORD_AUTH: nothing should ever ship a raw password
  # through this client. And NO ALLOW_REFRESH_TOKEN_AUTH: with
  # refresh_token_rotation enabled (below) Cognito REJECTS it as an explicit
  # flow at CreateUserPoolClient time ("not a permitted ExplicitAuthFlow
  # when refresh token rotation is enabled") — rotation owns the refresh
  # path. Found by the first dev-env apply; validate can't see it.
  explicit_auth_flows = [
    "ALLOW_USER_SRP_AUTH",
  ]

  prevent_user_existence_errors = "ENABLED"
  enable_token_revocation       = true

  # Token validities: 1 h access/ID tokens (the Cognito default, short
  # enough that revocation-by-rotation matters), 30-day refresh so an
  # attorney using the app weekly stays signed in, but an abandoned browser
  # session dies within a month.
  access_token_validity  = 1
  id_token_validity      = 1
  refresh_token_validity = 30
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
