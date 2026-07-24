output "ecr_repository_url" {
  description = "ECR repository the deploy workflow pushes API images to."
  value       = aws_ecr_repository.api.repository_url
}

output "lambda_function_name" {
  description = "API Lambda function name (deploy target for update-function-code/-configuration)."
  value       = aws_lambda_function.api.function_name
}

output "domain_name" {
  description = "Hostname the API serves."
  value       = aws_apigatewayv2_domain_name.api.domain_name
}

output "url" {
  description = "Public HTTPS base URL for the API."
  value       = "https://${var.domain_name}"
}

output "http_api_id" {
  description = "API Gateway HTTP API id (CloudWatch dimension, CLI operations)."
  value       = aws_apigatewayv2_api.api.id
}

output "waitlist_table_name" {
  description = "DynamoDB table behind POST /v1/waitlist."
  value       = aws_dynamodb_table.waitlist.name
}

output "ssm_parameter_prefix" {
  description = "SSM namespace holding this environment's API config (/insolvia/<env>/api)."
  value       = local.ssm_prefix
}

output "alarms_topic_arn" {
  description = "SNS topic the API alarms publish to. A human must subscribe and confirm — Terraform does not manage subscriptions."
  value       = aws_sns_topic.alarms.arn
}

# The API Lambda's execution role is the mailer's one registered caller
# (module.mailer's caller_role_name / caller_role_arn inputs): the mailer's
# SigV4 allowlist grants execute-api:Invoke, and the insolvia_api service
# registry entry's allowed_role_arns, to this role and no other.
output "lambda_role_name" {
  description = "API Lambda execution role name."
  value       = aws_iam_role.api.name
}

output "lambda_role_arn" {
  description = "API Lambda execution role ARN."
  value       = aws_iam_role.api.arn
}
