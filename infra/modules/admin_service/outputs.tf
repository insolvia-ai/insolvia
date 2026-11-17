output "lambda_function_name" {
  description = "Admin Lambda function name (deploy target for update-function-code/-configuration)."
  value       = aws_lambda_function.admin.function_name
}

output "lambda_alias_name" {
  description = "Alias serving traffic. The deploy workflow shifts it only after the new version passes its smoke test."
  value       = aws_lambda_alias.live.name
}

# The role modules/firm_store (admin_role_name) and modules/auth
# (admin_invite_role_name) attach their grants to — the seams live there so
# each grant sits beside the resource it opens.
output "lambda_role_name" {
  description = "Admin Lambda execution role name."
  value       = aws_iam_role.admin.name
}

output "lambda_role_arn" {
  description = "Admin Lambda execution role ARN."
  value       = aws_iam_role.admin.arn
}

output "domain_name" {
  description = "Hostname the admin API serves."
  value       = aws_apigatewayv2_domain_name.admin.domain_name
}

output "url" {
  description = "Public HTTPS base URL for the admin API."
  value       = "https://${var.domain_name}"
}

output "http_api_id" {
  description = "API Gateway HTTP API id (CloudWatch dimension, CLI operations)."
  value       = aws_apigatewayv2_api.admin.id
}

output "audit_table_name" {
  description = "Append-only admin audit table (#178's provisioning record)."
  value       = aws_dynamodb_table.audit.name
}

output "ssm_parameter_prefix" {
  description = "SSM namespace holding this environment's admin config (/insolvia/<env>/admin)."
  value       = local.ssm_prefix
}

output "alarms_topic_arn" {
  description = "SNS topic the admin alarms publish to. A human must subscribe and confirm — Terraform does not manage subscriptions."
  value       = aws_sns_topic.alarms.arn
}
