# ── The credential vault (ADR 0024, guardrail 3) ────────────────
# An attorney's CM/ECF login — the PACER password and the TOTP seed — sealed
# under a DEDICATED key, openable by the filing worker's role and by nothing
# else. The sealed rows live in Aurora since ADR 0028 (filing_credentials,
# SELECT for the `filing` Postgres role alone); the DynamoDB table this
# module held was deleted in plan 18.23. ADR 0024 makes three properties binding, and this
# module is where each one is enforced rather than promised:
#
#   1. ONLY THE FILING WORKER CAN DECRYPT, AND THE KEY POLICY SAYS SO — not
#      only IAM. `local.key_policy` carries an explicit Deny on every verb
#      that turns a ciphertext into plaintext, for every principal that is
#      not the worker's role. An explicit deny in a key policy beats every
#      allow anywhere — the account root's delegation, an admin's
#      AdministratorAccess, a grant — so "nobody else holds Decrypt" is a
#      property of the key, not of the absence of a grant somebody might add.
#   2. THE API SEALS AND NEVER OPENS. Its grant is GenerateDataKey under the
#      vault's encryption-context purpose: it can mint the data key an
#      enrolment is sealed with, and it cannot unwrap one afterwards.
#   3. EVERY USE IS RECORDED. CloudTrail records each GenerateDataKey and
#      Decrypt on this key (with the encryption context — firm, attorney,
#      credential — in the event), via modules/audit_trail's management
#      events; the filing host appends the application
#      `credential.open` row to the case access log (an Aurora table since
#      ADR 0028), which names the attorney, the filing and the purpose.
#
# THE ESCAPE HATCH, NAMED. A principal holding kms:PutKeyPolicy can rewrite
# this policy, and one holding iam:UpdateAssumeRolePolicy can widen who
# assumes the worker. Both are held by the deploy role and the human admin
# group; neither is removable without making the key unmanageable (the
# root-delegation note below). The controls on them are the ones ci-trust
# already relies on for the case key: every PutKeyPolicy and every trust
# change is a CloudTrail management event the audit trail now retains
# (modules/audit_trail, include_management_events), and the change would have
# to arrive as a reviewed Terraform diff. ci-trust additionally denies the
# deploy role every decrypt verb on this key by alias, so even a pipeline
# that rewrote the key policy could not open a credential ITSELF.

data "aws_caller_identity" "current" {}

locals {
  # insolvia-<env>-filing-credentials — the KEY's alias (`alias/${local.name}`),
  # published to the hosts as insolvia-kms-filing-key-alias. ci-trust's
  # DenyFilingCredentialDecryption matches `alias/insolvia-*-filing-credentials`
  # and SealingKeys refuses any other shape; renaming here without renaming
  # there silently stops that deny matching.
  name = "${var.project}-${var.environment}-filing-credentials"

  # The filing worker's role — services/filing's Lambda (ADR 0024 PR 7)
  # assumes it. Named for the service it serves, `-role` per insolvia-aws-naming.
  worker_role_name = "${var.project}-${var.environment}-filing-role"

  account_id = data.aws_caller_identity.current.account_id

  # CONSTRUCTED, not read off aws_iam_role.worker.arn, and that is what makes
  # the policy below a pure function of names: the module's unit test
  # (tests/filing_credentials.tftest.hcl) pins the WHOLE rendered document
  # under a mocked provider, which could not know a generated ARN. The
  # aws_kms_key's depends_on below still orders the role first, because KMS
  # refuses a policy naming a principal that does not exist yet.
  worker_role_arn = "arn:aws:iam::${local.account_id}:role/${local.worker_role_name}"
  api_role_arn    = var.api_role_name == null ? null : "arn:aws:iam::${local.account_id}:role/${var.api_role_name}"

  # The envelope's encryption-context purpose — MUST equal
  # insolvia_core.filing_credentials.CREDENTIAL_PURPOSE. Every allow below
  # conditions on it, and the second deny refuses Decrypt without it even to
  # the worker, so the worker's grant opens vault envelopes and nothing else
  # it might one day be handed. Renaming one side alone does not fail an
  # apply; every enrolment starts failing with AccessDenied.
  credential_purpose = "filing-credential"

  key_policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(
      [
        {
          # The account root's delegation to IAM — modules/case_store says at
          # length why every key here keeps it: a key policy naming no
          # principal able to change it is unrecoverable. It does NOT make
          # the key openable by IAM: the two denies below are explicit, and an
          # explicit deny in a key policy beats this allow and every IAM
          # policy behind it.
          Sid       = "EnableIAMPolicies"
          Effect    = "Allow"
          Principal = { AWS = "arn:aws:iam::${local.account_id}:root" }
          Action    = "kms:*"
          Resource  = "*"
        },
        {
          # THE GUARDRAIL. Every principal but the worker's role is refused
          # Decrypt — the API's role, the deploy role, every human admin, and
          # in dev the developer who applied this key. aws:PrincipalArn on an
          # assumed-role session is the ROLE's ARN, so this matches every
          # session of the worker and no other principal.
          Sid       = "DenyDecryptToAllButFilingWorker"
          Effect    = "Deny"
          Principal = { AWS = "*" }
          Action    = "kms:Decrypt"
          Resource  = "*"
          Condition = {
            StringNotEquals = { "aws:PrincipalArn" = local.worker_role_arn }
          }
        },
        {
          # The other two routes to plaintext, refused to EVERYONE, the worker
          # included: ReEncrypt* is a decrypt whose plaintext is handed to a
          # key with a different policy, and CreateGrant is how a principal
          # this policy never names could be handed Decrypt by one it does.
          # Nothing that uses this key needs either.
          Sid       = "DenyReEncryptAndGrantsToEveryone"
          Effect    = "Deny"
          Principal = { AWS = "*" }
          Action    = ["kms:ReEncryptFrom", "kms:ReEncryptTo", "kms:CreateGrant"]
          Resource  = "*"
        },
        {
          # Even the worker opens only VAULT envelopes: a Decrypt that does
          # not carry the vault's purpose is refused. StringNotEquals is true
          # when the key is absent, so a context-free Decrypt is denied too.
          Sid       = "DenyOpenOutsideTheVaultContext"
          Effect    = "Deny"
          Principal = { AWS = "*" }
          Action    = "kms:Decrypt"
          Resource  = "*"
          Condition = {
            StringNotEquals = { "kms:EncryptionContext:purpose" = local.credential_purpose }
          }
        },
        {
          # The worker's open, granted BY THE KEY POLICY itself (ADR 0024:
          # "the key policy (not only IAM) allows kms:Decrypt to the filing
          # worker's role alone"). The role's own IAM policy repeats it, which
          # costs nothing and keeps the role readable on its own.
          Sid       = "AllowFilingWorkerOpen"
          Effect    = "Allow"
          Principal = { AWS = local.worker_role_arn }
          Action    = ["kms:Decrypt", "kms:DescribeKey"]
          Resource  = "*"
          Condition = {
            StringEquals = { "kms:EncryptionContext:purpose" = local.credential_purpose }
          }
        },
      ],
      local.api_role_arn == null ? [] : [
        {
          # SEAL-ONLY. GenerateDataKey hands back a fresh data key, plain and
          # wrapped; the API encrypts the enrolment with the plain half and
          # stores the wrapped half. It can never unwrap that half again —
          # Decrypt is denied to it above — so a compromised API can enrol a
          # credential and cannot read one back.
          Sid       = "AllowApiSeal"
          Effect    = "Allow"
          Principal = { AWS = local.api_role_arn }
          Action    = "kms:GenerateDataKey"
          Resource  = "*"
          Condition = {
            StringEquals = { "kms:EncryptionContext:purpose" = local.credential_purpose }
          }
        },
      ],
    )
  })

  worker_trust_policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(
      [
        {
          Sid       = "FilingWorkerLambda"
          Effect    = "Allow"
          Principal = { Service = "lambda.amazonaws.com" }
          Action    = "sts:AssumeRole"
        },
      ],
      length(var.worker_assumable_by) == 0 ? [] : [
        {
          # DEV ONLY — the precondition on aws_iam_role.worker refuses this
          # anywhere else. It is how a developer proves the worker's open on
          # a laptop against the real key policy (ADR 0024 PR 4's done-when).
          Sid       = "DeveloperAssumesFilingWorkerInDev"
          Effect    = "Allow"
          Principal = { AWS = var.worker_assumable_by }
          Action    = "sts:AssumeRole"
        },
      ],
    )
  })

  # What the filing worker may do FROM THE VAULT: open a credential's
  # envelope, under the vault purpose, and nothing else. The credential's ROW
  # is Aurora's since ADR 0028 (filing_credentials, SELECT for the `filing`
  # Postgres role alone), and so is the access row it appends; neither is an
  # IAM grant any more. Its queue grant is modules/filing_queue's; its
  # bucket, kill-switch and log grants are modules/filing_worker's, pinned
  # whole by that module's test.
  worker_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "CredentialOpen"
        Effect   = "Allow"
        Action   = ["kms:Decrypt"]
        Resource = aws_kms_key.vault.arn
        Condition = {
          StringEquals = { "kms:EncryptionContext:purpose" = local.credential_purpose }
        }
      },
    ]
  })

  # SEAL-ONLY: the API mints the data key an enrolment is sealed with and
  # can never unwrap it (the key policy denies it Decrypt). Enrol, status and
  # revoke are rows in Aurora since ADR 0028, not IAM grants.
  api_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "CredentialSeal"
        Effect   = "Allow"
        Action   = ["kms:GenerateDataKey"]
        Resource = aws_kms_key.vault.arn
        Condition = {
          StringEquals = { "kms:EncryptionContext:purpose" = local.credential_purpose }
        }
      },
    ]
  })
}

# ── The filing worker's role ────────────────────────────────────
# Created HERE, ahead of the service that will assume it (services/filing,
# ADR 0024 PR 7), because the key policy must name it and KMS refuses a
# policy naming a principal that does not exist. PR 7's Lambda takes this
# role by name rather than minting its own.
resource "aws_iam_role" "worker" {
  name               = local.worker_role_name
  assume_role_policy = local.worker_trust_policy
  tags               = var.tags

  lifecycle {
    precondition {
      condition     = length(var.worker_assumable_by) == 0 || startswith(var.environment, "dev-")
      error_message = "worker_assumable_by is for a developer's own dev env only. Staging and prod trust Lambda alone (ADR 0024, guardrail 3)."
    }
  }
}

resource "aws_iam_role_policy" "worker" {
  name   = "${local.name}-open"
  role   = aws_iam_role.worker.name
  policy = local.worker_policy
}

# ── The key ─────────────────────────────────────────────────────
resource "aws_kms_key" "vault" {
  description             = "Insolvia filing credentials (${var.environment}) — attorneys' CM/ECF password and TOTP seed. Only the filing worker may decrypt (ADR 0024, guardrail 3)."
  enable_key_rotation     = true
  deletion_window_in_days = var.key_deletion_window_in_days
  policy                  = local.key_policy
  tags                    = var.tags

  # The policy names the worker (and the API) role by constructed ARN; KMS
  # validates every principal at PutKeyPolicy, so the role must exist first.
  depends_on = [aws_iam_role.worker]
}

resource "aws_kms_alias" "vault" {
  name          = "alias/${local.name}"
  target_key_id = aws_kms_key.vault.key_id
}

# ── The API's seal-only grant ───────────────────────────────────
resource "aws_iam_role_policy" "api" {
  count = var.api_role_name == null ? 0 : 1

  name   = "${local.name}-seal"
  role   = var.api_role_name
  policy = local.api_policy
}
