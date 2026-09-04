variable "project" {
  description = "Resource name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name — the -<env> segment on every resource here."
  type        = string
}

# Same split, same reason as api_service's pair: infra dirs are staging/prod,
# the service validates INSOLVIA_ENV against local|staging|production.
variable "insolvia_env" {
  description = "Value of the INSOLVIA_ENV variable the worker reads (staging or production). Ignored when the worker half is absent."
  type        = string
  default     = "staging"

  validation {
    condition     = contains(["staging", "production"], var.insolvia_env)
    error_message = "insolvia_env must be \"staging\" or \"production\" — the service rejects anything else."
  }
}

variable "ecr_repository_url" {
  description = <<-EOT
    URL of the shared insolvia-shared-jobs repository (created in
    infra/envs/shared, looked up by the env root). null means "no worker
    half at all" — infra/envs/dev, where there is no Lambda and jobs run
    through the local worker poller; the queue and DLQ are still created,
    because they are what the local seam runs against.
  EOT
  type        = string
  default     = null
}

variable "image_tag" {
  description = "Moving per-environment marker tag CI repoints at each deploy ('staging' / 'prod'). Only the first-apply seed for the worker Lambda."
  type        = string
  default     = "latest"
}

variable "api_role_name" {
  description = <<-EOT
    Execution role of the API Lambda — module.api_service.lambda_role_name.
    A NAME, attached from this side (the case_store/mailer seam pattern), so
    api_service never has to know this module exists. Gets sqs:SendMessage on
    the job queue and nothing else. null in infra/envs/dev, where the local
    API enqueues under the developer's own credentials.
  EOT
  type        = string
  default     = null
}

variable "worker_timeout_seconds" {
  description = <<-EOT
    The worker Lambda's timeout — the hard ceiling on one job attempt, and
    the number the queue's visibility timeout is derived from (6x, per AWS's
    event-source-mapping guidance). 900 is Lambda's own maximum; a job that
    cannot fit it is ADR 0018's reopen trigger, not a tuning problem.
  EOT
  type        = number
  default     = 900

  validation {
    condition     = var.worker_timeout_seconds >= 60 && var.worker_timeout_seconds <= 900
    error_message = "Between 60 and 900 seconds (Lambda's maximum)."
  }
}

variable "worker_memory_mb" {
  description = "Worker Lambda memory. 512 matches the API today; 9.6's PDF assembly is the expected reason to raise it."
  type        = number
  default     = 512
}

variable "alarms_topic_arn" {
  description = <<-EOT
    SNS topic the pipeline alarms publish to — module.api_service's
    alarms_topic_arn, reused rather than minting a second topic a human
    would have to subscribe to separately. null (dev) skips the alarms.
  EOT
  type        = string
  default     = null
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}
