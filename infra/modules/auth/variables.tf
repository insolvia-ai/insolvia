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

# ACTIVE on prod, INACTIVE on staging. The prod pool holds real attorney
# accounts — a `terraform destroy` (or a plan that replaces the pool) must
# fail loudly instead of silently deleting every user.
variable "deletion_protection" {
  description = "Whether the user pool is protected from deletion (true on prod)."
  type        = bool
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}
