variable "aws_region" {
  description = "Default AWS region."
  type        = string
  default     = "us-east-1"
}

variable "domain_name" {
  description = "Apex domain for Insolvia."
  type        = string
  default     = "insolvia.ai"
}

# Flat `staging-app`, not nested `app.staging`: an ACM wildcard covers exactly
# one label, so `*.insolvia.ai` matches `staging-app.insolvia.ai` but not
# `app.staging.insolvia.ai` — nesting would force a second wildcard cert.
variable "subdomain" {
  description = "Hostname this environment serves."
  type        = string
  default     = "staging-app.insolvia.ai"
}

# Same flat-label reasoning as `subdomain`: `*.insolvia.ai` covers
# `staging-api.insolvia.ai`, but would not cover a nested `api.staging.…`.
variable "api_subdomain" {
  description = "Hostname the backend API serves in this environment."
  type        = string
  default     = "staging-api.insolvia.ai"
}

# Same flat-label reasoning again: `*.insolvia.ai` covers
# `staging-mailer-api.insolvia.ai`, not a nested `mailer-api.staging.…`.
variable "mailer_subdomain" {
  description = "Hostname the mailer API serves in this environment."
  type        = string
  default     = "staging-mailer-api.insolvia.ai"
}

# Same flat-label reasoning once more: `*.insolvia.ai` covers
# `staging-www.insolvia.ai`, not a nested `www.staging.…`. Prod serves the
# real `www.insolvia.ai` plus the apex; staging serves this host only.
variable "marketing_subdomain" {
  description = "Hostname the marketing site serves in this environment."
  type        = string
  default     = "staging-www.insolvia.ai"
}

variable "marketing_image_tag" {
  description = "ECR image tag the marketing SSR Lambda is seeded from (creation-time only; CI owns the running image afterwards). Defaults to this environment's moving marker tag — NOT `latest`, which under the shared insolvia-marketing repository would mean whatever any environment pushed last."
  type        = string
  default     = "staging"
}

# Same flat-label reasoning as `api_subdomain`: `*.insolvia.ai` covers
# `staging-admin-api.insolvia.ai`, not a nested one. The admin SERVICE's
# host (#213); the portal itself serves from admin_subdomain when its
# hosting lands (#215).
variable "admin_api_subdomain" {
  description = "Hostname the admin service serves in this environment."
  type        = string
  default     = "staging-admin-api.insolvia.ai"
}

# A PUBLIC value (it appears in every sign-in redirect the portal makes), so
# committed rather than secret-managed — #209 records the decision. One
# client per environment; this is staging's.
variable "google_admin_client_id" {
  description = "Google Workspace OAuth client id staff ID tokens must carry as their audience."
  type        = string
  default     = "925851246989-a4prtrjp0p5j1q71g8pbv4irqu7ibsce.apps.googleusercontent.com"
}

# The admin PORTAL's host (#215) — the static SPA; the admin SERVICE lives at
# admin_api_subdomain above. Same flat-label reasoning as every subdomain here.
variable "admin_subdomain" {
  description = "Hostname the admin portal serves in this environment."
  type        = string
  default     = "staging-admin.insolvia.ai"
}
