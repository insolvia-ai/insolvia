variable "aws_region" {
  description = "Default AWS region."
  type        = string
  default     = "us-east-1"
}

variable "domain_name" {
  description = "Apex domain for Insolvia."
  type        = string
  default     = "insolvia.ai"
}

variable "github_repo" {
  description = <<-EOT
    owner/name of the repo allowed to assume the deploy role. Must match the
    canonical case GitHub emits in the OIDC `sub` claim (the IAM condition is
    case-sensitive) — i.e. the org's exact login casing.
  EOT
  type        = string
  default     = "insolvia-ai/insolvia"
}
