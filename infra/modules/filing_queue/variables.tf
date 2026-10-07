variable "project" {
  description = "Resource name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name — the second segment of every name here (insolvia-<env>-filing)."
  type        = string
}

variable "api_role_name" {
  description = <<-EOT
    The API Lambda's execution role — module.api_service.lambda_role_name.
    Granted sqs:SendMessage on the filing queue and nothing else: the
    attorney's approval (services/api) is the only producer of a filing job.
    Null in infra/envs/dev, where the local API sends under the developer's
    own credentials.
  EOT
  type        = string
  default     = null
}

variable "worker_role_name" {
  description = <<-EOT
    The filing worker's execution role — module.filing_credentials.
    worker_role_name (insolvia-<env>-filing-role). Granted consume on this
    queue (receive, delete, change visibility, read attributes) and never
    send. Required: the role exists in every environment, dev included.
  EOT
  type        = string
}

variable "visibility_timeout_seconds" {
  description = <<-EOT
    How long a received filing job stays hidden before redelivery. PR 7 sets
    it against the filing worker's own timeout; until then 900 (Lambda's
    maximum) — inside the approval's one-hour lifetime, so three attempts can
    all still find their approval consumable.
  EOT
  type        = number
  default     = 900

  validation {
    condition     = var.visibility_timeout_seconds >= 60 && var.visibility_timeout_seconds <= 1200
    error_message = "Between 60 and 1200 seconds: past 20 minutes a third attempt would start after the approval (one hour) has expired."
  }
}

variable "message_retention_seconds" {
  description = <<-EOT
    How long an unconsumed filing job is kept. Twice the approval's lifetime
    (services/api/core/filing_approval.APPROVAL_TTL_SECONDS = 3600): a job
    older than its approval can never be consumed, so a longer retention
    only stores dead letters.
  EOT
  type        = number
  default     = 7200

  validation {
    condition     = var.message_retention_seconds >= 3600 && var.message_retention_seconds <= 86400
    error_message = "At least the approval's lifetime (3600) and at most a day."
  }
}

variable "tags" {
  description = "Tags applied to every resource in this module."
  type        = map(string)
  default     = {}
}
