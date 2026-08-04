output "table_name" {
  description = "Case table name. Published into the API's SSM namespace by the env root, not by this module — see the env-level aws_ssm_parameter."
  value       = aws_dynamodb_table.cases.name
}

output "table_arn" {
  description = "Case table ARN."
  value       = aws_dynamodb_table.cases.arn
}

output "owner_index_name" {
  description = "Sparse GSI backing 'list the cases I own' (GET /v1/cases)."
  value       = "by-owner"
}

# Issue 8.6 takes this rather than creating a second key: one key protects one
# case, documents and rows alike.
output "kms_key_arn" {
  description = "Customer-managed key encrypting case data at rest."
  value       = aws_kms_key.case.arn
}

output "kms_key_alias" {
  description = "Human-facing alias for the case key."
  value       = aws_kms_alias.case.name
}

output "access_log_table_name" {
  description = "Append-only table recording which signed-in user read or changed which case (CASE_ACCESS_LOG_TABLE_NAME for the API)."
  value       = aws_dynamodb_table.access_log.name
}

output "access_log_table_arn" {
  description = "Access-log table ARN."
  value       = aws_dynamodb_table.access_log.arn
}
