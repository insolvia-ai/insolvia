variable "project" {
  description = "Name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name (dev-<id>, staging, prod)."
  type        = string
}

variable "kms_key_arn" {
  description = "The CASE key, from modules/case_store. Deliberately not a key of this module's own — firm membership decides who reads case data, and a second key would be a second deny to get right."
  type        = string
}

variable "point_in_time_recovery" {
  description = "Continuous backups. On in prod; off in dev and staging, where the contents are disposable."
  type        = bool
  default     = false
}

variable "deletion_protection" {
  description = "Whether the table refuses to be destroyed. False everywhere since ADR 0028: the table is being deleted (plan 18.23)."
  type        = bool
  default     = false
}

variable "tags" {
  description = "Tags applied to the table."
  type        = map(string)
  default     = {}
}
