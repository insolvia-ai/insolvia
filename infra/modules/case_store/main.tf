# ── Case data store (issue 8.2) ─────────────────────────────────
# The first persistent store of GLBA-scope data in this account: SSNs, full
# financials, and the source documents behind them. Business plan §10 and the
# Safeguards Rule floor in the regulatory register set the bar; the logical
# model it holds is docs/reference/case-data-model.md.
#
# It owns four things: a customer-managed KMS key, the case table, the API
# role's grant on both, and nothing else. The case DOCUMENT bucket (8.6) is
# deliberately not here — it will take this module's key ARN as an input
# rather than minting a second key, so that one key protects one case.
#
# WHY A SEPARATE MODULE rather than more of api_service: this store outlives
# any one service, and keeping it separate keeps the blast radius of an
# api_service change away from the data. The cost is the cross-module role
# reference below, which follows the mailer's precedent exactly.

data "aws_caller_identity" "current" {}
data "aws_region" "current" {}

# The one principal allowed to touch case rows. Looked up by name rather than
# passed as an ARN for the same reason module.mailer does it: a resource
# reference across a module boundary would make api_service depend on this
# module, and this module already depends on api_service.
#
# Optional, and null in exactly one place: infra/envs/dev, where there is no
# Lambda at all — the local API runs as a dev server or under compose and the
# developer's own IAM user is the principal. The waitlist table's dev instance
# skips its grant for the same reason.
data "aws_iam_role" "api" {
  count = var.api_role_name == null ? 0 : 1
  name  = var.api_role_name
}

locals {
  # insolvia-cases-<env>
  name = "${var.project}-cases-${var.environment}"

  # Every KMS action the API role needs, and no more.
  #
  # This is NOT dead weight, and it is the thing most likely to be deleted by
  # someone tidying up. The grant DynamoDB creates at table-creation time
  # covers its own key management, not caller reads: the table-key Decrypt is
  # issued ON BEHALF OF THE CALLING PRINCIPAL, and AWS's own documentation
  # notes the cached table key is per-caller and picks up IAM changes. Remove
  # these and every read fails once the cache expires — which is the worst
  # possible failure shape, because it passes review and passes the first
  # smoke test.
  #
  # Fenced to DynamoDB as the calling service, so the role can never turn this
  # into a direct Decrypt of a case document.
  api_key_actions = [
    "kms:Decrypt",
    "kms:GenerateDataKey",
    "kms:DescribeKey",
  ]
}

# ── The key ─────────────────────────────────────────────────────
# One key per environment. Staging and prod never share one: a key is the
# thing that makes prod data unreadable to a staging mistake.
#
# ON THE KEY POLICY. This grants the account root kms:*, which is AWS's
# default and reads alarmingly if you have not met it before. It does not mean
# "everyone can decrypt" — it means "IAM identity policies decide", which is
# what makes every other grant in this repo work, including the deploy role's
# explicit deny on the data-plane verbs (infra/envs/ci-trust).
#
# The alternative — omitting root and naming only the API role — is what
# actually delivers "no human read path" as a property of the key itself, and
# it is rejected on purpose: a key policy that names no principal able to
# change it is unrecoverable, and AWS documents exactly this as the way to
# make a key permanently unmanageable. The "no human read paths in prod" that
# issue 8.2 asks for is delivered instead by no human principal holding
# dynamodb data-plane or kms decrypt permissions, and by the audit trail
# recording it if one ever does.
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

resource "aws_kms_key" "case" {
  description             = "Insolvia case data (${var.environment}) — GLBA-scope. See docs/reference/case-data-model.md."
  enable_key_rotation     = true
  deletion_window_in_days = var.key_deletion_window_in_days
  policy                  = data.aws_iam_policy_document.key.json
  tags                    = var.tags
}

resource "aws_kms_alias" "case" {
  name          = "alias/${local.name}"
  target_key_id = aws_kms_key.case.key_id
}

# ── The table ───────────────────────────────────────────────────
# Single-table, partitioned by case. Every access pattern in
# docs/reference/case-data-model.md § "What this demands of the store" is
# either "fetch a whole case" or "list one entity type within a case", which
# is one Query on PK with an optional begins_with on SK:
#
#   PK = CASE#<case_id>
#   SK = META                     the case record itself
#      | <ENTITY>#<id>            DEBTOR#…, CLAIM#…, ASSET#…, SOFA#…, …
#
# The one genuinely cross-case read is "list the cases I own", which 8.3 needs
# for GET /v1/cases. That is the GSI, and it is SPARSE ON PURPOSE: only the
# META item carries the index attributes, so the index holds exactly one entry
# per case rather than one per row.
#
#   GSI1PK = OWNER#<principal>
#   GSI1SK = <created_at>#<case_id>       newest-first without a sort in the API
#
# GSI1SK sorts lexicographically, so created_at must be fixed-width RFC 3339
# with a literal Z — a "+00:00" offset is the same instant and sorts wrong,
# silently misordering GET /v1/cases. DynamoDB cannot enforce that; 8.3 owes
# it a contract test.
#
# Item-shape conventions are enforced in services/api's core layer, not here —
# DynamoDB validates only the key attributes below.
resource "aws_dynamodb_table" "cases" {
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

  # key_schema rather than the GSI's hash_key/range_key: the provider
  # deprecated those in 6.29.0 when it added multi-attribute index keys, and
  # `terraform validate` warns on them. Order matters — DynamoDB requires HASH
  # before RANGE, and this list is passed through verbatim.
  global_secondary_index {
    name            = "by-owner"
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

  # The whole point of this module. `enabled = true` alone would encrypt under
  # the AWS-owned DynamoDB key, which is not a key we control and not what
  # business plan §10 commits to.
  server_side_encryption {
    enabled     = true
    kms_key_arn = aws_kms_key.case.arn
  }

  # Prod on, staging off. A case table that can be destroyed by a plan is not
  # a case table; staging holds only synthetic data and stays disposable.
  deletion_protection_enabled = var.deletion_protection

  tags = var.tags
}

# ── The one application principal ───────────────────────────────
# Attached from inside this module onto the role looked up above — the mailer
# module's pattern, and the reason there is no dependency cycle.
#
# Data-plane only: no CreateTable, no UpdateTable, no DeleteTable. The deploy
# role manages the table's shape and can never read a row; this role reads and
# writes rows and can never change the shape. Per ADR 0001 it is also the only
# principal on either side of that line.
resource "aws_iam_role_policy" "api_case_access" {
  count = var.api_role_name == null ? 0 : 1

  name = "access-${local.name}"
  role = data.aws_iam_role.api[0].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "CaseTableData"
        Effect = "Allow"
        Action = [
          "dynamodb:GetItem",
          "dynamodb:BatchGetItem",
          "dynamodb:Query",
          "dynamodb:PutItem",
          "dynamodb:UpdateItem",
          "dynamodb:DeleteItem",
          "dynamodb:BatchWriteItem",
          "dynamodb:TransactGetItems",
          "dynamodb:TransactWriteItems",
          "dynamodb:ConditionCheckItem",
        ]
        Resource = [
          aws_dynamodb_table.cases.arn,
          "${aws_dynamodb_table.cases.arn}/index/*",
        ]
        # DynamoDB's endpoint accepts plain HTTP as well as HTTPS, and the SDK
        # choosing HTTPS is a default rather than a guarantee. Issue 8.2 puts
        # TLS in transit in scope, so this makes it one. Behaviour-neutral
        # today — nothing in services/api would notice.
        Condition = {
          Bool = { "aws:SecureTransport" = "true" }
        }
      },
      {
        # No dynamodb:Scan anywhere above — every read in the model is keyed,
        # and a Scan on this table is a full read of every debtor's financials.
        # If something ever genuinely needs one, it should have to come back
        # here and say so in a diff.
        Sid      = "CaseKeyUse"
        Effect   = "Allow"
        Action   = local.api_key_actions
        Resource = aws_kms_key.case.arn
        Condition = {
          StringEquals = {
            "kms:ViaService" = "dynamodb.${data.aws_region.current.region}.amazonaws.com"
          }
        }
      },
    ]
  })
}
