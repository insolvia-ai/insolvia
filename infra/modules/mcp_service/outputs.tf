output "ecr_repository_url" {
  description = "ECR repository the deploy workflow pushes MCP images to (shared across environments)."
  value       = var.ecr_repository_url
}

output "lambda_alias_name" {
  description = "Alias serving traffic. The deploy workflow shifts it only after the new version passes its smoke test."
  value       = aws_lambda_alias.live.name
}

output "lambda_function_name" {
  description = "MCP Lambda function name (deploy target for update-function-code/-configuration)."
  value       = aws_lambda_function.mcp.function_name
}

output "domain_name" {
  description = "Hostname the MCP server serves — the authority of the canonical resource URI."
  value       = aws_apigatewayv2_domain_name.mcp.domain_name
}

output "url" {
  description = "Public HTTPS base URL. The MCP endpoint itself is <url>/mcp."
  value       = "https://${var.domain_name}"
}

output "http_api_id" {
  description = "API Gateway HTTP API id (CloudWatch dimension, CLI operations)."
  value       = aws_apigatewayv2_api.mcp.id
}

output "ssm_parameter_prefix" {
  description = "SSM namespace holding this environment's MCP config (/insolvia/<env>/mcp)."
  value       = local.ssm_prefix
}

output "alarms_topic_arn" {
  description = "SNS topic the MCP alarms publish to. A human must subscribe and confirm — Terraform does not manage subscriptions."
  value       = aws_sns_topic.alarms.arn
}

# The seam modules/case_store and modules/firm_store attach this service's
# data grants onto (mcp_role_name) — the third application principal, after
# the API (ADR 0001) and the admin service (ADR 0011).
output "lambda_role_name" {
  description = "MCP Lambda execution role name."
  value       = aws_iam_role.mcp.name
}

output "lambda_role_arn" {
  description = "MCP Lambda execution role ARN."
  value       = aws_iam_role.mcp.arn
}
