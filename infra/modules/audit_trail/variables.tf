variable "project" {
  description = "Resource name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name — the -<env> suffix on the trail, bucket and key."
  type        = string
}

variable "data_resource_arns" {
  description = <<-EOT
    Resource ARN prefixes whose DATA events this trail records — today the
    case table, and the case document bucket when 8.6 lands. Matched with
    starts_with, so a table ARN also covers its indexes.

    Deliberately a list of named resources rather than "every table in the
    account": data events bill per event, and this trail exists to record
    case-data access rather than to be a general audit surface.
  EOT
  type        = list(string)

  validation {
    condition     = length(var.data_resource_arns) > 0
    error_message = "A trail with no data resources records nothing — omit the module instead."
  }
}

variable "retention_days" {
  description = <<-EOT
    Days log files are kept before expiry, current and non-current versions
    alike. Audit evidence is only useful as far back as it reaches, so this
    is a compliance decision rather than a cost one; the default is a year.
  EOT
  type        = number
  default     = 365

  validation {
    condition     = var.retention_days >= 90
    error_message = "Keep at least 90 days — shorter than a quarter is not an audit trail."
  }
}

variable "key_deletion_window_in_days" {
  description = "Days a scheduled deletion of the audit key waits. Deleting it makes every retained log file unreadable."
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
