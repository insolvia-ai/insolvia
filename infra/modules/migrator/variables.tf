variable "project" {
  description = "Resource name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Infra environment name (staging, prod) — the second segment of every name here."
  type        = string
}

# The services' split: infra dirs are staging/prod, the backend validates
# INSOLVIA_ENV against local|staging|production.
variable "insolvia_env" {
  description = "INSOLVIA_ENV for the migrator (staging or production)."
  type        = string

  validation {
    condition     = contains(["staging", "production"], var.insolvia_env)
    error_message = "insolvia_env must be \"staging\" or \"production\"."
  }
}

variable "ecr_repository_url" {
  description = "The shared insolvia-shared-migrator-dotnet repository (insolvia-services' infra/envs/shared, looked up by the env root)."
  type        = string
}

variable "image_tag" {
  description = "Moving per-environment marker tag ('staging' / 'prod'). Only the first-apply seed."
  type        = string
}

variable "vpc_subnet_ids" {
  description = "Private subnets — module.network.private_subnet_ids."
  type        = list(string)
}

variable "vpc_security_group_ids" {
  description = "[module.network.lambda_security_group_id] — the group modules/aurora admits 5432 from."
  type        = list(string)
}

variable "master_secret_arn" {
  description = "The RDS-managed master secret (module.aurora.master_secret_arn). The only secret this role may read."
  type        = string
}

variable "db_cluster_resource_id" {
  description = "The cluster's immutable resource id (module.aurora.cluster_resource_id) — what the rds-db:connect ARNs for the migrator and master logins name."
  type        = string
}

variable "master_username" {
  description = "The cluster's master user (module.aurora's master_username, default insolvia_admin) — the IAM-token fallback login after an rds_iam lockout (main.tf's header)."
  type        = string
  default     = "insolvia_admin"
}

variable "seed" {
  description = "Whether this environment's migrator also seeds (staging: true; production: false, refused)."
  type        = bool
  default     = false
}

variable "user_pool_arn" {
  description = "The firm pool's ARN (module.auth.user_pool_arn) — the seed's test accounts. Required when seed is true."
  type        = string
  default     = null
}

variable "fixture_bucket_arn" {
  description = "The shared seed-fixture bucket's ARN (insolvia-services' envs/shared). Required when seed is true."
  type        = string
  default     = null
}

variable "case_document_bucket_arn" {
  description = "This environment's case-documents bucket ARN. Required when seed is true."
  type        = string
  default     = null
}

variable "case_kms_key_arn" {
  description = "The case key (module.case_store.kms_key_arn) — the documents' bucket key, and the tax-id envelopes the seed seals. Required when seed is true."
  type        = string
  default     = null
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}
