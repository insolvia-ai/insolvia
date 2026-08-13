variable "project" {
  description = "Project slug, used in resource names."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Infra environment name (dev-<id>, staging, prod), used in resource names."
  type        = string
}

# Same split, same reason, as modules/api_service's: infra dirs say "prod",
# the service validates local|staging|production.
variable "insolvia_env" {
  description = "Value of the INSOLVIA_ENV variable the service reads (staging or production)."
  type        = string

  validation {
    condition     = contains(["staging", "production"], var.insolvia_env)
    error_message = "insolvia_env must be \"staging\" or \"production\" — the service rejects anything else."
  }
}

variable "domain_name" {
  description = "Fully-qualified hostname the admin API serves (e.g. staging-admin-api.insolvia.ai)."
  type        = string
}

variable "hosted_zone_id" {
  description = "Route53 hosted zone ID for insolvia.ai."
  type        = string
}

variable "acm_certificate_arn" {
  description = "ARN of the *.insolvia.ai ACM certificate, in the API's own region (REGIONAL endpoint)."
  type        = string
}

variable "ecr_repository_url" {
  description = "URL of the shared insolvia-admin repository (created in infra/envs/shared, looked up by the env root)."
  type        = string
}

variable "image_tag" {
  description = "Moving per-environment marker tag CI repoints at each deploy ('staging' / 'prod'). Only the first-apply seed for the Lambda."
  type        = string
  default     = "latest"
}

variable "firm_table_name" {
  description = "The environment's firm table (modules/firm_store) — the same table the tenant API reads; ADR 0011 records the shared access. The CRUD+Scan grant is attached by that module's admin_role_name seam, not here."
  type        = string
}

variable "firm_user_pool_id" {
  description = "The FIRM Cognito pool (insolvia-<env>-users) provisioning mints first-administrator accounts in. The AdminCreateUser grant is attached by modules/auth's admin_invite_role_name seam, not here."
  type        = string
}

# A PUBLIC value — it appears in every sign-in redirect the portal makes —
# which is why it is committed in the env roots rather than treated as a
# secret (#209).
variable "google_client_id" {
  description = "The environment's Google Workspace OAuth client id the service verifies staff ID tokens against."
  type        = string
}

# True everywhere this module runs in a deployed environment: the audit table
# IS the durable provisioning record (#178). Dev instances may disarm it so
# dev-aws-destroy.sh can remove the table.
variable "audit_deletion_protection" {
  description = "Whether the admin audit table refuses deletion."
  type        = bool
  default     = true
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}
