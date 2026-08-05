variable "project" {
  description = "Name prefix. The bucket must stay in the insolvia-case-* family — ci-trust's CaseDocumentBuckets grant is scoped to exactly that prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name (dev-<id>, staging, prod)."
  type        = string
}

variable "aws_region" {
  description = "Region, used only to fence the API's KMS grant to S3 as the calling service."
  type        = string
}

variable "kms_key_arn" {
  description = "The CASE key, from modules/case_store. Never a key of this module's own — one key protects one case, and the deploy role's data-plane deny is written against that key's alias."
  type        = string
}

variable "api_role_name" {
  description = "Execution role that brokers uploads and downloads. Null in infra/envs/dev, where there is no Lambda and the developer's own IAM user is the principal."
  type        = string
  default     = null
}

variable "force_destroy" {
  description = "Whether `terraform destroy` may empty the bucket first. True in staging and dev (synthetic documents, disposable), false in prod."
  type        = bool
  default     = false
}

variable "tags" {
  description = "Tags applied to the bucket."
  type        = map(string)
  default     = {}
}
