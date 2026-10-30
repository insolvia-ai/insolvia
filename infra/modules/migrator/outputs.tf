output "function_name" {
  description = "The migrator Lambda — what CI invokes before every alias shift."
  value       = aws_lambda_function.migrator.function_name
}

output "role_name" {
  description = "The migrator's execution role."
  value       = aws_iam_role.migrator.name
}

output "ecr_repository_url" {
  description = "Passed through: the repository the deploy workflow pushes migrator images to."
  value       = var.ecr_repository_url
}

output "ssm_parameter_prefix" {
  description = "The migrator's config namespace (/insolvia/<env>/migrator)."
  value       = local.ssm_prefix
}
