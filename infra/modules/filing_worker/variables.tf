variable "project" {
  description = "Resource name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name — the second segment of every name here (insolvia-<env>-filing-*)."
  type        = string
}

variable "insolvia_env" {
  description = "INSOLVIA_ENV for the Lambda (staging or production). Ignored where there is no Lambda (dev)."
  type        = string
  default     = "staging"

  validation {
    condition     = contains(["staging", "production"], var.insolvia_env)
    error_message = "insolvia_env must be \"staging\" or \"production\" — the worker refuses anything else, and \"local\" would let it compose a fake court."
  }
}

variable "ecr_repository_url" {
  description = <<-EOT
    The shared insolvia-shared-filing repository (insolvia-services'
    infra/envs/shared, looked up by the env root). null means no Lambda at
    all — infra/envs/dev, where the local poller consumes the real queue as
    this role.
  EOT
  type        = string
  default     = null
}

variable "image_tag" {
  description = "Moving per-environment marker tag CI repoints at each deploy ('staging' / 'prod'). The first-apply seed only."
  type        = string
  default     = "latest"
}

variable "worker_role_name" {
  description = "The filing worker's role — module.filing_credentials.worker_role_name (insolvia-<env>-filing-role)."
  type        = string
}

variable "worker_role_arn" {
  description = "The same role's ARN — module.filing_credentials.worker_role_arn — which the Lambda runs as."
  type        = string
}

variable "queue_arn" {
  description = "The filing queue — module.filing_queue.queue_arn. Its consume grant is that module's."
  type        = string
}

variable "dlq_name" {
  description = "The filing queue's dead-letter queue name, for the depth alarm."
  type        = string
}

variable "case_table_arn" {
  description = "The case table — module.case_store.table_arn. Read and written by the Python worker while dynamodb_grants is true (dev's poller); unused once it is false. TRANSITIONAL — 18.23."
  type        = string
  default     = null
}

variable "db_endpoint" {
  description = "Aurora's writer endpoint (module.aurora.endpoint), for INSOLVIA_DB_ENDPOINT. null in dev (no Lambda)."
  type        = string
  default     = null
}

variable "db_port" {
  description = "Aurora's port (module.aurora.port), for INSOLVIA_DB_PORT."
  type        = number
  default     = 5432
}

variable "db_database_name" {
  description = "The application database (module.aurora.database_name), for INSOLVIA_DB_DATABASE_NAME. null in dev."
  type        = string
  default     = null
}

variable "case_kms_key_alias" {
  description = "The case key's alias (alias/insolvia-<env>-cases), for INSOLVIA_KMS_CASE_KEY_ALIAS — the Case Upload file's taxid.read. null in dev."
  type        = string
  default     = null
}

variable "filing_kms_key_alias" {
  description = "The vault key's alias (alias/insolvia-<env>-filing-credentials), for INSOLVIA_KMS_FILING_KEY_ALIAS — the credential opener. null in dev."
  type        = string
  default     = null
}

variable "case_kms_key_arn" {
  description = "The case key — module.case_store.kms_key_arn — usable through DynamoDB and S3 only."
  type        = string
}

variable "case_document_bucket_arn" {
  description = "The case documents bucket's ARN — packets read, the filing's own prefix written."
  type        = string
}

variable "case_document_bucket_name" {
  description = "The case documents bucket's name, for CASE_DOCUMENT_BUCKET."
  type        = string
}

variable "timeout_seconds" {
  description = <<-EOT
    The Lambda's timeout — one filing attempt's ceiling, and the worker's
    claim lease (services/filing core/config.DEFAULT_LEASE_SECONDS = 600; the
    two must agree). Below the queue's visibility timeout (900) so a message is
    never redelivered while its attempt can still run.
  EOT
  type        = number
  default     = 600

  validation {
    condition     = var.timeout_seconds >= 60 && var.timeout_seconds < 900
    error_message = "Between 60 and 899 seconds: it must stay under the filing queue's 900-second visibility timeout."
  }
}

variable "memory_mb" {
  description = "Lambda memory. The digest is recomputed through the forms engine twice a run."
  type        = number
  default     = 1024
}

variable "alarms_topic_arn" {
  description = "SNS topic for the alarms — module.api_service.alarms_topic_arn. null (dev) skips them."
  type        = string
  default     = null
}

variable "tags" {
  description = "Tags applied to every resource in this module."
  type        = map(string)
  default     = {}
}

# ── The VPC and the database (ADR 0028, plan 18.22) ─────────────
# null wherever the Lambda half is absent (infra/envs/dev); required, in
# effect, wherever ecr_repository_url is set — the precondition on the
# function says so by name.

variable "vpc_subnet_ids" {
  description = "Private subnets the Lambda runs in — module.network.private_subnet_ids. null in dev (no Lambda)."
  type        = list(string)
  default     = null
}

variable "vpc_security_group_ids" {
  description = "Security groups for the Lambda — [module.network.lambda_security_group_id], the one modules/aurora admits 5432 from. null in dev."
  type        = list(string)
  default     = null
}

variable "db_cluster_resource_id" {
  description = "The Aurora cluster's resource id (module.aurora.cluster_resource_id) — what this host's rds-db:connect grant names. null in dev."
  type        = string
  default     = null
}

# TRANSITIONAL (ADR 0028): delete with the DynamoDB tables in plan 18.23.
variable "dynamodb_grants" {
  description = <<-EOT
    Whether the application roles keep their DynamoDB grants on this module's
    tables. true is the Python backend's shape, and stays the default for
    infra/envs/dev, where a developer's local Python stack still reads the
    per-machine tables until 18.23 deletes the Python. Staging and prod pass
    false since the switch (plan 18.22): their .NET hosts keep their data in
    Aurora, so a role keeps only the grants that are not DynamoDB's — the KMS
    envelopes, S3, the kill switch. The TABLES themselves are untouched here;
    18.23 deletes them with the maintainer.
  EOT
  type        = bool
  default     = true
}
