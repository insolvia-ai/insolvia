# ── The credential vault (ADR 0024, guardrail 3) ────────────────
# An attorney's CM/ECF login — the PACER password and the TOTP seed — sealed
# under a DEDICATED key in a DEDICATED table, openable by the filing worker's
# role and by nothing else. ADR 0024 makes three properties binding, and this
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
#      credential — in the event) and the data events on this table, via
#      modules/audit_trail; the worker's role can append the application
#      `credential.open` row to the case access log, which names the
#      attorney, the filing and the purpose (insolvia_core.filing_credentials).
#
# WHY THE TABLE IS ENCRYPTED UNDER THE CASE KEY AND NOT THE VAULT KEY. DynamoDB
# decrypts a CMK-encrypted table's keys AS THE CALLER (kms:ViaService =
# dynamodb), so a table under the vault key would need the API's role to hold
# kms:Decrypt on the vault key — through DynamoDB, but Decrypt all the same,
# and the deny in (1) would have to carve an exception for it. A key whose
# policy reads "Decrypt: the worker, plus anyone via DynamoDB" is a weaker
# sentence than "Decrypt: the worker". So the two jobs take two keys: the
# table's at-rest wrapper is the case key, exactly as modules/firm_store's
# is (ci-trust's DenyCaseDataDecryption already fences it from the
# pipeline), and the SECRET inside each item is sealed under the vault key.
# The non-secret attributes — the login name, the courts, the status — are
# what the case key protects; the password and seed are what this key does.
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
data "aws_region" "current" {}

locals {
  # insolvia-<env>-filing-credentials — the TABLE's name AND the KEY's alias
  # (`alias/${local.name}`). One local on purpose, the case store's pattern:
  # insolvia_core.adapters.aws.filing_credentials derives both from the case
  # table name the API is already configured with, so no environment carries
  # a new configuration value. ci-trust's DenyFilingCredentialDecryption
  # matches `alias/insolvia-*-filing-credentials`; renaming here without
  # renaming there silently stops that deny matching.
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

  # What the filing worker may do, and it is exactly what PR 7's Lambda needs
  # FROM THE VAULT: read a credential's item (its status is re-checked
  # immediately before the final submit — ADR 0024), open its envelope, and
  # append the access row. No Put/Update/Delete on the vault: the worker
  # never enrols or revokes. Its queue grant is modules/filing_queue's; its
  # case-table, bucket, kill-switch and log grants are modules/filing_worker's
  # (ADR 0024 PR 7), pinned whole by that module's test.
  worker_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "CredentialRead"
        Effect   = "Allow"
        Action   = ["dynamodb:GetItem"]
        Resource = "arn:aws:dynamodb:${data.aws_region.current.region}:${local.account_id}:table/${local.name}"
        Condition = {
          Bool = { "aws:SecureTransport" = "true" }
        }
      },
      {
        Sid      = "CredentialOpen"
        Effect   = "Allow"
        Action   = ["kms:Decrypt"]
        Resource = aws_kms_key.vault.arn
        Condition = {
          StringEquals = { "kms:EncryptionContext:purpose" = local.credential_purpose }
        }
      },
      {
        # Append-only, the API's CaseAccessLogAppend posture: the worker
        # records that it opened a credential and can never read the log.
        Sid      = "CredentialAccessLogAppend"
        Effect   = "Allow"
        Action   = ["dynamodb:PutItem"]
        Resource = var.access_log_table_arn
        Condition = {
          Bool = { "aws:SecureTransport" = "true" }
        }
      },
      {
        # The table's (and the access log's) at-rest key, through DynamoDB
        # only — modules/case_store's api_key_actions note says why this is
        # not dead weight.
        Sid      = "TableKeyThroughDynamoDb"
        Effect   = "Allow"
        Action   = ["kms:Decrypt", "kms:GenerateDataKey", "kms:DescribeKey"]
        Resource = var.table_kms_key_arn
        Condition = {
          StringEquals = { "kms:ViaService" = "dynamodb.${data.aws_region.current.region}.amazonaws.com" }
        }
      },
    ]
  })

  api_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        # Enrol (PutItem), status (Query, GetItem), revoke (DeleteItem — the
        # item is DESTROYED, ADR 0024: revocation is not a flag the worker
        # might fail to read). No Scan: every read names the attorney.
        Sid    = "CredentialRows"
        Effect = "Allow"
        Action = [
          "dynamodb:PutItem",
          "dynamodb:GetItem",
          "dynamodb:Query",
          "dynamodb:DeleteItem",
        ]
        Resource = aws_dynamodb_table.credentials.arn
        Condition = {
          Bool = { "aws:SecureTransport" = "true" }
        }
      },
      {
        Sid      = "CredentialSeal"
        Effect   = "Allow"
        Action   = ["kms:GenerateDataKey"]
        Resource = aws_kms_key.vault.arn
        Condition = {
          StringEquals = { "kms:EncryptionContext:purpose" = local.credential_purpose }
        }
      },
      {
        Sid      = "TableKeyThroughDynamoDb"
        Effect   = "Allow"
        Action   = ["kms:Decrypt", "kms:GenerateDataKey", "kms:DescribeKey"]
        Resource = var.table_kms_key_arn
        Condition = {
          StringEquals = { "kms:ViaService" = "dynamodb.${data.aws_region.current.region}.amazonaws.com" }
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

# ── The table ───────────────────────────────────────────────────
#   PK  ATTORNEY#<firm_id>#<subject>   the attorney the credential belongs to
#   SK  CREDENTIAL#<credential_id>
#
# Keyed by the attorney because every read is "this attorney's credentials":
# status lists them, revoke and open name one. No index — nothing asks the
# table a question that does not start from the attorney.
resource "aws_dynamodb_table" "credentials" {
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

  point_in_time_recovery { enabled = var.point_in_time_recovery }

  server_side_encryption {
    enabled     = true
    kms_key_arn = var.table_kms_key_arn
  }

  deletion_protection_enabled = var.deletion_protection

  tags = var.tags
}

# ── The API's seal-only grant ───────────────────────────────────
resource "aws_iam_role_policy" "api" {
  count = var.api_role_name == null ? 0 : 1

  name   = "${local.name}-seal"
  role   = var.api_role_name
  policy = local.api_policy
}
