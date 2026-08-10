variable "project" {
  description = "Project slug, used in resource names."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Infra environment name (staging, prod), used in resource names."
  type        = string
}

# Browser origins the web SPA client may redirect to, e.g.
# ["https://app.insolvia.ai"]. The module derives the exact callback URL
# (<origin>/auth/callback) and sign-out URL (<origin>) from each entry, so the
# path contract lives in one place. Staging appends a fixed localhost dev
# origin; prod registers production only.
#
# A native (mobile/desktop) client would NOT go through this variable: it needs
# a custom scheme, `insolvia://auth/callback`, not an http(s) origin — see the
# header comment in main.tf. The regex below rejects one on purpose.
variable "web_origins" {
  description = "Web app origins allowed as OAuth redirect targets (scheme + host + optional port, no path, no trailing slash)."
  type        = list(string)

  validation {
    condition     = alltrue([for o in var.web_origins : can(regex("^https?://[^/]+$", o))])
    error_message = "Each web origin must be scheme://host[:port] with no path and no trailing slash."
  }
}

# ── Custom auth domain (optional) ───────────────────────────────
# Set on staging and prod; NULL on dev, which keeps the Cognito prefix domain.
#
# Dev is deliberately excluded rather than overlooked. A custom domain takes
# 15-20 minutes to create and the same to delete, needs a DNS record, and is
# per-pool — so a per-developer-machine one would mean a quarter-hour added to
# every `dev-aws-setup.sh` and a Route53 record per machine. The prefix domain
# is fully functional; only the address bar differs.
#
# One label only: the shared certificate is `*.insolvia.ai`, which covers
# `auth.insolvia.ai` but NOT `auth.staging.insolvia.ai`. Hence the flat
# `staging-auth` naming, matching `staging-app` and `staging-api`.
variable "custom_domain" {
  description = "Custom auth hostname, e.g. auth.insolvia.ai. Null keeps the Cognito prefix domain."
  type        = string
  default     = null

  validation {
    condition     = var.custom_domain == null || can(regex("^[a-z0-9-]+\\.[a-z0-9.-]+$", var.custom_domain))
    error_message = "custom_domain must be a bare hostname (no scheme, no path, no trailing dot)."
  }
}

# Required whenever custom_domain is set. Cognito puts the certificate on a
# CloudFront distribution it manages, so it MUST live in us-east-1 regardless of
# where the pool does — see docs/reference/terraform.md.
variable "certificate_arn" {
  description = "us-east-1 ACM certificate covering custom_domain. Required when custom_domain is set."
  type        = string
  default     = null
}

variable "hosted_zone_id" {
  description = "Route53 zone to create custom_domain's alias record in. Required when custom_domain is set."
  type        = string
  default     = null
}

# ACTIVE on prod, INACTIVE on staging. The prod pool holds real attorney
# accounts — a `terraform destroy` (or a plan that replaces the pool) must
# fail loudly instead of silently deleting every user.
variable "deletion_protection" {
  description = "Whether the user pool is protected from deletion (true on prod)."
  type        = bool
}

# ── Outbound email (#210) ───────────────────────────────────────
# Null → Cognito's default sender (no-reply@verificationemail.com, 50/day,
# unaffected by the SES sandbox — the right answer for dev). Set → DEVELOPER
# mode through the account's SES identity, so invites and reset codes leave as
# no-reply@insolvia.ai. Constructed by the caller the way modules/mailer
# builds its identity ARN — arn:aws:ses:<region>:<account>:identity/<domain> —
# never remote state.
#
# Sequencing, because SES is sandboxed until #211: staging sets this and tests
# with sandbox-verified recipients; PROD STAYS NULL until production access is
# granted — in the sandbox, an invite to an unverified prod mailbox is
# silently undeliverable, which is strictly worse than the default sender.
variable "ses_source_arn" {
  description = "SES identity ARN Cognito sends through (DEVELOPER mode), or null for the Cognito default sender."
  type        = string
  default     = null
}

variable "from_email_address" {
  description = "From header when ses_source_arn is set. Must be on the SES identity's domain."
  type        = string
  default     = "Insolvia <no-reply@insolvia.ai>"
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}

# ── The API's invite grant ──────────────────────────────────────
# Name of the API's Lambda execution role, or null where there is no Lambda
# (infra/envs/dev — the local API runs as a dev server under the developer's
# own IAM user). Same optional shape, and the same reason, as
# modules/case_store's.
#
# Looked up by name rather than passed as an ARN so this module does not gain a
# dependency on api_service, which already depends on it.
variable "api_role_name" {
  description = "API Lambda execution role name to grant AdminCreateUser on this pool, or null."
  type        = string
  default     = null
}
