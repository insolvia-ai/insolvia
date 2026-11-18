# The VPC the backend Lambdas and the Aurora cluster live in (ADR 0030).
#
#   • Two private subnets in two AZs — Aurora's subnet group needs two, and
#     the Lambdas spread across both. Nothing in them has a public address.
#   • One public subnet holding ONE fck-nat instance, the private subnets'
#     only way out (Cognito JWKS, the mailer, SES, the courts). A managed NAT
#     gateway is ~$32/month idle; a t4g.nano is ~$3. The cost of that choice is
#     a single point of egress: if the instance dies, Lambdas lose the
#     internet (not the database) until it is replaced. Accepted pre-launch;
#     revisit at launch with the rest of ADR 0030's capacity calls.
#   • Gateway endpoints for S3 and DynamoDB: free, and they keep document and
#     table traffic off the NAT instance. The DynamoDB one goes with the last
#     table (#626).

locals {
  name = "${var.project}-${var.environment}-network"
  azs  = slice(data.aws_availability_zones.available.names, 0, 2)
}

data "aws_availability_zones" "available" {
  state = "available"
}

data "aws_region" "current" {}

resource "aws_vpc" "main" {
  cidr_block           = var.cidr_block
  enable_dns_support   = true
  enable_dns_hostnames = true # the Aurora endpoints are DNS names
  tags                 = merge(var.tags, { Name = local.name })
}

resource "aws_subnet" "private" {
  count             = 2
  vpc_id            = aws_vpc.main.id
  availability_zone = local.azs[count.index]
  cidr_block        = cidrsubnet(var.cidr_block, 8, 10 + count.index)
  tags              = merge(var.tags, { Name = "${local.name}-private-${count.index}" })
}

resource "aws_subnet" "public" {
  vpc_id            = aws_vpc.main.id
  availability_zone = local.azs[0]
  cidr_block        = cidrsubnet(var.cidr_block, 8, 0)
  tags              = merge(var.tags, { Name = "${local.name}-public" })
}

resource "aws_internet_gateway" "main" {
  vpc_id = aws_vpc.main.id
  tags   = merge(var.tags, { Name = local.name })
}

resource "aws_route_table" "public" {
  vpc_id = aws_vpc.main.id
  tags   = merge(var.tags, { Name = "${local.name}-public" })
}

resource "aws_route" "public_internet" {
  route_table_id         = aws_route_table.public.id
  destination_cidr_block = "0.0.0.0/0"
  gateway_id             = aws_internet_gateway.main.id
}

resource "aws_route_table_association" "public" {
  subnet_id      = aws_subnet.public.id
  route_table_id = aws_route_table.public.id
}

# ── fck-nat: egress for the private subnets ─────────────────────
# The project's published AL2023 arm64 image; its owner id is fck-nat's
# account (https://fck-nat.dev). No SSH key and no inbound rule from outside
# the VPC: the instance is configured by its AMI and never logged into.
data "aws_ami" "fck_nat" {
  most_recent = true
  owners      = ["568608671756"]

  filter {
    name   = "name"
    values = ["fck-nat-al2023-*-arm64-ebs"]
  }
}

resource "aws_security_group" "nat" {
  name        = "${local.name}-nat"
  description = "fck-nat: forwards anything from inside the VPC, accepts nothing from outside it."
  vpc_id      = aws_vpc.main.id
  tags        = var.tags
}

resource "aws_vpc_security_group_ingress_rule" "nat_from_vpc" {
  security_group_id = aws_security_group.nat.id
  description       = "Traffic to be forwarded, from the private subnets"
  cidr_ipv4         = var.cidr_block
  ip_protocol       = "-1"
}

resource "aws_vpc_security_group_egress_rule" "nat_out" {
  security_group_id = aws_security_group.nat.id
  description       = "Forwarded traffic to the internet"
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "-1"
}

resource "aws_instance" "nat" {
  ami                         = data.aws_ami.fck_nat.id
  instance_type               = var.nat_instance_type
  subnet_id                   = aws_subnet.public.id
  vpc_security_group_ids      = [aws_security_group.nat.id]
  associate_public_ip_address = true
  source_dest_check           = false # it forwards packets that are not addressed to it

  metadata_options {
    http_tokens = "required"
  }

  root_block_device {
    encrypted = true
  }

  tags = merge(var.tags, { Name = "${local.name}-nat" })
}

resource "aws_route_table" "private" {
  vpc_id = aws_vpc.main.id
  tags   = merge(var.tags, { Name = "${local.name}-private" })
}

resource "aws_route" "private_egress" {
  route_table_id         = aws_route_table.private.id
  destination_cidr_block = "0.0.0.0/0"
  network_interface_id   = aws_instance.nat.primary_network_interface_id
}

resource "aws_route_table_association" "private" {
  count          = 2
  subnet_id      = aws_subnet.private[count.index].id
  route_table_id = aws_route_table.private.id
}

resource "aws_vpc_endpoint" "gateway" {
  for_each          = toset(["s3", "dynamodb"])
  vpc_id            = aws_vpc.main.id
  service_name      = "com.amazonaws.${data.aws_region.current.region}.${each.key}"
  vpc_endpoint_type = "Gateway"
  route_table_ids   = [aws_route_table.private.id]
  tags              = merge(var.tags, { Name = "${local.name}-${each.key}" })
}

# ── The backend Lambdas' security group ─────────────────────────
# Every Lambda in the private subnets wears this one. Outbound only; the
# database's group admits Postgres from it (modules/database).
resource "aws_security_group" "lambda" {
  name        = "${var.project}-${var.environment}-lambda"
  description = "Backend Lambdas: outbound only."
  vpc_id      = aws_vpc.main.id
  tags        = var.tags
}

resource "aws_vpc_security_group_egress_rule" "lambda_out" {
  security_group_id = aws_security_group.lambda.id
  description       = "The database, the gateway endpoints, and the internet through fck-nat"
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "-1"
}
