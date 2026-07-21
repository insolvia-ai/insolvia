variable "project" {
  description = "Project name used in resource names (e.g. insolvia)"
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name (e.g. shared)"
  type        = string
}

variable "aws_region" {
  description = "AWS region where SES receives inbound mail"
  type        = string
  default     = "us-east-1"
}

variable "domain_name" {
  description = "Mail domain that receives inbound mail (e.g. insolvia.ai)"
  type        = string
}

variable "mail_addresses" {
  description = <<-EOT
    The confirmed address map. Keys are local parts on `domain_name`; `purpose`
    documents what each one is for and `forwards` decides whether SES accepts
    mail for it and forwards it on.

    `no-reply@` is set `forwards = false` deliberately: it is a SEND-ONLY
    transactional sender. It must not appear in the receipt rule's recipient
    list, and the Lambda drops anything addressed to it. Flipping it to `true`
    would make the address a live inbox — do not do that without deciding who
    reads it.
  EOT

  type = map(object({
    purpose  = string
    forwards = bool
  }))

  default = {
    hello = {
      purpose  = "General enquiries"
      forwards = true
    }
    support = {
      purpose  = "Product support"
      forwards = true
    }
    security = {
      purpose  = "Vulnerability disclosure"
      forwards = true
    }
    "no-reply" = {
      purpose  = "Transactional sender — SEND-ONLY, never received"
      forwards = false
    }
  }

  validation {
    condition     = length([for k, v in var.mail_addresses : k if v.forwards]) > 0
    error_message = "At least one address must have forwards = true, or the SES receipt rule has no recipients."
  }
}

variable "from_address" {
  description = "Verified SES sender used as the From: of the forwarded message"
  type        = string
}

variable "ses_identity_arn" {
  description = <<-EOT
    ARN of the verified SES domain identity used to send the forward.

    No longer referenced in the IAM policy — SES authorizes SendRawEmail
    against the destination identity too, so that statement covers
    identity/* in this account (see the SendForward comment in main.tf).

    Kept deliberately: passing `module.email.identity_arn` is what creates the
    dependency edge that orders identity creation before the receipt rule. Drop
    this input and Terraform is free to build the rule against a domain SES has
    not verified yet.
  EOT
  type        = string
}

variable "inbound_forward_to" {
  description = <<-EOT
    Private destination inbox for forwarded mail. This is a human-provided
    secret: leave it empty in committed config and inject it at apply time via
    TF_VAR_inbound_forward_to. Terraform writes it once into an SSM SecureString
    and thereafter ignores changes to the value, so the real address never has
    to live in state-adjacent config or in a tfvars file.
  EOT
  type        = string
  default     = ""
  sensitive   = true
}

variable "inbound_object_expiration_days" {
  description = "Days after which raw inbound messages are expired from S3"
  type        = number
  default     = 30
}

variable "max_message_bytes" {
  description = "Maximum assembled forward size before attachments are dropped"
  type        = number
  default     = 9000000
}

variable "max_attachment_bytes" {
  description = "Maximum per-attachment size that is forwarded"
  type        = number
  default     = 6000000
}

variable "lambda_source_dir" {
  description = "Path to the Python Lambda source root (the directory containing the inbound_forwarder package)"
  type        = string
}

variable "tags" {
  description = "Common resource tags"
  type        = map(string)
  default     = {}
}
