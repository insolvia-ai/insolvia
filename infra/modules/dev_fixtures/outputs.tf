output "bucket_name" {
  description = "The shared fixture bucket. scripts/dev-aws-seed.sh and the staging seed step read fixture objects from it; `seed publish` writes them."
  value       = aws_s3_bucket.fixtures.id
}

output "bucket_arn" {
  description = "Fixture bucket ARN."
  value       = aws_s3_bucket.fixtures.arn
}
