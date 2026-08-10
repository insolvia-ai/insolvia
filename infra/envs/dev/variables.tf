variable "aws_region" {
  description = "AWS region for this machine's development resources."
  type        = string
  default     = "us-east-1"
}

variable "aws_principal_arn" {
  description = "Caller ARN recorded as the development resource owner (tag only)."
  type        = string
}

# The three machine variables are supplied by scripts/dev-aws-common.sh from
# the persistent per-machine UUID at ~/.config/insolvia/machine-id. They are
# what guarantees isolation: every resource name embeds machine_short_id, so
# two developers can never collide.

variable "machine_id" {
  description = "Persistent UUID generated for this OS user on this machine."
  type        = string
  validation {
    condition     = can(regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", var.machine_id))
    error_message = "machine_id must be a lowercase UUID."
  }
}

variable "machine_short_id" {
  description = "First 12 hexadecimal characters of machine_id, used in resource names."
  type        = string
  validation {
    condition     = can(regex("^[0-9a-f]{12}$", var.machine_short_id))
    error_message = "machine_short_id must contain exactly 12 lowercase hexadecimal characters."
  }
}

variable "machine_name" {
  description = "Human-readable machine hostname, stored only as a resource tag."
  type        = string
}

variable "enable_audit_trail" {
  description = <<-EOT
    Stand up this machine's own case-data audit trail (modules/audit_trail).

    OFF by default, and that is a cost decision rather than a scope one: a
    per-developer CloudTrail trail bills per data event to record a developer
    reading their own synthetic cases, which is evidence nobody will ever
    read. The module is still exercised locally on demand —

      terraform -chdir=infra/envs/dev apply -var=enable_audit_trail=true ...

    — so a change to modules/audit_trail can be validated on a laptop before
    it reaches staging, which is the part that matters. Turn it back off and
    re-apply to remove it.
  EOT
  type        = bool
  default     = false
}

# The DEV Google Workspace OAuth client (#209) — one client, shared by every
# developer machine, redirecting to http://localhost:3100/auth/callback. A
# PUBLIC value (it appears in every sign-in redirect), committed on purpose;
# per-machine clients would mean a console procedure per laptop for no
# isolation gain (the audience check is per-environment, not per-machine).
variable "google_admin_client_id" {
  description = "Google Workspace OAuth client id for local admin-portal sign-in."
  type        = string
  default     = "925851246989-tttlgepq8mjfhmtlkmgikrtv4kskv0pk.apps.googleusercontent.com"
}
