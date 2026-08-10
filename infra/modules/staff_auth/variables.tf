variable "project" {
  description = "Project slug, used in resource names."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Infra environment name (dev-<id>, staging, prod), used in resource names."
  type        = string
}

# Browser origins the admin portal client may redirect to. Same contract as
# modules/auth's web_origins: the module derives <origin>/auth/callback and the
# sign-out URL from each entry, and Cognito matches EXACTLY — which is why the
# dev origin pins a port (the portal's dev server owns :3100; the app owns
# :3000).
variable "web_origins" {
  description = "Admin portal origins allowed as OAuth redirect targets (scheme + host + optional port, no path, no trailing slash)."
  type        = list(string)

  validation {
    condition     = alltrue([for o in var.web_origins : can(regex("^https?://[^/]+$", o))])
    error_message = "Each web origin must be scheme://host[:port] with no path and no trailing slash."
  }
}

# ACTIVE on prod, INACTIVE elsewhere. The prod staff pool holds the accounts
# that can provision and suspend every tenant; a destroy must fail loudly.
variable "deletion_protection" {
  description = "Whether the staff pool is protected from deletion (true on prod)."
  type        = bool
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}
