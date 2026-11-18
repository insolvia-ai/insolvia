output "endpoint" {
  description = "The cluster's writer endpoint."
  value       = aws_rds_cluster.database.endpoint
}

# The services' DATABASE_URL, per Postgres role: no password — IAM
# authentication mints a short-lived token as the password at connect time.
output "database_urls" {
  description = "Postgres role -> its DATABASE_URL (IAM auth, TLS required)."
  value = {
    for user in keys(var.iam_users) :
    user => "postgresql://${user}@${aws_rds_cluster.database.endpoint}:5432/${aws_rds_cluster.database.database_name}?sslmode=require"
  }
}

output "migrator_function_name" {
  description = "The migrator Lambda the deploy workflow invokes before the alias shift."
  value       = aws_lambda_function.migrator.function_name
}

output "kms_key_arn" {
  description = "The database key."
  value       = aws_kms_key.database.arn
}

output "key_policy" {
  description = "The rendered key policy — pinned by tests/database.tftest.hcl."
  value       = local.key_policy
}
