variable "project" {
  description = "Name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name (staging, prod). Not used by infra/envs/dev: local development runs Postgres in Docker (ADR 0028)."
  type        = string
}

variable "vpc_id" {
  description = "The VPC from modules/network."
  type        = string
}

variable "subnet_ids" {
  description = "The private subnets from modules/network, in at least two AZs (an RDS subnet group requires it)."
  type        = list(string)

  validation {
    condition     = length(var.subnet_ids) >= 2
    error_message = "An Aurora subnet group needs subnets in at least two availability zones."
  }
}

variable "lambda_security_group_id" {
  description = "The backend Lambdas' security group from modules/network. The ONLY source the database admits on 5432."
  type        = string
}

variable "engine_version" {
  description = "Aurora PostgreSQL version. 17 is ADR 0028's choice; scale-to-zero needs 16.3+/15.7+. Minor upgrades are taken automatically by the instance, so the module ignores drift on this argument after create — bump the default to move NEW environments, upgrade existing ones through RDS."
  type        = string
  default     = "17.5"
}

variable "database_name" {
  description = "The application database created with the cluster."
  type        = string
  default     = "insolvia"
}

variable "master_username" {
  description = "Master user. Its password is generated and held by RDS in Secrets Manager (manage_master_user_password); only the migrator and a human break-glass use it (ADR 0028 § Data)."
  type        = string
  default     = "insolvia_admin"
}

variable "min_capacity" {
  description = "Serverless v2 minimum ACUs. 0 enables auto-pause (ADR 0028). Prod stays at 0 before launch; the ADR revisits it at launch, when a 15-second resume on the first request after idle stops being acceptable."
  type        = number
  default     = 0
}

variable "max_capacity" {
  description = "Serverless v2 maximum ACUs — the cost ceiling. Must be at least 1 when min_capacity is 0."
  type        = number
  default     = 2

  validation {
    condition     = var.max_capacity >= 1
    error_message = "max_capacity must be at least 1 ACU."
  }
}

variable "seconds_until_auto_pause" {
  description = "Idle seconds before the cluster pauses (300-86400). Only applies when min_capacity is 0. Shorter saves ACU-hours; longer means fewer 15-second resumes during a working day."
  type        = number
  default     = 900

  validation {
    condition     = var.seconds_until_auto_pause >= 300 && var.seconds_until_auto_pause <= 86400
    error_message = "seconds_until_auto_pause must be between 300 and 86400."
  }
}

variable "backup_retention_days" {
  description = "Automated backup retention (1-35). Aurora's continuous backup IS point-in-time recovery within this window; there is no separate switch."
  type        = number
  default     = 7

  validation {
    condition     = var.backup_retention_days >= 1 && var.backup_retention_days <= 35
    error_message = "backup_retention_days must be between 1 and 35."
  }
}

variable "log_retention_days" {
  description = "Retention of the exported PostgreSQL log group. Must be one of CloudWatch's allowed values."
  type        = number
  default     = 30
}

variable "deletion_protection" {
  description = "Whether the cluster refuses to be deleted. True in prod; false in staging, which is disposable on purpose. With it on, destroy also takes a final snapshot; with it off it does not."
  type        = bool
  default     = false
}

variable "key_deletion_window_in_days" {
  description = "KMS key deletion window (7-30). Scheduling this key's deletion makes the database permanently unreadable — snapshots and PITR restores are encrypted under it — so prod uses 30. Staging uses 7 so a teardown does not leave a pending key behind."
  type        = number
  default     = 30
}

variable "apply_immediately" {
  description = "Apply cluster modifications now instead of in the next maintenance window. Staging convenience; prod leaves it false."
  type        = bool
  default     = false
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
  default     = {}
}
