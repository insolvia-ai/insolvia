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

# ── Inbound forwarding (#23) ───────────────────────────────────
variable "inbound_forward_to" {
  description = <<-EOT
    Private destination mailbox for mail forwarded from hello@ / support@ /
    security@. A human secret: it is NOT committed and NOT in
    terraform.tfvars.example. Supply it at apply time via the environment:

      TF_VAR_inbound_forward_to='someone@example.com' terraform apply

    Terraform writes it once into an SSM SecureString and then ignores changes
    to the value, so subsequent applies do not need it set.
  EOT
  type        = string
  default     = ""
  sensitive   = true
}
# ── end inbound forwarding ─────────────────────────────────────
