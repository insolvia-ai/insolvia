output "name" {
  description = "insolvia-<env>-cases: the case key's alias name, which the services read as CASE_TABLE_NAME (a historical name) to derive their key aliases. Published into the API's SSM namespace by the env root."
  value       = local.name
}

# Issue 8.6 takes this rather than creating a second key: one key protects one
# case's documents and sealed identifiers alike.
output "kms_key_arn" {
  description = "Customer-managed key for case documents and sealed tax ids."
  value       = aws_kms_key.case.arn
}

output "kms_key_alias" {
  description = "Human-facing alias for the case key."
  value       = aws_kms_alias.case.name
}
