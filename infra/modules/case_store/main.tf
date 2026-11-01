# ── Case data key (issue 8.2) ───────────────────────────────────
# The customer-managed KMS key for GLBA-scope case data: SSNs, full
# financials, and the source documents behind them. Business plan §10 and the
# Safeguards Rule floor in the regulatory register set the bar; the logical
# model is docs/reference/case-data-model.md.
#
# It owns the key, its alias, and the roles' tax-id envelope grants on it,
# and nothing else. The case ROWS are Aurora's since ADR 0028 (the DynamoDB
# case and access-log tables this module held were deleted in plan 18.23);
# the case DOCUMENT bucket (modules/case_documents) takes this module's key
# ARN rather than minting a second key, so that one key protects one case.
#
# The module keeps its name, `case_store`, on purpose: renaming the call in an
# env root moves the key's address, and Terraform would destroy and re-create
# the key — which makes every sealed tax id and every document unreadable.

data "aws_caller_identity" "current" {}

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
# developer's own IAM user is the principal.

locals {
  # insolvia-<env>-cases — the KEY's alias (`alias/${local.name}`, below),
  # which every deployed environment hands its hosts explicitly
  # (insolvia-kms-case-key-alias). The alias must keep matching
  # `alias/insolvia-*-cases`: SealingKeys refuses any other, and ci-trust's
  # DenyCaseDataDecryption is written against it.
  name = "${var.project}-${var.environment}-cases"

  # The tax-id envelope's encryption-context purpose (issue 13.12 / #382) —
  # must equal insolvia_core.tax_ids.TAX_ID_PURPOSE. It is what lets the
  # TaxIdKeyUse statements below grant GenerateDataKey/Decrypt DIRECTLY (no
  # kms:ViaService fence) without opening the key to anything else the role
  # might one day be asked to decrypt: a call that does not carry this
  # context is denied, and a call that does is one this module designed.
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
# data-plane or kms decrypt permissions, and by the audit trail
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

# ── The application principals: the tax-id envelope only ───────
# Since the switch (ADR 0028) the case data is Aurora's. What a role holds on
# THIS key is the one direct use: the debtor's tax identifier,
# envelope-encrypted by the host itself (issue 13.12 / #382). (The S3 fence
# on the same key is modules/case_documents' and modules/filing_worker's.)
#
# The fence is the ENCRYPTION CONTEXT rather than a calling service: both
# verbs require the context's `purpose` member to be the one the hosts stamp
# (local.tax_id_purpose), so a grant here authorises exactly the tax-id
# envelope and cannot be turned into a general Decrypt of anything else
# sealed under the key (a case document's data key carries no such context
# and stays behind the S3 fence in modules/case_documents).
#
# The API role holds BOTH verbs: it seals on the debtor PUT and opens for the
# single-form B121 preview. The pipeline worker holds Decrypt alone (it prints
# identifiers, it never enters one), and the MCP role holds neither — "no
# tool returns a full tax identifier" is thereby an IAM fact as well as an
# application one (docs/reference/mcp-surface.md).
#
# Attached from inside this module onto the role named by var.api_role_name /
# var.worker_role_name — the shape the header's role-name note owns.
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

# The pipeline worker (ADR 0018): the API's TaxIdKeyUse NARROWED to Decrypt.
# Packet assembly opens each debtor's sealed identifier to print B121, and the
# review worker opens it to reproduce the packet's bytes — reads both, logged
# as `taxid.read` rows by the code that performs them. No GenerateDataKey:
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
