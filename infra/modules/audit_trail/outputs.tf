output "trail_name" {
  description = "CloudTrail trail recording case-data access."
  value       = aws_cloudtrail.audit.name
}

output "trail_arn" {
  description = "Trail ARN."
  value       = aws_cloudtrail.audit.arn
}

output "bucket_name" {
  description = "Bucket the trail writes to. The deploy role may write and may not delete — see ci-trust's DenyAuditLogErasure."
  value       = aws_s3_bucket.audit.id
}

output "kms_key_arn" {
  description = "Key encrypting the log files. Separate from the case key on purpose; nothing that can read case data can read this."
  value       = aws_kms_key.audit.arn
}
