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
variable "web_origins" {
  description = "Web app origins allowed as OAuth redirect targets (scheme + host + optional port, no path, no trailing slash)."
  type        = list(string)

  validation {
    condition     = alltrue([for o in var.web_origins : can(regex("^https?://[^/]+$", o))])
    error_message = "Each web origin must be scheme://host[:port] with no path and no trailing slash."
  }
}

# The desktop app's loopback redirect ports — see the desktop client in
# main.tf for why this is a fixed list and not a wildcard. The default is the
# contract with apps/insolvia_app: the desktop sign-in flow must bind one of
# exactly these ports.
variable "desktop_loopback_ports" {
  description = "Fixed TCP ports registered as http://127.0.0.1:<port>/callback for the desktop PKCE flow. Cognito matches callback URLs exactly (no wildcard ports), so the desktop app must bind one of these."
  type        = list(number)
  default     = [41539, 41540, 41541, 41542]
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
