# ── Aurora PostgreSQL (ADR 0028 § Data, plan 18.5) ─────────────
# The one relational database per environment that replaces every DynamoDB
# table: Aurora Serverless v2, PostgreSQL 17, in the private subnets of
# modules/network, encrypted under a key of its own.
#
# It owns the cluster and its one instance, the subnet and parameter groups, the
# security group, the KMS key, and the exported log group. It deliberately does
# NOT own any Lambda's access: authentication is IAM per service (each Lambda
# role maps to its own Postgres role), the rds-db:connect grants are made where
# the roles are made (the switch PR, 18.22), and the Postgres roles themselves
# are created by the migrator (18.12). This module publishes what those need —
# the cluster resource id above all — and nothing in it is attached to a Lambda.
#
# WHAT IS NOT HERE: a reader instance (one instance; a second doubles the idle
# floor the moment min capacity stops being 0), Performance Insights, Enhanced
# Monitoring, RDS Proxy (a Lambda per request opening one connection to a
# cluster that is often paused is the design; a proxy would hold it open and
# defeat auto-pause).

data "aws_caller_identity" "current" {}

locals {
  # insolvia-<env>-db — the cluster identifier, subnet group, security group
  # and key alias all share it. Component "db" names what it serves (the data
  # of every service), not the tier.
  name = "${var.project}-${var.environment}-db"

  # Aurora PostgreSQL 17's parameter group family.
  parameter_group_family = "aurora-postgresql17"
}

# ── The key ─────────────────────────────────────────────────────
# One key per environment, as modules/case_store: staging and prod never share
# one, because a key is what makes prod data unreadable to a staging mistake.
# It protects the cluster's storage, its automated backups and snapshots.
#
# THE KEY POLICY IS case_store's, and says the same thing: the account root may
# do anything, which means "IAM identity policies decide" — it is what makes the
# rest of this repo's IAM work and is not a grant to everyone. The reasoning for
# rejecting a policy that names only the service is case_store's too (a key
# policy that names no principal able to change it is unrecoverable).
#
# WHAT COULD AND COULD NOT BE KEPT FROM case_store's FENCE. The fence there is
# the deploy role's explicit deny on kms:Decrypt/GenerateDataKey for
# alias/insolvia-*-cases (insolvia-services' ci-trust). Here:
#
#   KEPT — no human or pipeline principal needs Decrypt on this key, and
#   nothing in this module grants any. Aurora is the only user of the data key:
#   RDS holds a KMS GRANT on it, made at cluster creation by whoever creates the
#   cluster, and uses it to decrypt storage inside the service. Creating an
#   encrypted cluster needs the CALLER to hold kms:CreateGrant and
#   kms:DescribeKey on the key — not Decrypt. So the same explicit deny can
#   cover alias/insolvia-*-db; adding the alias to it is an insolvia-services
#   change and is the one thing this module cannot do for itself.
#
#   NOT KEPT — the Lambdas never touch this key (they reach Aurora over TLS
#   with an IAM auth token, and the database decrypts transparently), which is
#   the property the case key has to work for with kms:ViaService fences. That
#   makes a tighter policy than case_store's possible in principle, but the
#   service principal's own use is by grant rather than by policy, so there is
#   no statement to fence and nothing for a ViaService condition to bind. The
#   fence that remains is IAM, not the key policy.
#
#   ALSO NOT KEPT, on purpose — the master user secret is NOT encrypted under
#   this key (see master_user_secret_kms_key_id below).
data "aws_iam_policy_document" "key" {
  statement {
    sid    = "EnableIAMPolicies"
    effect = "Allow"
    principals {
      type        = "AWS"
      identifiers = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:root"]
    }
    actions   = ["kms:*"]
    resources = ["*"]
  }
}

resource "aws_kms_key" "db" {
  description             = "Insolvia database (${var.environment}) — Aurora storage, backups and snapshots. See ADR 0028."
  enable_key_rotation     = true
  deletion_window_in_days = var.key_deletion_window_in_days
  policy                  = data.aws_iam_policy_document.key.json
  tags                    = var.tags
}

resource "aws_kms_alias" "db" {
  name          = "alias/${local.name}"
  target_key_id = aws_kms_key.db.key_id
}

# ── Network placement ───────────────────────────────────────────
resource "aws_db_subnet_group" "db" {
  name        = local.name
  description = "Insolvia ${var.environment} database: the private subnets"
  subnet_ids  = var.subnet_ids
  tags        = var.tags
}

# 5432 from the backend Lambdas' group and from nothing else. There is no
# egress rule at all: the database initiates no connections, and a security
# group created with no inline rules has none (Terraform removes AWS's default
# allow-all egress). A human break-glass reaches the cluster through a path
# built for the occasion, not through a standing hole in this group.
resource "aws_security_group" "db" {
  name        = local.name
  description = "Aurora: PostgreSQL from the backend Lambdas only"
  vpc_id      = var.vpc_id

  tags = merge(var.tags, { Name = local.name })
}

resource "aws_vpc_security_group_ingress_rule" "from_lambda" {
  security_group_id            = aws_security_group.db.id
  description                  = "PostgreSQL from the backend Lambdas"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = var.lambda_security_group_id
}

# ── Parameters ──────────────────────────────────────────────────
# TLS is required of every client. This is already the default on PostgreSQL 15+
# on RDS and is set explicitly so that a console edit or a default change
# cannot quietly turn it off: an IAM auth token is a bearer credential and
# must never cross the wire in the clear.
resource "aws_rds_cluster_parameter_group" "db" {
  name        = local.name
  family      = local.parameter_group_family
  description = "Insolvia ${var.environment} database cluster parameters"

  parameter {
    name  = "rds.force_ssl"
    value = "1"
  }

  tags = var.tags
}

# ── Logs ────────────────────────────────────────────────────────
# RDS would create this group itself with NEVER-EXPIRE retention on first
# export. Creating it first is how retention is set at all. The name is RDS's
# fixed pattern; if it drifts, the export creates a second, unmanaged group.
resource "aws_cloudwatch_log_group" "postgresql" {
  name              = "/aws/rds/cluster/${local.name}/postgresql"
  retention_in_days = var.log_retention_days
  tags              = var.tags
}

# ── The cluster ─────────────────────────────────────────────────
resource "aws_rds_cluster" "main" {
  cluster_identifier = local.name
  engine             = "aurora-postgresql"
  engine_mode        = "provisioned" # Serverless v2 is a provisioned cluster with db.serverless instances
  engine_version     = var.engine_version

  database_name   = var.database_name
  master_username = var.master_username

  # RDS generates the master password, stores it in Secrets Manager and rotates
  # it. It never passes through Terraform, so it is never in state — the
  # reason this module takes no password variable at all.
  #
  # master_user_secret_kms_key_id is left unset, so the secret is under the
  # AWS-managed aws/secretsmanager key rather than the CMK above. Putting it
  # under the CMK would make the creating principal (the CI deploy role) need
  # kms:Decrypt/GenerateDataKey on the database key to create the secret, which
  # is exactly the permission the case-data deny exists to keep from it. The
  # secret's access control is therefore IAM alone: nobody but the migrator and
  # a human break-glass should be granted secretsmanager:GetSecretValue on it,
  # and the deploy role must be explicitly denied it.
  manage_master_user_password = true

  db_subnet_group_name            = aws_db_subnet_group.db.name
  vpc_security_group_ids          = [aws_security_group.db.id]
  db_cluster_parameter_group_name = aws_rds_cluster_parameter_group.db.name
  port                            = 5432

  storage_encrypted = true
  kms_key_id        = aws_kms_key.db.arn

  # Each Lambda role maps to its own Postgres role via an IAM auth token
  # (rds-db:connect on the cluster resource id), so no service holds a password.
  iam_database_authentication_enabled = true

  # Aurora's continuous backup is its PITR: any second inside this window can
  # be restored to a new cluster.
  backup_retention_period         = var.backup_retention_days
  preferred_backup_window         = "07:00-08:00"
  copy_tags_to_snapshot           = true
  enabled_cloudwatch_logs_exports = ["postgresql"]

  # Deletion guards, the way case_store has them: protection is a variable,
  # true in prod, and the final snapshot follows it. A protected cluster that is
  # somehow destroyed still leaves a snapshot; a disposable one does not leave a
  # snapshot (and a bill) behind on every staging teardown. There is no
  # lifecycle.prevent_destroy: it would make the module un-destroyable in
  # staging, where destroy-and-reseed is the normal reset (ADR 0028 § Cut-over).
  deletion_protection       = var.deletion_protection
  skip_final_snapshot       = !var.deletion_protection
  final_snapshot_identifier = var.deletion_protection ? "${local.name}-final" : null

  apply_immediately = var.apply_immediately

  serverlessv2_scaling_configuration {
    min_capacity = var.min_capacity
    max_capacity = var.max_capacity

    # Only meaningful at min 0; RDS rejects it otherwise.
    seconds_until_auto_pause = var.min_capacity == 0 ? var.seconds_until_auto_pause : null
  }

  tags = var.tags

  lifecycle {
    # Minor-version upgrades are automatic (the instance sets
    # auto_minor_version_upgrade), so the running version moves ahead of the
    # variable and would otherwise plan a downgrade forever.
    ignore_changes = [engine_version]
  }

  depends_on = [aws_cloudwatch_log_group.postgresql]
}

# One writer, no reader. db.serverless is what makes the instance Serverless v2.
resource "aws_rds_cluster_instance" "writer" {
  identifier         = "${local.name}-1"
  cluster_identifier = aws_rds_cluster.main.id
  engine             = aws_rds_cluster.main.engine
  engine_version     = aws_rds_cluster.main.engine_version
  instance_class     = "db.serverless"

  publicly_accessible        = false
  auto_minor_version_upgrade = true

  tags = var.tags

  lifecycle {
    ignore_changes = [engine_version]
  }
}
