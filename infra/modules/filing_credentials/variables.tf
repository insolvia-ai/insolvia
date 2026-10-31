variable "project" {
  description = "Resource name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name — the second segment of every name here (insolvia-<env>-filing-credentials)."
  type        = string
}

variable "api_role_name" {
  description = <<-EOT
    The API Lambda's execution role. It is granted SEAL-ONLY use of the vault
    key (GenerateDataKey under the vault's encryption-context purpose) and
    nothing else — enrol / status / revoke are Aurora rows. Null where there is no
    API Lambda (infra/envs/dev — the developer's own principal plays the API
    there, and the key policy's deny refuses it Decrypt all the same).
  EOT
  type        = string
  default     = null
}

variable "worker_assumable_by" {
  description = <<-EOT
    Extra principals (IAM ARNs) the filing worker's role trusts besides
    Lambda. DEV ONLY: infra/envs/dev passes the developer's own principal, so
    "the worker's role opens a credential" can be proved on a laptop by
    assuming the real role rather than by a stand-in. A precondition on the
    role refuses a non-empty list in any environment not named dev-*, so
    staging and prod cannot be widened by a one-line edit to their roots.
  EOT
  type        = list(string)
  default     = []
}

variable "key_deletion_window_in_days" {
  description = "Days a scheduled deletion of the vault key waits. Deleting it makes every sealed credential unopenable — which, for this key, is a revocation of all of them at once rather than a data loss."
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
