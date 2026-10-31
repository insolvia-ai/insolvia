# Issue 8.6 takes this rather than creating a second key: one key protects one
# case, documents and sealed tax ids alike.
output "kms_key_arn" {
  description = "Customer-managed key encrypting case data at rest."
  value       = aws_kms_key.case.arn
}

output "kms_key_alias" {
  description = "The case key's alias — alias/insolvia-<env>-cases (insolvia-kms-case-key-alias for the hosts)."
  value       = aws_kms_alias.case.name
}
