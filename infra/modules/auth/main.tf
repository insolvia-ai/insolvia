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

# The branded email bodies (#210). Inline HTML rather than templatefile():
# each is one small paragraph-block whose only variables are Cognito's own
# {username}/{####} placeholders — which must reach Cognito literally, so a
# Terraform template would spend its complexity escaping them. Copy changes
# are code-reviewed diffs here, same as everything else in this module.
#
# Kept deliberately spare: no images, no links to click. A credential email
# that trains recipients to click buttons is phishing practice; this one tells
# them what happened and what the temporary password is, and the sign-in page
# is wherever their firm already goes.
locals {
  invite_email_message = join("", [
    "<p>Hello,</p>",
    "<p>An account has been created for you on <b>Insolvia</b>, the bankruptcy case preparation platform your firm uses.</p>",
    "<p>Your sign-in name is {username} and your temporary password is <b>{####}</b>. It expires in 7 days — when you first sign in, you will choose a password of your own.</p>",
    "<p>If you were not expecting this invitation, you can ignore this email.</p>",
    "<p>— Insolvia</p>",
  ])

  verification_email_message = join("", [
    "<p>Your Insolvia verification code is <b>{####}</b>.</p>",
    "<p>If you did not request this code, you can ignore this email — nothing changes without it.</p>",
    "<p>— Insolvia</p>",
  ])
}

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

    # The email that delivers a new user's temporary password (#210) — the
    # first thing an invited attorney ever receives from Insolvia, replacing
    # Cognito's stock "Your username is... your temporary password is..." text.
    # Templates are independent of WHO SENDS (email_configuration below), so
    # they land on every environment immediately, default sender included.
    # Cognito requires both {username} and {####} in the message.
    invite_message_template {
      email_subject = "Welcome to Insolvia — your account is ready"
      email_message = local.invite_email_message
      # Never sent — no SMS delivery is configured anywhere — but the provider
      # transmits an empty string when the attribute is omitted and Cognito
      # rejects that ("length greater than or equal to 6"). Found by a real
      # dev apply; validate cannot see it. Must contain both placeholders.
      sms_message = "Insolvia: sign-in {username}, temporary password {####}"
    }
  }

  # The code emails: attribute verification and — the one that matters today —
  # the forgot-password code the managed login's own "Forgot your password?"
  # flow sends. Same stock-text-to-branded upgrade as the invite above.
  verification_message_template {
    default_email_option = "CONFIRM_WITH_CODE"
    email_subject        = "Your Insolvia verification code"
    email_message        = local.verification_email_message
  }

  # WHO SENDS (#210): unset, Cognito's default sender —
  # no-reply@verificationemail.com, capped at 50 emails/day, and the right
  # answer for dev, where the default sender delivers to any address with no
  # SES sandbox in the way. Set, mail leaves as no-reply@insolvia.ai through
  # the account's own SES identity (DEVELOPER mode). Two consequences the
  # variable's docs spell out: SES sandbox rules then apply (an invite to an
  # unverified address silently fails to deliver until production access,
  # #211), and the FIRST DEVELOPER-mode pool in the account needs the Cognito
  # email service-linked role to exist — a one-time human CLI, because the
  # deploy role deliberately cannot create service-linked roles for Cognito
  # (docs/runbooks/aws-bootstrap.md § "Cognito email service-linked role").
  dynamic "email_configuration" {
    for_each = var.ses_source_arn == null ? [] : [var.ses_source_arn]
    content {
      email_sending_account = "DEVELOPER"
      from_email_address    = var.from_email_address
      source_arn            = email_configuration.value
    }
  }

  # Email is the username. auto_verified_attributes lets an admin-created
  # user's verified email drive account recovery below.
  username_attributes      = ["email"]
  auto_verified_attributes = ["email"]

  # USERNAMES ARE MATCHED CASE-INSENSITIVELY, and this block is the only way to
  # say so. Cognito's LEGACY default — what you get when this block is absent —
  # is case-SENSITIVE, which for a pool whose username IS an email address
  # means `Alice@firm.com` and `alice@firm.com` are two different accounts and
  # an attorney who types a capital on the sign-in page is told they do not
  # exist. Measured against the real staging pool rather than inferred:
  # creating `a@example.invalid` and then `A@EXAMPLE.INVALID` produced two
  # accounts, no exception, each with its own subject.
  #
  # THIS BLOCK IS IMMUTABLE. AWS accepts it only at creation, so the provider
  # marks it ForceNew — adding it REPLACES the pool and deletes every account
  # in it. That is why it was not simply set when the pool was written, and why
  # it is being set now rather than later: the cost is every account plus every
  # row keyed on a Cognito subject, and both of those only grow.
  #
  # What "every row keyed on a subject" means, because it is the half that is
  # easy to miss: a replaced pool mints NEW subs, so `firm_user` rows
  # (insolvia-firms-<env>, keyed on the subject), `case.createdBy` and every
  # case assignment would all point at identities that no longer exist. Those
  # tables are empty today. After the first firm is onboarded this stops being
  # a pool replacement and becomes a data migration.
  username_configuration {
    case_sensitive = false
  }

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

  # Order the branding style BEFORE this flips to managed login. Nothing in the
  # attribute references would do it — the branding hangs off the app client,
  # not the domain — so without this the two are independent and Terraform may
  # do them in either order.
  #
  # It matters because the failure is an outage, not a cosmetic gap: a client on
  # managed login with no branding style does not fall back to defaults, it
  # serves "Login pages unavailable. Please contact an administrator." and
  # sign-in is simply down. That is exactly how staging broke — the version was
  # flipped while the style still lived in a system Terraform could not reach.
  #
  # This matters most for `envs/dev`, where every developer applies this module
  # to their own pool from their own machine: a partial apply there would break
  # local sign-in per machine, with no CI run to notice. Depending on the
  # branding makes "managed login is on but has nothing to render" unreachable
  # rather than merely unlikely.
  depends_on = [aws_cognito_managed_login_branding.web]
}

# ── Custom auth domain (staging, prod) ──────────────────────────
# `auth.insolvia.ai` instead of `insolvia-prod.auth.<region>.amazoncognito.com`.
# Sign-in is the one screen where an unfamiliar domain reads as phishing, and
# AWS's own guidance is that a custom domain "improves user trust ... especially
# when the root domain matches the domain that hosts your application".
#
# ADDED ALONGSIDE the prefix domain, not instead of it. A pool may hold one of
# each, and keeping both makes the cutover free: the app moves when its build
# picks up the new `domain` output, and the prefix keeps serving until then.
# Replacing would mean deleting the prefix domain first and waiting out the
# create — sign-in down for the duration.
#
# Two costs of a custom domain, both real:
#   • Create and delete each take 15-20 minutes, and Terraform blocks. Plan
#     accordingly; this is not a resource to churn.
#   • Cognito serves `/.well-known/openid-configuration` from the CUSTOM domain
#     only once one exists. Nothing here reads it — the API verifies against
#     `issuer_url`, which is the pool's own endpoint and unaffected — but an
#     OIDC client added later must point at the custom domain.
#
# The parent domain needs an A record or Cognito refuses to create this, as
# protection against hijacking a domain you do not control. `insolvia.ai`
# already has one (the marketing apex alias); an SOA record would not count.
resource "aws_cognito_user_pool_domain" "custom" {
  count = var.custom_domain == null ? 0 : 1

  domain                = var.custom_domain
  user_pool_id          = aws_cognito_user_pool.main.id
  certificate_arn       = var.certificate_arn
  managed_login_version = 2

  # Same ordering rule as the prefix domain above: a domain on managed login
  # with no branding style serves "Login pages unavailable", not a default.
  depends_on = [aws_cognito_managed_login_branding.web]
}

# Cognito creates its own CloudFront distribution for the custom domain and
# hands back the hostname to point at. The zone id comes from the resource
# rather than the usual hard-coded CloudFront constant (Z2FDTNDATAQYW2) —
# provider 6 exposes it, and reading it beats copying a magic string.
resource "aws_route53_record" "auth" {
  count = var.custom_domain == null ? 0 : 1

  zone_id = var.hosted_zone_id
  name    = var.custom_domain
  type    = "A"

  alias {
    name                   = aws_cognito_user_pool_domain.custom[0].cloudfront_distribution
    zone_id                = aws_cognito_user_pool_domain.custom[0].cloudfront_distribution_zone_id
    evaluate_target_health = false
  }
}

# ── Managed login branding ──────────────────────────────────────
# The style the sign-in pages render with. Cognito serves the pages; nothing
# here is hosted by us. Requires provider >= 6.12.0 — one of the two reasons
# infra/CLAUDE.md pins a floor rather than `~> 6.0`; modules/case_store sets
# the other, and its 6.37.0 is the one the roots actually carry.
#
# `managed-login-settings.json` IS GENERATED — do not hand-edit it. JSON has no
# comment syntax and AWS rejects unknown keys, so the file cannot carry its own
# DO-NOT-EDIT banner; this comment is it.
#
# Its COLOURS come from `packages/insolvia_tokens/tokens.json` via
# `npm run tokens`, from the semantic layer (`primary`, `bg`, `line`, …) rather
# than palette names, so a re-brand stays a one-file change and this page can
# never drift from the app. `npm run tokens:check` gates that in CI.
#
# Its STRUCTURE — layout, border radii, which auth methods appear — is AWS's
# schema, owned by the console's branding editor. To change layout: edit in the
# console, re-export with DescribeManagedLoginBrandingByClient
# (ReturnMergedResources), commit, then re-run `npm run tokens` to restore the
# token colours over whatever the console wrote.
#
# COLOURS ONLY, NO ASSETS, on purpose — two separate reasons:
#
#   • No logo has been uploaded yet. The logo slots currently hold Cognito's
#     grey placeholder graphic, and committing that would make AWS's placeholder
#     the thing Terraform reapplies on every deploy and carries to prod. When a
#     real wordmark exists, it lands here as `asset` blocks
#     (filebase64, <= 40 assets, 2 MB each).
#   • The export merges in Cognito's OWN illustrations — the email, SMS,
#     passkey and password graphics, plus identity-provider button icons for
#     providers this pool does not use. Those are AWS's artwork; declaring them
#     here would commit someone else's assets to a public repo to no purpose.
#     Assets we do not declare are left alone, so Cognito keeps supplying them.
#
# Dark mode is fully branded, and the dark primary button is brass rather than
# the inverted white-on-navy a hand-mapping reaches for. That is not a taste
# call made here: `semantic.primary.dark` already answers it, the app renders
# the same, and the sign-in page agreeing with the app matters more than this
# module having an opinion. (Navy would also have failed — 1.21 contrast on the
# dark page, against WCAG's 3.0 floor for a distinguishable control.)
resource "aws_cognito_managed_login_branding" "web" {
  user_pool_id = aws_cognito_user_pool.main.id
  client_id    = aws_cognito_user_pool_client.web.id

  settings = file("${path.module}/managed-login-settings.json")
}

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
#     Reaching data needs a row in insolvia-firms-<env>, which is a different
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

  name = "invite-${aws_cognito_user_pool.main.name}"
  role = var.api_role_name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "InviteFirmUser"
        Effect   = "Allow"
        Action   = ["cognito-idp:AdminCreateUser"]
        Resource = aws_cognito_user_pool.main.arn
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

  name = "admin-invite-${aws_cognito_user_pool.main.name}"
  role = var.admin_invite_role_name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "ProvisionFirstAdministrator"
        Effect   = "Allow"
        Action   = ["cognito-idp:AdminCreateUser"]
        Resource = aws_cognito_user_pool.main.arn
        Condition = {
          Bool = { "aws:SecureTransport" = "true" }
        }
      },
    ]
  })
}
