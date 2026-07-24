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
