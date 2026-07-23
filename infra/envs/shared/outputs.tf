output "route53_zone_id" {
  description = "Hosted zone ID for insolvia.ai."
  value       = aws_route53_zone.main.zone_id
}

output "route53_name_servers" {
  description = "Point the registrar at these name servers to delegate insolvia.ai."
  value       = aws_route53_zone.main.name_servers
}

output "acm_certificate_arn" {
  description = "ARN of the validated *.insolvia.ai certificate."
  value       = aws_acm_certificate_validation.wildcard.certificate_arn
}

output "github_actions_role_arn" {
  description = "Set this as the AWS_ROLE_ARN GitHub secret."
  value       = aws_iam_role.github_actions.arn
}

# ── Email (#19, #20) ───────────────────────────────────────────
output "ses_identity_arn" {
  description = "ARN of the insolvia.ai SES domain identity."
  value       = module.email.identity_arn
}

output "ses_from_address" {
  description = "Default transactional From address."
  value       = module.email.from_address
}

output "ses_mail_from_domain" {
  description = "Custom SES MAIL FROM (Return-Path) domain."
  value       = module.email.mail_from_domain
}
# ── end email ──────────────────────────────────────────────────

output "apex_mx_records" {
  description = "Who receives mail for @insolvia.ai. Google Workspace — the SES inbound forwarder was removed."
  value       = module.email.apex_mx_records
}
