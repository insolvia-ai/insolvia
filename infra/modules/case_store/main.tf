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

# The one principal allowed to touch case rows arrives as var.api_role_name —
# a NAME, used directly by the policy attachments below, and deliberately
# neither of the two alternatives:
#
#   - Not a resource reference (an ARN output consumed as `role =`): that
#     direction of reference would make api_service depend on this module,
#     and this module already depends on api_service.
#   - Not a `data "aws_iam_role"` lookup on the name: a data source whose
#     config is a known string is read at PLAN time, so an environment whose
#     first apply creates the role and its grants together fails before it
#     creates anything — "reading IAM Role (…): couldn't find resource", and
#     re-running can never fix it. The admin service's first staging apply
#     hit exactly this. Using the name directly keeps the ordering edge (the
#     env root passes module.<service>.lambda_role_name, so the attachment
#     still waits for the role) without the plan-time read.
#
# Every module with a grant seam follows this shape and points here.
#
# Optional, and null in exactly one place: infra/envs/dev, where there is no
# Lambda at all — the local API runs as a dev server or under compose and the
# developer's own IAM user is the principal. The waitlist table's dev instance
# skips its grant for the same reason.

locals {
  # insolvia-<env>-cases
  name = "${var.project}-${var.environment}-cases"

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
# TWO cross-case reads, because "list the cases I may see" has two answers.
# Both indexes are SPARSE: only the item that carries the key attributes
# appears, so neither holds one entry per row.
#
#   by-firm       GSI1PK = FIRM#<firm_id>              on the META item
#                 GSI1SK = <created_at>#<case_id>
#
#   by-assignee   GSI2PK = ASSIGNEE#<subject>          on ASSIGNEE#… items
#                 GSI2SK = <created_at>#<case_id>
#
# WHICH ONE A REQUEST USES DEPENDS ON THE CALLER, and that is the awkward part
# of this design rather than a detail to discover later. A firm admin, or
# anyone whose firm user carries `access_all_cases`, lists through by-firm. A
# user restricted to their assigned matters lists through by-assignee. The two
# indexes return different sets by construction, so a pagination cursor minted
# against one is meaningless against the other — services/api must refuse a
# mismatched cursor rather than silently skip cases.
#
# It was GSI1PK = OWNER#<principal> until firms existed, which made a case the
# property of one Cognito subject: a colleague at the same firm got a 404, and
# two people could not work one matter. Renaming the index is what forces the
# rest of the system to stop pretending otherwise.
#
# Both sort keys sort lexicographically, so created_at must be fixed-width
# RFC 3339 with a literal Z — a "+00:00" offset is the same instant and sorts
# wrong, silently misordering the listing. DynamoDB cannot enforce that; the
# core layer owes it a contract test.
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
  attribute {
    name = "GSI2PK"
    type = "S"
  }
  attribute {
    name = "GSI2SK"
    type = "S"
  }

  # key_schema rather than the GSI's hash_key/range_key: the provider
  # deprecated those in 6.29.0 when it added multi-attribute index keys, and
  # `terraform validate` warns on them. Order matters — DynamoDB requires HASH
  # before RANGE, and this list is passed through verbatim.
  #
  # RENAMING AN INDEX REPLACES IT. `by-owner` -> `by-firm` is a destroy and a
  # create, not an update, and DynamoDB backfills a new GSI from the table
  # rather than from the old index. Staging and prod hold zero items so there
  # is nothing to backfill; a developer machine may hold probe rows, which
  # scripts/dev-aws-reset.sh clears. Read the plan before applying this to
  # anything holding real cases — after which it is a migration, not an edit.
  #
  # AND IT TAKES TWO APPLIES, which is DynamoDB's constraint rather than
  # Terraform's: **one index may be created or deleted per UpdateTable call**.
  # Going from one index to two therefore needs the first apply to finish and
  # the new index to reach ACTIVE before the second can start. Observed on dev:
  # the first apply dropped by-owner and created by-firm, a re-run created
  # by-assignee. A CI apply that appears to succeed while leaving an index
  # missing is this, and re-running is the fix.
  global_secondary_index {
    name            = "by-firm"
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

  # The restricted-access listing. Its entries are ASSIGNEE#<subject> items in
  # the case's own partition — the assignment IS the index entry, so linking a
  # user to a case and making it appear in their list are one write.
  global_secondary_index {
    name            = "by-assignee"
    projection_type = "ALL"

    key_schema {
      attribute_name = "GSI2PK"
      key_type       = "HASH"
    }
    key_schema {
      attribute_name = "GSI2SK"
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

# ── The access log ──────────────────────────────────────────────
# The log that answers "which signed-in user read this case", which the
# CloudTrail trail in modules/audit_trail structurally cannot: ADR 0001 makes
# the API role the only principal AWS ever sees, so the end user's identity
# exists only inside the request. This table is where the API writes it down.
#
# The two are complementary and neither replaces the other. CloudTrail proves
# nothing but the API touched the store; this proves who asked the API to.
#
#   PK = CASE#<case_id>
#   SK = <recorded_at>#<event_id>   chronological within a case
#
# Keyed by case because that is the question actually asked — of a client, in
# a dispute, or in a breach notice: who saw THIS file. "What did this account
# touch" wants a by-principal index instead; it is deliberately not here yet,
# and a GSI can be added online later without a migration.
#
# READS ARE LOGGED, not just writes. A write log answers "who changed this",
# which the provenance fields in the case record already answer better. The
# question this table exists for is who *saw* it.
resource "aws_dynamodb_table" "access_log" {
  name         = "${var.project}-${var.environment}-case-access-log"
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

  # NO TTL, and it cannot be added here. This table had `ttl { attribute_name
  # = "expires_at", enabled = true }` and it broke every staging deploy, for a
  # reason that is structural rather than a missing permission:
  #
  #   DynamoDB's UpdateTimeToLive is authorised against the CALLER, and on a
  #   CMK-encrypted table it needs kms:Decrypt on that key. This table is
  #   encrypted under the case key on purpose (see below), and ci-trust's
  #   DenyCaseDataDecryption denies the deploy role kms:Decrypt on exactly
  #   alias/insolvia-*-cases. An explicit deny wins, so the create half-
  #   succeeded — table made, TTL rejected — leaving a tainted resource that
  #   every later run destroyed and failed to recreate. Four merges in a row.
  #
  # There is no condition key that separates "enable TTL" from "read a row":
  # both are kms:ViaService = dynamodb. Granting Decrypt back would hand CI the
  # case data the deny exists to withhold, and re-keying the log to an alias
  # outside the pattern would hand it the access log. Both are worse than
  # having no TTL.
  #
  # It is also the posture DenyAuditLogErasure already argues for the trail
  # bucket: a retention rule the deploy pipeline can set is a delete button the
  # deploy pipeline can press, and TTL deletes silently. Retention here is a
  # compliance decision that belongs to the regulatory register and to a human
  # apply, not to a Terraform default. Until it is made, rows are kept.
  #
  # PITR is fine on the same key and stays: enabling it needs no data-plane
  # verb, which is why the case table has always applied cleanly.
  #
  # SPELLED OUT rather than deleted, and that is not style. `ttl` is
  # optional+computed in the provider, so simply removing the block produces
  # "No changes" against a table that already has TTL on — the config stops
  # describing reality and never converges. Verified against a dev env that had
  # it enabled: block deleted → no diff; block present and disabled → one
  # in-place change. On a table being created this is the default, so the
  # provider issues no UpdateTimeToLive and the deploy role needs nothing.
  #
  # The name must stay `expires_at`, and not because anything reads it. An
  # attribute name is required even to disable (an empty one is a
  # ValidationException) and DynamoDB will only accept the name TTL is
  # CURRENTLY active on — anything else fails with "TimeToLive is active on a
  # different AttributeName". Any dev env that applied the old config has it on
  # `expires_at`, so this is what lets those converge instead of erroring
  # forever. Both were found by running it; neither is in the provider docs.
  #
  # It is inert either way. Turning `enabled` on is not a one-line change: it
  # needs an attribute something actually writes — nothing does, and the old
  # writer used `expiresAt`, which is how this went unnoticed — AND it will
  # fail in CI on the KMS deny above.
  ttl {
    attribute_name = "expires_at"
    enabled        = false
  }

  point_in_time_recovery { enabled = var.point_in_time_recovery }

  # Same key as the case table. That is deliberate: it means the deploy role,
  # already denied every data-plane verb on alias/insolvia-*-cases, cannot read
  # the access log either.
  server_side_encryption {
    enabled     = true
    kms_key_arn = aws_kms_key.case.arn
  }

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

  name = "${local.name}-access"
  role = var.api_role_name

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
        # APPEND-ONLY, and the omissions are the design. No GetItem, no Query,
        # no UpdateItem, no DeleteItem: the API writes access records and can
        # never read, amend or remove one. An audit log the audited service can
        # rewrite is not evidence, and the waitlist table's PutItem-only grant
        # is the same reasoning.
        #
        # The consequence is that no endpoint can serve an access history
        # today. That is a real limitation and the right default — serving it
        # means granting Query, which should be its own decision with its own
        # diff, not a capability that arrived by accident.
        Sid      = "CaseAccessLogAppend"
        Effect   = "Allow"
        Action   = ["dynamodb:PutItem"]
        Resource = aws_dynamodb_table.access_log.arn
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

# ── The second application principal: the pipeline worker ───────
# ADR 0018 amends "exactly one principal reads this table" the way ADR 0011
# amended ADR 0001: the worker Lambda is the API's own long-running half — it
# advances the job rows the API accepted, and (with 9.6/9.7) reads the case
# it is assembling — under a role of its own so its blast radius is its own.
#
# Same statements as the API's grant, minus one, and the omission is the
# design: NO access-log append. The worker records nothing in the access log
# today — the accept was logged by the API as job.accept, and per-read
# logging inside workers arrives with the workers that actually read case
# data (9.6/9.7), as a grant added in a diff that says so. No Scan, same as
# the API, for the same reason.
resource "aws_iam_role_policy" "worker_case_access" {
  count = var.worker_role_name == null ? 0 : 1

  name = "${local.name}-worker-access"
  role = var.worker_role_name

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
          "dynamodb:ConditionCheckItem",
        ]
        Resource = [
          aws_dynamodb_table.cases.arn,
          "${aws_dynamodb_table.cases.arn}/index/*",
        ]
        Condition = {
          Bool = { "aws:SecureTransport" = "true" }
        }
      },
      {
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
