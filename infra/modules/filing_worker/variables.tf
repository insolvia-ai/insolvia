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
    The shared insolvia-shared-filing repository (infra/envs/shared, looked up
    by the env root). null means no Lambda at all — infra/envs/dev, where the
    local poller consumes the real queue as this role.
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
  description = "The case table — module.case_store.table_arn. Read (GetItem, Query) and written (PutItem, UpdateItem) for the approval, the filing record and the receipt's document item."
  type        = string
}

variable "case_table_name" {
  description = "The case table's name, for CASE_TABLE_NAME. The vault's table and key alias are derived from it (insolvia_core.adapters.aws.filing_credentials)."
  type        = string
}

variable "case_access_log_table_name" {
  description = "The case access log's name, for CASE_ACCESS_LOG_TABLE_NAME. The append grant is modules/filing_credentials'."
  type        = string
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
  description = "Lambda memory. The digest is recomputed through the forms engine (pypdf) twice a run."
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
