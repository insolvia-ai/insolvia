# ── Firm store (firms, firm users, and what they may do) ────────
# The tenancy layer. A case belongs to a FIRM, not to the person who opened it,
# and this table is what makes that sentence mean something: which firm a
# signed-in user belongs to, what their role is, and which features they may
# reach.
#
# WHY A TABLE OF ITS OWN rather than more partitions in insolvia-<env>-cases.
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
#   - A table called insolvia-<env>-cases holding firms would be a naming lie,
#     and infra/CLAUDE.md's `insolvia-<thing>-<env>` convention is the thing
#     that keeps a reader able to guess what is where.
#
# It takes the CASE key rather than minting one, exactly as modules/case_documents
# does. Firm membership is not case data, but it is the thing that decides who
# reads case data, and a separate key would be a second thing to get the deny
# right on. ci-trust's DenyCaseDataDecryption covers alias/insolvia-*-cases, so
# reusing that key means CI provisions this table and can never read a firm's
# staff list.

locals {
  # insolvia-<env>-firms
  name = "${var.project}-${var.environment}-firms"
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

  # Off everywhere since ADR 0028: the firm directory is Aurora's, and this
  # table is deleted in plan 18.23 — DynamoDB refuses to delete a protected
  # table, so protection comes off one apply before the resource goes.
  deletion_protection_enabled = var.deletion_protection

  tags = var.tags
}

# No application principal holds a grant on this table any more (ADR 0028):
# the API, admin and MCP hosts resolve firms from Aurora. The table is kept
# only until plan 18.23 deletes it.
