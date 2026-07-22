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
