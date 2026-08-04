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
  EOT
  type        = string
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
