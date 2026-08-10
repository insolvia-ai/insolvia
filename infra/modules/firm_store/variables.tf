variable "project" {
  description = "Name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name (dev-<id>, staging, prod)."
  type        = string
}

variable "aws_region" {
  description = "Region, used only to fence the API's KMS grant to DynamoDB as the calling service."
  type        = string
}

variable "kms_key_arn" {
  description = "The CASE key, from modules/case_store. Deliberately not a key of this module's own — firm membership decides who reads case data, and a second key would be a second deny to get right."
  type        = string
}

variable "api_role_name" {
  description = "Execution role that resolves callers and administers firm users. Null in infra/envs/dev, where there is no Lambda and the developer's own IAM user is the principal."
  type        = string
  default     = null
}

variable "point_in_time_recovery" {
  description = "Continuous backups. On in prod; off in dev and staging, where the contents are disposable."
  type        = bool
  default     = false
}

variable "deletion_protection" {
  description = "Whether the table refuses to be destroyed. True in prod: losing a firm's user list locks every one of its staff out of every case."
  type        = bool
  default     = false
}

variable "tags" {
  description = "Tags applied to the table."
  type        = map(string)
  default     = {}
}

# The admin service's Lambda execution role (#213), or null where there is no
# admin Lambda (dev — the local admin service runs under the developer's own
# IAM user). Looked up by name for the same no-circular-dependency reason as
# api_role_name.
variable "admin_role_name" {
  description = "Admin Lambda execution role name to grant firm-table CRUD + Scan, or null."
  type        = string
  default     = null
}
