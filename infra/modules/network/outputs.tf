output "vpc_id" {
  description = "The environment's VPC."
  value       = aws_vpc.main.id
}

output "vpc_cidr" {
  description = "The VPC's IPv4 block."
  value       = aws_vpc.main.cidr_block
}

output "private_subnet_ids" {
  description = "One private subnet per AZ, for the backend Lambdas' vpc_config and Aurora's subnet group."
  value       = aws_subnet.private[*].id
}

output "lambda_security_group_id" {
  description = "Security group for backend Lambdas. modules/aurora admits 5432 from it and from nothing else."
  value       = aws_security_group.lambda.id
}

output "nat_public_ip" {
  description = "The environment's stable egress address (what a counterparty would allow-list)."
  value       = aws_eip.nat.public_ip
}

output "nat_instance_id" {
  description = "The fck-nat instance."
  value       = aws_instance.nat.id
}
