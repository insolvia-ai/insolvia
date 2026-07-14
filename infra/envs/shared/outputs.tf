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
