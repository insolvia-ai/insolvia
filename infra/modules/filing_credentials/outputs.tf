output "table_name" {
  description = "The credential table — insolvia-<env>-filing-credentials."
  value       = aws_dynamodb_table.credentials.name
}

output "table_arn" {
  description = "The credential table's ARN, for modules/audit_trail's data events."
  value       = aws_dynamodb_table.credentials.arn
}

output "kms_key_arn" {
  description = "The vault key's ARN — the key the password and TOTP seed are sealed under."
  value       = aws_kms_key.vault.arn
}

output "kms_key_alias" {
  description = "The vault key's alias — alias/insolvia-<env>-filing-credentials."
  value       = aws_kms_alias.vault.name
}

output "worker_role_name" {
  description = "The filing worker's execution role (services/filing, ADR 0024 PR 7)."
  value       = aws_iam_role.worker.name
}

output "worker_role_arn" {
  description = "The filing worker's execution role ARN — the one principal the key policy lets decrypt."
  value       = aws_iam_role.worker.arn
}

# The rendered documents, exposed so tests/filing_credentials.tftest.hcl can
# pin them whole. Policies are not secrets; they are in every plan already.
output "key_policy" {
  description = "The vault key's policy document, as rendered."
  value       = local.key_policy
}

output "worker_trust_policy" {
  description = "The filing worker role's trust policy, as rendered."
  value       = local.worker_trust_policy
}
