output "cluster_identifier" {
  description = "Cluster identifier (insolvia-<env>-db)."
  value       = aws_rds_cluster.main.cluster_identifier
}

output "cluster_arn" {
  description = "Cluster ARN."
  value       = aws_rds_cluster.main.arn
}

output "endpoint" {
  description = "Writer endpoint. Published to SSM by the env root as /insolvia/<env>/db/endpoint."
  value       = aws_rds_cluster.main.endpoint
}

output "reader_endpoint" {
  description = "Reader endpoint. Unused while there is one instance (it resolves to the writer)."
  value       = aws_rds_cluster.main.reader_endpoint
}

output "port" {
  description = "Port the cluster listens on."
  value       = aws_rds_cluster.main.port
}

output "database_name" {
  description = "The application database."
  value       = aws_rds_cluster.main.database_name
}

output "cluster_resource_id" {
  description = "The cluster's immutable resource id (cluster-XXXX). What an rds-db:connect ARN names: arn:aws:rds-db:<region>:<account>:dbuser:<this>/<db user>. Not the identifier."
  value       = aws_rds_cluster.main.cluster_resource_id
}

output "master_secret_arn" {
  description = "ARN of the RDS-managed master credential in Secrets Manager. Only the migrator and a human break-glass may read it."
  value       = aws_rds_cluster.main.master_user_secret[0].secret_arn
}

output "security_group_id" {
  description = "The database's security group."
  value       = aws_security_group.db.id
}

output "kms_key_arn" {
  description = "Customer-managed key encrypting the cluster's storage, backups and snapshots."
  value       = aws_kms_key.db.arn
}

output "kms_key_alias" {
  description = "Human-facing alias for the database key."
  value       = aws_kms_alias.db.name
}
