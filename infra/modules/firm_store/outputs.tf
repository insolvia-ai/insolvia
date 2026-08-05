output "table_name" {
  description = "Firm table name (FIRM_TABLE_NAME for the API). Published into the API's SSM namespace by the env root, not by this module — the same split modules/case_store makes."
  value       = aws_dynamodb_table.firms.name
}

output "table_arn" {
  description = "Firm table ARN."
  value       = aws_dynamodb_table.firms.arn
}

output "subject_index_name" {
  description = "Sparse GSI resolving a Cognito sub to its firm user. The only way in from a token."
  value       = "by-subject"
}
