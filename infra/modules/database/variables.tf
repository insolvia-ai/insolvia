variable "project" {
  description = "Project name — the first segment of every resource name."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name (staging, prod) — the second segment of every resource name."
  type        = string
}

variable "insolvia_env" {
  description = "INSOLVIA_ENV the migrator Lambda runs with."
  type        = string
}

variable "vpc_id" {
  description = "The VPC the cluster lives in (modules/network)."
  type        = string
}

variable "subnet_ids" {
  description = "Two private subnets in two AZs (modules/network)."
  type        = list(string)
}

variable "client_security_group_id" {
  description = "The backend Lambdas' security group — the only source the database admits Postgres from."
  type        = string
}

variable "engine_version" {
  description = "Aurora PostgreSQL version. ADR 0030 fixes the major at 17."
  type        = string
  default     = "17.4"

  validation {
    condition     = startswith(var.engine_version, "17.")
    error_message = "ADR 0030 fixes PostgreSQL 17."
  }
}

variable "min_capacity" {
  description = "Serverless v2 minimum ACUs. 0 pauses an idle cluster (ADR 0030); prod's is revisited at launch."
  type        = number
  default     = 0
}

variable "max_capacity" {
  description = "Serverless v2 maximum ACUs."
  type        = number
  default     = 2
}

variable "iam_users" {
  description = "Postgres role -> the IAM role name allowed to connect as it (rds-db:connect). One per service (ADR 0030). The migrator is not listed: it is created here."
  type        = map(string)
}

variable "migrator_image_uri" {
  description = "Seed image for the migrator Lambda (the API image carries insolvia_core and its migrations). The deploy workflow owns the running image."
  type        = string
}

variable "deletion_protection" {
  description = "Cluster deletion protection, and a final snapshot on destroy. Off in staging, on in prod."
  type        = bool
}

variable "backup_retention_days" {
  description = "Automated backup retention."
  type        = number
  default     = 7
}

variable "key_deletion_window_in_days" {
  description = "KMS key deletion window. 7 in staging, 30 in prod."
  type        = number
  default     = 30
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
  default     = {}
}
