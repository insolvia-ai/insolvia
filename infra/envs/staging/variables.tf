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
  description = "ECR image tag the marketing SSR Lambda is created from (creation-time only; CI owns it afterwards)."
  type        = string
  default     = "latest"
}
