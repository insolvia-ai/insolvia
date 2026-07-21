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

# ── Inbound forwarding (#21, #24, #25) ─────────────────────────
output "inbound_forwarded_recipients" {
  description = "Addresses SES accepts and forwards. no-reply@ is excluded by design (send-only)."
  value       = module.inbound_forwarding.forwarded_recipients
}

output "inbound_forwarder_alerts_topic_arn" {
  description = "SNS topic for the forwarder DLQ/error alarms. Subscribe to this by hand and confirm the subscription email."
  value       = module.inbound_forwarding.alerts_topic_arn
}

output "inbound_forwarder_dlq_url" {
  description = "DLQ holding inbound mail that failed every delivery retry."
  value       = module.inbound_forwarding.dlq_url
}

output "inbound_forward_to_ssm_parameter" {
  description = "SSM SecureString path holding the private forward destination."
  value       = module.inbound_forwarding.forward_to_ssm_parameter
}
# ── end inbound forwarding ─────────────────────────────────────
