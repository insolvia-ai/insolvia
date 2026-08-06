output "bucket_name" {
  description = "Document bucket name (CASE_DOCUMENT_BUCKET for the API). Published into the API's SSM namespace by the env root, not by this module — the same split modules/case_store makes."
  value       = aws_s3_bucket.documents.id
}

output "bucket_arn" {
  description = "Document bucket ARN."
  value       = aws_s3_bucket.documents.arn
}
