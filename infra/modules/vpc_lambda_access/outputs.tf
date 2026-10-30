output "network_policy_name" {
  description = "The ENI policy's name. Referencing it (or depends_on the module) orders a Lambda's vpc_config after the grant Lambda checks for."
  value       = aws_iam_role_policy.network.name
}

output "db_username" {
  description = "The Postgres role this execution role may log in as (null for the migrator)."
  value       = var.db_username
}
