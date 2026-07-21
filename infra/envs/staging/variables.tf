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
