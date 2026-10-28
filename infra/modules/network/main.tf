# ── Network (ADR 0028 § Network, plan 18.5) ─────────────────────
# One VPC per deployed environment, for the .NET backend's Lambdas and Aurora.
# Nothing in it is attached to a Lambda yet: this module and modules/aurora land
# first so the switch PR (18.22) only has to point functions at them.
#
#   • two AZs, one private subnet in each — every backend Lambda and the
#     Aurora cluster live here and have no route to the internet gateway;
#   • ONE public subnet holding a fck-nat instance, the whole environment's
#     egress (Cognito and Google JWKS, the mailer, Anthropic, the CM/ECF
#     courts). One instance is a single point of failure for egress and ADR 0028
#     accepts that until the first real firm; the trigger to move to a managed
#     NAT gateway is in the ADR, not here;
#   • free GATEWAY endpoints for S3 and DynamoDB, so the heavy traffic (case
#     documents, and DynamoDB while the tables still exist) never crosses the
#     NAT. Every other AWS API goes through the NAT; interface endpoints
#     (~$7/month each) wait for measured traffic.
#
# NAMING. insolvia-<env>-network is the VPC's Name tag, and -private-<az>,
# -public, -nat and -lambda hang off it. These are Name TAGS: EC2 has no name
# attribute, so nothing looks anything up by them. Security group names are real
# names (and unique per VPC), so they carry the same strings.

data "aws_region" "current" {}

data "aws_availability_zones" "available" {
  state = "available"

  # Local Zones and Wavelength Zones are listed too and cannot host any of this.
  filter {
    name   = "opt-in-status"
    values = ["opt-in-not-required"]
  }
}

# The fck-nat community AMI (https://fck-nat.dev), looked up rather than pinned:
# a hard-coded ami id is region-specific and rots, and the project publishes
# new builds under the same name pattern. Owner 568608671756 is the fck-nat
# publisher's account (the one its documentation names), which is what stops a
# look-alike image in another account from matching the name filter.
#
# THE TRADE-OFF OF `most_recent`: a plan after a new fck-nat release would
# propose REPLACING the instance (an AMI change is a replacement), which is a few
# minutes without egress for the environment. The lifecycle block on the
# instance ignores the AMI so a routine plan does not do it unprompted — taint
# the instance to take an update.
data "aws_ami" "fck_nat" {
  most_recent = true
  owners      = ["568608671756"]

  filter {
    name   = "name"
    values = ["fck-nat-al2023-*"]
  }
  filter {
    name   = "architecture"
    values = ["arm64"]
  }
  filter {
    name   = "virtualization-type"
    values = ["hvm"]
  }
}

locals {
  name = "${var.project}-${var.environment}-network"

  # Exactly two AZs, by ADR. Sorted by the data source, so the pair is stable.
  azs = slice(data.aws_availability_zones.available.names, 0, 2)

  # /20 private subnets (4,091 addresses each) from the front of the /16, and
  # the one public subnet a /24 from the back. A Lambda holds an ENI per
  # concurrent execution environment, and Aurora needs a handful, so the
  # private side is the one that should have room.
  private_cidrs = [for i, _ in local.azs : cidrsubnet(var.vpc_cidr, 4, i)]
  public_cidr   = cidrsubnet(var.vpc_cidr, 8, 255)
}

resource "aws_vpc" "main" {
  cidr_block = var.vpc_cidr

  # Aurora's endpoint is a DNS name that must resolve inside the VPC.
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = merge(var.tags, { Name = local.name })
}

# The default security group allows all traffic between its members, and
# anything created without naming a group lands in it. Adopting it with no
# rules turns "forgot to name a group" into "cannot talk to anything".
resource "aws_default_security_group" "main" {
  vpc_id = aws_vpc.main.id
  tags   = merge(var.tags, { Name = "${local.name}-default" })
}

# ── Subnets ─────────────────────────────────────────────────────
resource "aws_subnet" "private" {
  count = length(local.azs)

  vpc_id            = aws_vpc.main.id
  availability_zone = local.azs[count.index]
  cidr_block        = local.private_cidrs[count.index]

  tags = merge(var.tags, { Name = "${local.name}-private-${substr(local.azs[count.index], -1, 1)}" })
}

# One public subnet, in the first AZ, for the NAT instance alone. Nothing else
# is placed here and nothing in it is reachable from outside: the instance's
# security group admits only the VPC.
resource "aws_subnet" "public" {
  vpc_id            = aws_vpc.main.id
  availability_zone = local.azs[0]
  cidr_block        = local.public_cidr

  tags = merge(var.tags, { Name = "${local.name}-public" })
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

# ── The NAT instance (fck-nat) ──────────────────────────────────
# Admits traffic from inside the VPC only; its egress is open because that is
# its job. fck-nat's own image does the masquerading — no user data needed.
resource "aws_security_group" "nat" {
  name        = "${local.name}-nat"
  description = "fck-nat instance: forwards the VPC's egress"
  vpc_id      = aws_vpc.main.id

  tags = merge(var.tags, { Name = "${local.name}-nat" })
}

resource "aws_vpc_security_group_ingress_rule" "nat_from_vpc" {
  security_group_id = aws_security_group.nat.id
  description       = "All traffic from inside the VPC, to be forwarded"
  ip_protocol       = "-1"
  cidr_ipv4         = var.vpc_cidr
}

resource "aws_vpc_security_group_egress_rule" "nat_out" {
  security_group_id = aws_security_group.nat.id
  description       = "Egress to the internet"
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_instance" "nat" {
  ami                    = data.aws_ami.fck_nat.id
  instance_type          = var.nat_instance_type
  subnet_id              = aws_subnet.public.id
  vpc_security_group_ids = [aws_security_group.nat.id]

  # The point of a NAT instance: it forwards packets that are not addressed to it.
  source_dest_check = false

  metadata_options {
    http_endpoint = "enabled"
    http_tokens   = "required"
  }

  root_block_device {
    encrypted = true
  }

  tags = merge(var.tags, { Name = "${local.name}-nat" })

  lifecycle {
    # See the AMI lookup above: a new fck-nat release must not replace the
    # instance on a routine plan.
    ignore_changes = [ami]
  }
}

# A stable public address for the environment's egress. The CM/ECF courts and
# the mailer are the kind of counterparty that ends up allow-listing a source
# IP, and an auto-assigned address changes every time the instance is replaced.
# A public IPv4 address bills the same hourly whether or not it is an Elastic IP.
resource "aws_eip" "nat" {
  domain = "vpc"
  tags   = merge(var.tags, { Name = "${local.name}-nat" })

  depends_on = [aws_internet_gateway.main]
}

resource "aws_eip_association" "nat" {
  allocation_id = aws_eip.nat.id
  instance_id   = aws_instance.nat.id
}

# The instance is the environment's only egress, so a failed host is recovered
# rather than waited on. Auto-recovery restarts it on healthy hardware with the
# same ENI, address and routes; an instance-OS failure (status check instance)
# is not covered and is not worth a second alarm before launch.
resource "aws_cloudwatch_metric_alarm" "nat_recover" {
  alarm_name          = "${local.name}-nat-recover"
  alarm_description   = "fck-nat host failed its system status check: recover it on new hardware."
  namespace           = "AWS/EC2"
  metric_name         = "StatusCheckFailed_System"
  dimensions          = { InstanceId = aws_instance.nat.id }
  statistic           = "Maximum"
  comparison_operator = "GreaterThanThreshold"
  threshold           = 0
  period              = 60
  evaluation_periods  = 2
  treat_missing_data  = "notBreaching"
  alarm_actions       = ["arn:aws:automate:${data.aws_region.current.region}:ec2:recover"]
  tags                = var.tags
}

# ── Private routing ─────────────────────────────────────────────
# One route table for both private subnets: there is one NAT, so a per-AZ table
# would only repeat the same default route. The route targets the instance's
# ENI, not the instance, so it survives an instance reboot.
resource "aws_route_table" "private" {
  vpc_id = aws_vpc.main.id
  tags   = merge(var.tags, { Name = "${local.name}-private" })
}

resource "aws_route" "private_nat" {
  route_table_id         = aws_route_table.private.id
  destination_cidr_block = "0.0.0.0/0"
  network_interface_id   = aws_instance.nat.primary_network_interface_id
}

resource "aws_route_table_association" "private" {
  count = length(local.azs)

  subnet_id      = aws_subnet.private[count.index].id
  route_table_id = aws_route_table.private.id
}

# ── Gateway endpoints (free) ────────────────────────────────────
# Prefix-list routes added to the private table; no per-hour or per-GB charge.
resource "aws_vpc_endpoint" "s3" {
  vpc_id            = aws_vpc.main.id
  service_name      = "com.amazonaws.${data.aws_region.current.region}.s3"
  vpc_endpoint_type = "Gateway"
  route_table_ids   = [aws_route_table.private.id]

  tags = merge(var.tags, { Name = "${local.name}-s3" })
}

resource "aws_vpc_endpoint" "dynamodb" {
  vpc_id            = aws_vpc.main.id
  service_name      = "com.amazonaws.${data.aws_region.current.region}.dynamodb"
  vpc_endpoint_type = "Gateway"
  route_table_ids   = [aws_route_table.private.id]

  tags = merge(var.tags, { Name = "${local.name}-dynamodb" })
}

# ── The Lambda security group ───────────────────────────────────
# Attached to every backend Lambda in the switch PR. No ingress: a Lambda is
# only ever called through the Lambda API, never by address. Egress is open
# (the NAT, the gateway endpoints, Aurora); the database side admits THIS group
# by id (modules/aurora), which is the actual fence.
resource "aws_security_group" "lambda" {
  name        = "${local.name}-lambda"
  description = "Backend Lambdas: no ingress; egress to the NAT, endpoints and Aurora"
  vpc_id      = aws_vpc.main.id

  tags = merge(var.tags, { Name = "${local.name}-lambda" })
}

resource "aws_vpc_security_group_egress_rule" "lambda_out" {
  security_group_id = aws_security_group.lambda.id
  description       = "All egress"
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}
