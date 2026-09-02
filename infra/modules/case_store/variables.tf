variable "project" {
  description = "Resource name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name — the -<env> suffix on every resource here."
  type        = string
}

variable "api_role_name" {
  description = <<-EOT
    Execution role of the API Lambda — module.api_service.lambda_role_name.
    Passed as a NAME, not an ARN: this module looks it up with a data source
    and attaches the case-table grant from its own side, so api_service never
    has to know this module exists. A resource reference here would be a
    dependency cycle.

    null in infra/envs/dev, where there is no Lambda and the developer's own
    IAM user is the principal — the grant is then skipped entirely rather
    than pointed at something that does not exist.
  EOT
  type        = string
  default     = null
}

variable "worker_role_name" {
  description = <<-EOT
    Execution role of the pipeline worker Lambda —
    module.job_pipeline.worker_role_name (ADR 0018). Same shape and same
    reasoning as api_role_name above: a name, attached from this side, so
    neither this module nor job_pipeline has to reference the other's
    resources (job_pipeline's Lambda does not read this module's outputs
    either — the table name reaches the worker through SSM via the deploy
    workflow, exactly as it reaches the API).

    null in infra/envs/dev, where there is no worker Lambda — jobs run
    through entrypoints/worker_poller.py under the developer's own IAM user.
  EOT
  type        = string
  default     = null
}

variable "point_in_time_recovery" {
  description = <<-EOT
    DynamoDB PITR. On everywhere that holds data worth recovering; off in dev,
    which holds synthetic cases a developer wipes with dev-aws-reset.sh and
    where paying for recovery would be noise.
  EOT
  type        = bool
  default     = true
}

variable "deletion_protection" {
  description = "DynamoDB deletion protection. True in prod; staging stays disposable."
  type        = bool
  default     = true
}

variable "key_deletion_window_in_days" {
  description = <<-EOT
    Days a scheduled key deletion waits before it is irreversible. A deleted
    key makes every row in the table permanently unreadable — there is no
    restore, and PITR does not help, so prod takes the maximum.
  EOT
  type        = number
  default     = 30

  validation {
    condition     = var.key_deletion_window_in_days >= 7 && var.key_deletion_window_in_days <= 30
    error_message = "AWS accepts 7–30 days."
  }
}

variable "tags" {
  description = "Tags applied to every resource in this module."
  type        = map(string)
  default     = {}
}
