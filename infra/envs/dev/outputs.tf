# The dev-aws scripts consume these: machine_id is the ownership check every
# script performs before touching anything, waitlist_table_name is written
# into services/api/.env, and the auth_* outputs are printed for upcoming
# local auth work.

output "machine_id" {
  description = "Machine UUID this state belongs to — the scripts refuse to act when it does not match the local machine-id file."
  value       = var.machine_id
}

output "environment" {
  description = "This machine's environment name (dev-<machine_short_id>)."
  value       = local.environment
}

output "waitlist_table_name" {
  description = "This machine's waitlist DynamoDB table (WAITLIST_TABLE_NAME for the local API)."
  value       = aws_dynamodb_table.waitlist.name
}

output "case_table_name" {
  description = "This machine's case DynamoDB table (CASE_TABLE_NAME for the local API)."
  value       = module.case_store.table_name
}

output "case_access_log_table_name" {
  description = "This machine's case access-log table (CASE_ACCESS_LOG_TABLE_NAME for the local API)."
  value       = module.case_store.access_log_table_name
}

output "case_kms_key_alias" {
  description = "Alias of this machine's case-data key. Printed for orientation; nothing consumes it — DynamoDB resolves the key from the table."
  value       = module.case_store.kms_key_alias
}

output "auth_user_pool_id" {
  description = "This machine's Cognito user pool ID."
  value       = module.auth.user_pool_id
}

output "auth_web_client_id" {
  description = "This machine's web SPA app client ID (authorization-code + PKCE)."
  value       = module.auth.web_client_id
}

output "auth_domain" {
  description = "This machine's hosted auth domain (Cognito-provided)."
  value       = module.auth.domain
}

output "auth_issuer_url" {
  description = "OIDC issuer for tokens from this machine's pool."
  value       = module.auth.issuer_url
}
output "firm_table_name" {
  description = "Firms, firm users and their permissions (FIRM_TABLE_NAME for the API)."
  value       = module.firm_store.table_name
}
