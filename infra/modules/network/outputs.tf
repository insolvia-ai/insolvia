output "vpc_id" {
  description = "The VPC's id."
  value       = aws_vpc.main.id
}

output "private_subnet_ids" {
  description = "The two private subnets — where the Lambdas and the database live."
  value       = aws_subnet.private[*].id
}

output "lambda_security_group_id" {
  description = "The security group every backend Lambda in the VPC wears."
  value       = aws_security_group.lambda.id
}

# The one shape every Lambda module's `vpc_config` variable takes.
output "lambda_vpc_config" {
  description = "subnet_ids and security_group_ids for a backend Lambda."
  value = {
    subnet_ids         = aws_subnet.private[*].id
    security_group_ids = [aws_security_group.lambda.id]
  }
}
