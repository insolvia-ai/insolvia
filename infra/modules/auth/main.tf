# Cognito auth — the CONSUMER half. The user pool, its hosted domains and the
# app clients are insolvia-services' (its infra/modules/auth, in its
# infra/envs/{dev,staging,prod}) since ADR 0026: auth is a shared service.
# What stayed here is what belongs to the consumer:
#
#   • the managed-login BRANDING of this app's two clients — generated from
#     this repo's brand by `npm run tokens` and gated by `npm run tokens:check`
#     (ADR 0020: the brand is a consumer-owned override);
#   • the AdminCreateUser GRANTS on this repo's API and admin Lambda roles — a
#     consumer grants its own role access to the pool, so insolvia-services
#     never needs one of our role names, and a fresh account has no cycle.
#
# Both reach the pool through modules/auth_contract — the SSM parameters
# insolvia-services publishes — whose outputs this module passes through
# under the names it always had (user_pool_id, web_client_id, …).
#
# Called as module "auth" by staging, prod and dev-shared, and the four
# resources above kept their addresses (module.auth.aws_cognito_managed_login_branding.*,
# module.auth.aws_iam_role_policy.*): when the pool moved, these stayed put in
# this repo's state, untouched; the addresses that left moved to
# insolvia-services' state on 2026-10-12. A developer machine's infra/envs/dev calls
# modules/auth_contract directly instead: it brands nothing (the shared dev
# pool's branding is dev-shared's, once for every machine) and grants nothing
# (no Lambda).

module "contract" {
  source = "../auth_contract"

  project     = var.project
  environment = var.environment
}

locals {
  # The pool's NAME is the invite grants' policy-name prefix, unchanged from
  # when this module created the pool, so the grants keep their names.
  pool_name = "${var.project}-${var.environment}-users"

  user_pool_id     = module.contract.user_pool_id
  user_pool_arn    = module.contract.user_pool_arn
  web_client_id    = module.contract.web_client_id
  portal_client_id = module.contract.portal_client_id
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
# ONE ASSET, THE WORDMARK, and no others. The export merges in Cognito's OWN
# illustrations — the email, SMS, passkey and password graphics, plus
# identity-provider button icons for providers this pool does not use. Those
# are AWS's artwork; declaring them here would commit someone else's assets to
# a public repo to no purpose. Assets we do not declare are left alone, so
# Cognito keeps supplying them, and the two we do declare replace nothing but
# the grey placeholder that used to sit in the form's logo slot.
#
# Dark mode is fully branded, and the dark primary button is brass rather than
# the inverted white-on-navy a hand-mapping reaches for. That is not a taste
# call made here: `semantic.primary.dark` already answers it, the app renders
# the same, and the sign-in page agreeing with the app matters more than this
# module having an opinion. (Navy would also have failed — 1.21 contrast on the
# dark page, against WCAG's 3.0 floor for a distinguishable control.)
resource "aws_cognito_managed_login_branding" "web" {
  user_pool_id = local.user_pool_id
  client_id    = local.web_client_id

  settings = file("${path.module}/managed-login-settings.json")

  # The wordmark above the form, and the favicon on the tab it opens in. Both
  # are GENERATED — `brand/wordmark.svg` and `brand/icon.svg` cut from the
  # display face by scripts/render-brand-marks.sh, then coloured into this
  # directory by `npm run tokens` and gated by `npm run tokens:check`. Never
  # hand-edit one; the next `npm run tokens` overwrites it.
  #
  # The wordmark is TWO assets, one per colour mode, because Cognito's
  # light/dark is its own toggle and an <img> served from AWS's origin can only
  # follow the OS. The favicon is one, because the tile is dark in both schemes
  # — tool/render-brand-marks.ts owns both arguments.
  #
  # The API takes base64 (<= 40 assets, 2 MB each); these are ~7 KB of outlines.
  dynamic "asset" {
    for_each = toset(["LIGHT", "DARK"])

    content {
      category   = "FORM_LOGO"
      color_mode = asset.value
      extension  = "SVG"
      bytes      = filebase64("${path.module}/wordmark-${lower(asset.value)}.svg")
    }
  }

  asset {
    category   = "FAVICON_SVG"
    color_mode = "DYNAMIC"
    extension  = "SVG"
    bytes      = filebase64("${path.module}/favicon.svg")
  }
}

# The managed login pages for the portal client. BRANDING IS PER APP CLIENT,
# and a client on managed login with none does not fall back to defaults — it
# serves "Login pages unavailable". The same generated settings and marks as the web client: one
# brand, one sign-in page, whichever door a person comes through.
resource "aws_cognito_managed_login_branding" "portal" {
  user_pool_id = local.user_pool_id
  client_id    = local.portal_client_id

  settings = file("${path.module}/managed-login-settings.json")

  dynamic "asset" {
    for_each = toset(["LIGHT", "DARK"])

    content {
      category   = "FORM_LOGO"
      color_mode = asset.value
      extension  = "SVG"
      bytes      = filebase64("${path.module}/wordmark-${lower(asset.value)}.svg")
    }
  }

  asset {
    category   = "FAVICON_SVG"
    color_mode = "DYNAMIC"
    extension  = "SVG"
    bytes      = filebase64("${path.module}/favicon.svg")
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
