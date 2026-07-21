output "identity_arn" {
  description = "SES domain identity ARN authorized to send Insolvia mail."
  value       = aws_ses_domain_identity.domain.arn
}

output "domain_identity" {
  description = "Verified SES identity domain."
  value       = aws_ses_domain_identity.domain.domain
}

output "from_address" {
  description = "Default transactional From address."
  value       = local.from_address
}

output "mail_from_domain" {
  description = "Custom SES MAIL FROM (Return-Path) domain."
  value       = local.mail_from_domain
}

output "dkim_tokens" {
  description = "DKIM tokens whose CNAMEs are published in the hosted zone."
  value       = aws_ses_domain_dkim.domain.dkim_tokens
}
