variable "project" {
  description = "Project slug, used in resource names."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Infra environment name (staging, prod), used in resource names."
  type        = string
}

# Deliberately separate from `environment`: infra directories are named
# staging/prod (insolvia-<thing>-<env> convention), but the service validates
# INSOLVIA_ENV against local|staging|production — "prod" would crash it at
# startup. See services/api/src/insolvia_api/core/config.py.
variable "insolvia_env" {
  description = "Value of the INSOLVIA_ENV variable the service reads (staging or production)."
  type        = string

  validation {
    condition     = contains(["staging", "production"], var.insolvia_env)
    error_message = "insolvia_env must be \"staging\" or \"production\" — the service rejects anything else."
  }
}

variable "domain_name" {
  description = "Fully-qualified hostname the API serves (e.g. staging-api.insolvia.ai)."
  type        = string
}

variable "hosted_zone_id" {
  description = "Route53 hosted zone ID for insolvia.ai."
  type        = string
}

variable "acm_certificate_arn" {
  description = "ARN of the *.insolvia.ai ACM certificate. Must live in the API's own region (REGIONAL endpoint — see the custom-domain note in main.tf)."
  type        = string
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}
