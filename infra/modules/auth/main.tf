# Cognito auth for the Insolvia app (#65): one user pool per environment
# (insolvia-users-<env>), a Cognito-provided hosted domain, and two public
# PKCE app clients — one for the web SPA, one for the desktop app's loopback
# flow. Mirrors the andreas-services website auth module's house style
# (email-as-username, admin-only creation, SRP-only explicit flows), adapted
# from its server-side USER_PASSWORD_AUTH design to the OAuth
# authorization-code + PKCE flows a browser SPA and a native desktop app
# actually need.
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
# hosts the /oauth2/authorize, /oauth2/token, and sign-in pages both clients
# use. A custom domain (auth.insolvia.ai) is deferred: it needs its own
# us-east-1 ACM cert, an alias record, and buys only vanity — the prefix
# domain is fully functional and the client apps read the domain from config
# either way, so switching later is a config change, not a code change.

resource "aws_cognito_user_pool_domain" "main" {
  domain       = "${var.project}-${var.environment}"
  user_pool_id = aws_cognito_user_pool.main.id
}

# ── App clients ─────────────────────────────────────────────────
# Both clients are OAuth public clients (RFC 6749 §2.1): no secret, because a
# browser bundle and a desktop binary can both be unpacked and anything
# embedded in them read out. Both use the authorization-code grant; PKCE
# (RFC 7636) is the client's obligation — Cognito's authorize endpoint
# accepts and enforces a code_challenge when one is sent, but offers no
# server-side "require PKCE" toggle, so the app implementations MUST send
# one (both Flutter OAuth packages and every AppAuth port do by default).
#
# Refresh-token rotation is ENABLED on both: each refresh returns a new
# refresh token and retires the old one, so a stolen refresh token stops
# working as soon as the legitimate client refreshes. The 30 s grace period
# keeps a flaky network from locking the client out when a rotation response
# is lost in transit and the client retries with the "old" token.

locals {
  # The web SPA's redirect contract: the app must handle the code exchange at
  # <origin>/auth/callback and land sign-outs on the origin root. Derived
  # here, per origin, so staging's localhost dev origin gets the same paths
  # as the real one.
  web_callback_urls = [for o in var.web_origins : "${o}/auth/callback"]
  web_logout_urls   = var.web_origins

  # Desktop loopback redirects, the RFC 8252 §7.3 native-app pattern: the
  # app binds an HTTP listener on the loopback interface, opens the system
  # browser at /oauth2/authorize, and receives the code on the listener.
  #
  # Two Cognito constraints shape this list (verified against the
  # CreateUserPoolClient API reference, CallbackURLs):
  #
  #   1. Plain-HTTP callbacks are permitted ONLY for the loopback hosts
  #      `http://localhost`, `http://127.0.0.1`, and `http://[::1]`, with
  #      custom TCP ports allowed. We register the literal IP 127.0.0.1
  #      rather than the localhost hostname — RFC 8252 recommends it because
  #      it never touches the OS resolver (a hosts-file entry mapping
  #      localhost elsewhere would otherwise redirect the auth code), and the
  #      desktop app must bind and browse the SAME form, since Cognito
  #      string-matches the redirect_uri against this list.
  #
  #   2. Callback URLs are EXACT-match. RFC 8252 §8.3 tells authorization
  #      servers to allow any port on loopback redirects; Cognito does not —
  #      there is no wildcard-port form. The standard workaround is this
  #      small fixed port set: the desktop app must try to bind
  #      127.0.0.1:<port> for each port IN THIS ORDER and use the first that
  #      binds (four ports so one being occupied never blocks sign-in).
  #      The ports sit below 49152 deliberately: macOS and Windows hand out
  #      ephemeral (outbound) ports from 49152 up, so a port above that can
  #      be transiently held by any outgoing connection on the machine.
  #
  # These URLs are the contract with apps/insolvia_app's desktop sign-in:
  # change the ports or paths here and the app must change with them.
  desktop_callback_urls = [for p in var.desktop_loopback_ports : "http://127.0.0.1:${p}/callback"]
  desktop_logout_urls   = [for p in var.desktop_loopback_ports : "http://127.0.0.1:${p}/signout"]
}

# The web SPA (app.insolvia.ai / staging-app.insolvia.ai, Flutter web).
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

# The native desktop app (macOS/Windows, Flutter) — loopback-redirect PKCE.
resource "aws_cognito_user_pool_client" "desktop" {
  name         = "${var.project}-desktop-${var.environment}"
  user_pool_id = aws_cognito_user_pool.main.id

  generate_secret = false # public client — a shipped binary keeps no secrets

  allowed_oauth_flows_user_pool_client = true
  allowed_oauth_flows                  = ["code"]
  allowed_oauth_scopes                 = ["openid", "email", "profile"]
  supported_identity_providers         = ["COGNITO"]

  callback_urls = local.desktop_callback_urls
  logout_urls   = local.desktop_logout_urls

  # No ALLOW_REFRESH_TOKEN_AUTH for the same reason as the web client:
  # refresh_token_rotation below owns the refresh path, and Cognito rejects
  # the explicit flow when rotation is enabled.
  explicit_auth_flows = [
    "ALLOW_USER_SRP_AUTH",
  ]

  prevent_user_existence_errors = "ENABLED"
  enable_token_revocation       = true

  # Same 1 h access/ID tokens as the web client, but a 90-day refresh token:
  # the desktop app stores it in the OS keychain (not a browser), and
  # desktop-loyal attorneys expect an installed app to stay signed in — a
  # monthly full re-login is churn bait. Rotation (below) means the token at
  # rest is retired on every refresh anyway.
  access_token_validity  = 1
  id_token_validity      = 1
  refresh_token_validity = 90
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
