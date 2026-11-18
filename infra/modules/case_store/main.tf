# ── Case key (issue 8.2) ────────────────────────────────────────
# The customer-managed KMS key for case data, and the application roles'
# direct use of it for the tax-id envelope. Case ROWS live in Postgres since
# ADR 0030 (modules/database, whose cluster has its own key); this key now
# protects the case-documents bucket (modules/case_documents takes its ARN)
# and every sealed tax id, so one key still protects one case's files and
# identifiers.
#
# The module keeps its historical name so the key's state address does not
# move: renaming it would be a `moved` block for no change in AWS.
#
# The roles arrive as NAMES (var.api_role_name, var.worker_role_name), not
# resource references or `data "aws_iam_role"` lookups: a reference would
# make the service modules depend on this one in a cycle, and a data source
# on a known string is read at PLAN time, so an environment whose first
# apply creates the role and its grants together fails before it creates
# anything. Using the name keeps the ordering edge (the env root passes
# module.<service>.lambda_role_name) without the plan-time read. Every module
# with a grant seam follows this shape and points here.
#
# Null in infra/envs/dev, where there is no Lambda: the local services run
# under the developer's own IAM user.

data "aws_caller_identity" "current" {}

locals {
  # insolvia-<env>-cases — the KEY's alias (`alias/${local.name}`) and the
  # value the services read as CASE_TABLE_NAME, a historical name: the
  # tax-id ciphers (insolvia_core.adapters.aws.tax_id_cipher,
  # `case_key_alias`) and the vault (`filing_credentials_key_alias`) derive
  # their key aliases from it, in every environment including a developer's
  # `insolvia-dev-<short-id>-cases`. Change it and every seal starts failing
  # with NotFoundException on the next deploy.
  name = "${var.project}-${var.environment}-cases"

  # The tax-id envelope's encryption-context purpose (issue 13.12 / #382) —
  # must equal insolvia_core.tax_ids.TAX_ID_PURPOSE. It is what lets the
  # TaxIdKeyUse statements below grant GenerateDataKey/Decrypt DIRECTLY
  # without opening the key to anything else the role might one day be asked
  # to decrypt: a call that does not carry this context is denied.
  tax_id_purpose = "debtor-tax-id"
}

# ── The key ─────────────────────────────────────────────────────
# One key per environment. Staging and prod never share one: a key is the
# thing that makes prod data unreadable to a staging mistake.
#
# ON THE KEY POLICY. This grants the account root kms:*, which is AWS's
# default and reads alarmingly if you have not met it before. It does not mean
# "everyone can decrypt" — it means "IAM identity policies decide", which is
# what makes every other grant in this repo work, including the deploy role's
# explicit deny on the data-plane verbs (insolvia-services' infra/envs/ci-trust).
#
# The alternative — omitting root and naming only the API role — is what
# actually delivers "no human read path" as a property of the key itself, and
# it is rejected on purpose: a key policy that names no principal able to
# change it is unrecoverable, and AWS documents exactly this as the way to
# make a key permanently unmanageable. The "no human read paths in prod" that
# issue 8.2 asks for is delivered instead by no human principal holding
# case-data or kms decrypt permissions, and by the audit trail
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


# ── The API ─────────────────────────────────────────────────────
# THE ONE DIRECT USE OF THE KEY BY THE API (issue 13.12 / #382): the debtor's
# tax identifier is envelope-encrypted by the API itself — a data key
# generated under this key per sealed value, opened again only for the form
# that prints the number (B121). Both verbs require the context's `purpose`
# member to be the one insolvia_core.tax_ids stamps, so this statement
# authorises exactly the tax-id envelope and cannot be turned into a general
# Decrypt of anything else sealed under the key (a case document's data key
# carries no such context and stays behind the S3 fence in
# modules/case_documents).
#
# The API role holds BOTH verbs: it seals on the debtor PUT and opens for the
# single-form B121 preview. The worker's grant below holds Decrypt alone (it
# prints identifiers, it never enters one), and the MCP role holds neither —
# the last-four view is on the plain row, and "no tool returns a full tax
# identifier" is thereby an IAM fact as well as an application one
# (docs/reference/mcp-surface.md).
resource "aws_iam_role_policy" "api_case_access" {
  count = var.api_role_name == null ? 0 : 1

  name = "${local.name}-access"
  role = var.api_role_name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "TaxIdKeyUse"
        Effect = "Allow"
        Action = [
          "kms:GenerateDataKey",
          "kms:Decrypt",
        ]
        Resource = aws_kms_key.case.arn
        Condition = {
          StringEquals = {
            "kms:EncryptionContext:purpose" = local.tax_id_purpose
          }
        }
      },
    ]
  })
}

# ── The pipeline worker ─────────────────────────────────────────
# The API's TaxIdKeyUse, NARROWED to Decrypt (issue 13.12 / #382): packet
# assembly opens each debtor's sealed identifier to print B121, and the review
# worker opens it to reproduce the packet's bytes — reads both, logged as
# `taxid.read` rows by the code that performs them. No GenerateDataKey:
# nothing in the worker enters a tax id, so nothing in the worker can seal
# one. Same context fence.
resource "aws_iam_role_policy" "worker_case_access" {
  count = var.worker_role_name == null ? 0 : 1

  name = "${local.name}-worker-access"
  role = var.worker_role_name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "TaxIdKeyUse"
        Effect   = "Allow"
        Action   = ["kms:Decrypt"]
        Resource = aws_kms_key.case.arn
        Condition = {
          StringEquals = {
            "kms:EncryptionContext:purpose" = local.tax_id_purpose
          }
        }
      },
    ]
  })
}
