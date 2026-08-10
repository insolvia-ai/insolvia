# ── Firm store (firms, firm users, and what they may do) ────────
# The tenancy layer. A case belongs to a FIRM, not to the person who opened it,
# and this table is what makes that sentence mean something: which firm a
# signed-in user belongs to, what their role is, and which features they may
# reach.
#
# WHY A TABLE OF ITS OWN rather than more partitions in insolvia-cases-<env>.
# Three reasons, and the first is the one that matters:
#
#   - It is read on EVERY authenticated request. Ownership resolution happens
#     before any case is touched, so this is the hottest read in the system and
#     it deserves its own capacity and its own metrics rather than being mixed
#     into case traffic.
#   - Its IAM grant is a different shape. The API needs to read every firm user
#     (it does not know the firm until it has read one) but write only through
#     the administration endpoints — not the same grant as case data, which is
#     read and written on the same paths.
#   - A table called insolvia-cases-<env> holding firms would be a naming lie,
#     and infra/CLAUDE.md's `insolvia-<thing>-<env>` convention is the thing
#     that keeps a reader able to guess what is where.
#
# It takes the CASE key rather than minting one, exactly as modules/case_documents
# does. Firm membership is not case data, but it is the thing that decides who
# reads case data, and a separate key would be a second thing to get the deny
# right on. ci-trust's DenyCaseDataDecryption covers alias/insolvia-cases-*, so
# reusing that key means CI provisions this table and can never read a firm's
# staff list.

locals {
  # insolvia-firms-<env>
  name = "${var.project}-firms-${var.environment}"
}

data "aws_iam_role" "api" {
  count = var.api_role_name == null ? 0 : 1
  name  = var.api_role_name
}

# ── The table ───────────────────────────────────────────────────
#   PK  FIRM#<firm_id>
#   SK  META                 the firm itself
#       USER#<subject>       one firm user, keyed by their Cognito sub
#
#   GSI1PK  USER#<subject>   sparse: only user items carry it
#   GSI1SK  FIRM#<firm_id>
#
# The GSI answers the one question the access token cannot. A token carries a
# `sub` and nothing else authorization-bearing — no groups, no custom
# attributes, no pre-token Lambda (infra/modules/auth/main.tf has none of
# them) — so "which firm is this person in, and what may they do" has to be a
# lookup, and it has to be possible without already knowing the firm.
#
# SPARSE ON PURPOSE. The firm's own META item carries no GSI keys, so the index
# holds exactly one entry per user rather than one per item. DynamoDB indexes an
# item only when it has every key attribute of the index, which makes omission
# the mechanism — and also the sharp edge: forget to write GSI1PK on a user and
# that user simply cannot sign in, with no error anywhere.
resource "aws_dynamodb_table" "firms" {
  name         = local.name
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "PK"
  range_key    = "SK"

  attribute {
    name = "PK"
    type = "S"
  }
  attribute {
    name = "SK"
    type = "S"
  }
  attribute {
    name = "GSI1PK"
    type = "S"
  }
  attribute {
    name = "GSI1SK"
    type = "S"
  }

  global_secondary_index {
    name = "by-subject"
    # ALL rather than KEYS_ONLY: the resolved firm user IS the payload — role,
    # admin flag, access scope and the permission map are all read on every
    # request. A projection that forced a second GetItem would double the
    # latency of every authenticated call to save a few bytes of storage.
    projection_type = "ALL"

    key_schema {
      attribute_name = "GSI1PK"
      key_type       = "HASH"
    }
    key_schema {
      attribute_name = "GSI1SK"
      key_type       = "RANGE"
    }
  }

  point_in_time_recovery { enabled = var.point_in_time_recovery }

  # The same customer-managed key the case store uses. Not a second key — see
  # the header.
  server_side_encryption {
    enabled     = true
    kms_key_arn = var.kms_key_arn
  }

  # Prod on, staging off, matching modules/case_store. Losing a firm's user
  # list locks every one of its staff out of every case they have.
  deletion_protection_enabled = var.deletion_protection

  tags = var.tags
}

# ── The one application principal ───────────────────────────────
# Attached from inside this module onto the role looked up above — the same
# shape modules/case_store and modules/case_documents use, and for the same
# reason: a resource reference across the boundary would make api_service depend
# on this module.
data "aws_iam_role" "admin" {
  count = var.admin_role_name == null ? 0 : 1
  name  = var.admin_role_name
}

resource "aws_iam_role_policy" "api_firm_access" {
  count = var.api_role_name == null ? 0 : 1

  name = "access-${local.name}"
  role = data.aws_iam_role.api[0].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        # READS ARE TABLE-WIDE AND THAT IS UNAVOIDABLE. The API cannot scope a
        # read to one firm's partition, because resolving which firm a caller
        # belongs to is itself the read — there is nothing to scope by until it
        # has happened. A dynamodb:LeadingKeys condition would need the firm id
        # in the request context, and the only thing in the token is a Cognito
        # sub.
        #
        # So tenant isolation here is an APPLICATION property, not an IAM one,
        # exactly as it is for the case table (see modules/case_store) and for
        # the same reason ADR 0001 gives: one execution role, one trust
        # boundary, enforcement in code that can be read and tested.
        Sid    = "FirmDirectory"
        Effect = "Allow"
        Action = [
          "dynamodb:GetItem",
          "dynamodb:Query",
          "dynamodb:PutItem",
          "dynamodb:UpdateItem",
          "dynamodb:DeleteItem",
          "dynamodb:BatchGetItem",
        ]
        Resource = [
          aws_dynamodb_table.firms.arn,
          "${aws_dynamodb_table.firms.arn}/index/*",
        ]
        Condition = {
          Bool = { "aws:SecureTransport" = "true" }
        }
      },
      {
        # Fenced to DynamoDB as the calling service, so this grant can never
        # become a direct Decrypt of a case row or a case document. Both other
        # modules fence their own the same way, in their own directions.
        Sid    = "FirmKeyUse"
        Effect = "Allow"
        Action = [
          "kms:Decrypt",
          "kms:GenerateDataKey",
          "kms:DescribeKey",
        ]
        Resource = var.kms_key_arn
        Condition = {
          StringEquals = {
            "kms:ViaService" = "dynamodb.${var.aws_region}.amazonaws.com"
          }
        }
      },
    ]
  })
}

# ── The second application principal (#213, ADR 0011) ───────────
# The admin service — the exception to "one application principal" that
# ADR 0011 records. Same shape as the API's grant above with ONE deliberate
# addition: dynamodb:Scan, which the API's grant deliberately lacks and must
# keep lacking. Scan is what cross-tenant listing costs (firm META items
# carry no GSI keys — that absence is what keeps the by-subject index
# sparse), and granting it HERE and not THERE is what makes "the tenant hot
# path cannot enumerate firms" an IAM fact rather than a code-review hope.
resource "aws_iam_role_policy" "admin_firm_access" {
  count = var.admin_role_name == null ? 0 : 1

  name = "admin-access-${local.name}"
  role = data.aws_iam_role.admin[0].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "FirmAdministration"
        Effect = "Allow"
        Action = [
          "dynamodb:GetItem",
          "dynamodb:Query",
          "dynamodb:Scan",
          "dynamodb:PutItem",
          "dynamodb:UpdateItem",
          "dynamodb:DeleteItem",
          "dynamodb:BatchGetItem",
        ]
        Resource = [
          aws_dynamodb_table.firms.arn,
          "${aws_dynamodb_table.firms.arn}/index/*",
        ]
        Condition = {
          Bool = { "aws:SecureTransport" = "true" }
        }
      },
      {
        # Same DynamoDB-only fence as the API's key statement.
        Sid    = "FirmKeyUse"
        Effect = "Allow"
        Action = [
          "kms:Decrypt",
          "kms:GenerateDataKey",
          "kms:DescribeKey",
        ]
        Resource = var.kms_key_arn
        Condition = {
          StringEquals = {
            "kms:ViaService" = "dynamodb.${var.aws_region}.amazonaws.com"
          }
        }
      },
    ]
  })
}
