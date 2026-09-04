variable "project" {
  description = "Project slug, used in resource names."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Infra environment name (staging, prod), used in resource names."
  type        = string
}

# Deliberately separate from `environment`, exactly as modules/api_service:
# infra directories are named staging/prod, but the service validates
# INSOLVIA_ENV against local|staging|production — "prod" would crash it at
# startup. See services/mcp/src/insolvia_mcp/core/config.py.
variable "insolvia_env" {
  description = "Value of the INSOLVIA_ENV variable the service reads (staging or production)."
  type        = string

  validation {
    condition     = contains(["staging", "production"], var.insolvia_env)
    error_message = "insolvia_env must be \"staging\" or \"production\" — the service rejects anything else."
  }
}

variable "domain_name" {
  description = "Fully-qualified hostname the MCP server serves (e.g. staging-mcp.insolvia.ai). This host IS the canonical resource URI's authority (ADR 0016) — the protected-resource metadata, the OAuth `resource` parameter, and every directory listing carry it."
  type        = string
}

variable "hosted_zone_id" {
  description = "Route53 hosted zone ID for insolvia.ai."
  type        = string
}

variable "acm_certificate_arn" {
  description = "ARN of the *.insolvia.ai ACM certificate. Must live in the API's own region (REGIONAL endpoint — see modules/api_service's custom-domain note)."
  type        = string
}

variable "ecr_repository_url" {
  description = "URL of the shared insolvia-shared-mcp repository (created in infra/envs/shared, looked up by the env root). Shared across environments so prod deploys the exact digest staging validated."
  type        = string
}

variable "image_tag" {
  description = "Moving per-environment marker tag CI repoints at each deploy ('staging' / 'prod'). Only the first-apply seed for the Lambda; every later deploy sets image_uri by digest."
  type        = string
  default     = "latest"
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}
