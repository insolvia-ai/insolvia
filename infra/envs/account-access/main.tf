# ── Human account access ────────────────────────────────────────
# The account's human IAM principals: the groups people belong to, the users
# themselves, and what those users hold. Machines are not here — the pipeline's
# identity is `infra/envs/ci-trust`, and every service role is created by the
# env root that owns the service.
#
# WHY ITS OWN ROOT, AND NOT `shared`: nothing in this file can be applied by
# CI. The deploy role holds no `iam:*User*` or `iam:*Group*` action at all
# (grep ci-trust/main.tf — ServiceRoleManagement is scoped to `role/insolvia-*`
# and nothing else), so a CI apply of these resources fails with AccessDenied.
# That absence is deliberate: a pipeline that can create an IAM user with
# AdministratorAccess is a pipeline that can mint itself an admin, and the
# whole point of DenySelfPrivilegeEscalation is that merged code alone cannot
# escalate CI. Keeping human principals in a root CI cannot reach makes that
# property structural rather than a permission someone forgot to add.
#
# WHY NOT INSIDE `ci-trust`, which is already human-applied: blast radius and
# cadence. ci-trust holds the trust anchor CI authenticates through — a
# botched plan there takes every deploy offline. This root changes when a
# person joins, leaves or moves group, which is a different event on a
# different clock. Two states, two plans, two confirmations; a bad edit to the
# user map can never propose a change to the deploy role.
#
# Apply with scripts/apply-account-access.sh (guarded credential + plan +
# confirm). Rotating an MFA device is a console procedure, not an apply — see
# docs/runbooks/iam-mfa-rotation.md and § "What this root does not manage".

locals {
  # Environment stays "shared": these are account-wide principals with no
  # environment, exactly as in ci-trust. The Terraform root is named
  # "account-access"; the resources are account-level.
  common_tags = {
    Project     = "insolvia"
    Environment = "shared"
    ManagedBy   = "terraform"
  }

  # The groups a user may name in `var.human_users[*].groups`. Indexing through
  # this map is what turns a typo into a plan-time "key not found" instead of a
  # membership that silently grants nothing — `aws_iam_user_group_membership`
  # would happily send an unknown group name to IAM and fail late, or worse,
  # match a group this root does not manage.
  groups = {
    Admin = aws_iam_group.admin.name
  }

  # Flattened so each (user, policy) pair is its own resource instance with a
  # stable address. The key uses "|" rather than "/" because policy ARNs
  # already contain "/", and a composite key that can collide is a silent
  # resource swap.
  user_policy_attachments = merge([
    for username, user in var.human_users : {
      for policy_arn in user.attached_policy_arns :
      "${username}|${policy_arn}" => {
        user       = username
        policy_arn = policy_arn
      }
    }
  ]...)
}

# ── Groups ──────────────────────────────────────────────────────
# One group today. It exists as a group rather than a direct user attachment
# because the grant belongs to the role a person holds, not the person: moving
# someone in or out is then a one-line membership change with no policy edit,
# and `aws iam list-attached-group-policies` answers "who is an admin here"
# without walking every user.
resource "aws_iam_group" "admin" {
  name = "Admin"
}

resource "aws_iam_group_policy_attachment" "admin_administrator_access" {
  group      = aws_iam_group.admin.name
  policy_arn = "arn:aws:iam::aws:policy/AdministratorAccess"
}

# ── Users ───────────────────────────────────────────────────────
# `prevent_destroy` because this account currently has exactly one human user
# and no other console path in: deleting it — by a stray `terraform destroy`,
# or by editing the map with a typo that reads as a rename — locks everyone out
# of the account short of root recovery.
#
# The cost is that OFFBOARDING IS TWO STEPS ON PURPOSE: delete the
# `prevent_destroy` line, apply, then remove the user from `var.human_users`
# and apply again. A departure is a deliberate act and can afford a second
# plan; an accident cannot afford the first one.
#
# `force_destroy = false` (the default, stated) so a delete also fails while
# the user still has login profiles, keys or MFA devices attached — the removal
# order is then explicit rather than cascading.
resource "aws_iam_user" "human" {
  for_each = var.human_users

  name          = each.key
  force_destroy = false

  # `aws_iam_user` manages tags EXCLUSIVELY — a tag not listed here is removed
  # on apply, which is how a hand-set tag disappears without anyone deciding
  # it should. `extra_tags` is the escape hatch for tags that must survive;
  # see variables.tf for the one this account had and why it is not kept.
  tags = merge(local.common_tags, each.value.extra_tags)

  lifecycle {
    prevent_destroy = true
  }
}

# Exclusive: this resource manages the FULL set of groups for the user, so a
# group added by hand in the console is removed on the next apply. That is the
# behaviour we want from a root whose job is to be the source of truth — but it
# is worth knowing before wondering where a console change went.
resource "aws_iam_user_group_membership" "human" {
  for_each = var.human_users

  user   = aws_iam_user.human[each.key].name
  groups = [for group in each.value.groups : local.groups[group]]
}

# NOT exclusive, unlike group membership: `aws_iam_user_policy_attachment`
# manages only the attachments it declares, so a policy attached by hand
# survives an apply. There is no exclusive variant for users; catching drift
# here means reading `aws iam list-attached-user-policies` rather than trusting
# the plan.
resource "aws_iam_user_policy_attachment" "human" {
  for_each = local.user_policy_attachments

  user       = aws_iam_user.human[each.value.user].name
  policy_arn = each.value.policy_arn
}

# ── Adoption of the pre-Terraform account ───────────────────────
# Every resource above already existed when this root was written — the account
# was bootstrapped by hand in July 2026 — so these `import` blocks adopt them
# instead of proposing a create. Without them the first plan proposes creating
# a user and group that exist, and the apply fails on EntityAlreadyExists.
#
# They are no-ops once the resources are in state, so leaving them costs
# nothing and documents where the resources came from. THE ONE FOOTGUN: an
# import block naming a resource that is neither in state nor in the account
# is a hard plan error. So when you add a NEW person to `var.human_users`, do
# not add an import block for them — Terraform must create that user. Import
# blocks are only ever for adopting something that already exists.
import {
  to = aws_iam_group.admin
  id = "Admin"
}

import {
  to = aws_iam_group_policy_attachment.admin_administrator_access
  id = "Admin/arn:aws:iam::aws:policy/AdministratorAccess"
}

import {
  to = aws_iam_user.human["andreas.savva"]
  id = "andreas.savva"
}

import {
  to = aws_iam_user_group_membership.human["andreas.savva"]
  id = "andreas.savva/Admin"
}

import {
  to = aws_iam_user_policy_attachment.human["andreas.savva|arn:aws:iam::aws:policy/IAMUserChangePassword"]
  id = "andreas.savva/arn:aws:iam::aws:policy/IAMUserChangePassword"
}

# ── What this root does not manage, and why ─────────────────────
#
# MFA DEVICES. `aws_iam_virtual_mfa_device` exists, and it is the wrong tool
# here: the resource returns `base_32_string_seed` and `qr_code_png` as
# attributes, which means the TOTP shared secret is written to
# s3://insolvia-terraform-state in plaintext. Anyone who can read that bucket
# can then generate valid second factors for an AdministratorAccess user, which
# inverts what the second factor is for. Enrolment stays a console action by
# the person holding the device — docs/runbooks/iam-mfa-rotation.md is the
# procedure, including the two failure modes that look like a permissions
# error and are not.
#
# CONSOLE PASSWORDS. `aws_iam_user_login_profile` has the same defect: without
# a PGP key the generated password lands in state, and with one you have moved
# the problem to key custody for a single-maintainer account. The password is
# set by the person at first sign-in.
#
# ACCESS KEYS. `aws_iam_access_key` writes the secret to state — same reason.
# `andreas.savva` currently holds one long-lived key created 2026-07-26 and
# tagged `claude-code`; it stays out of Terraform, and rotating it means
# creating the new key, updating the consumer, then deleting the old one.
#
# A SELF-SERVICE "MANAGE YOUR OWN MFA" POLICY. The well-known AWS snippet
# (iam:CreateVirtualMFADevice + iam:EnableMFADevice on
# arn:aws:iam::*:mfa/${aws:username}) is deliberately absent, and this note
# exists so it is not added from a search result the next time an MFA setup
# fails. Every user here is in Admin, so AdministratorAccess already allows all
# of it — verified with `aws iam simulate-principal-policy`, which returns
# "allowed" for CreateVirtualMFADevice, EnableMFADevice, DeactivateMFADevice,
# DeleteVirtualMFADevice, ResyncMFADevice and ListMFADevices. Attaching the
# snippet would change nothing and would imply a restriction that is not there.
# It becomes load-bearing the day a NON-admin user is added: that is when to
# write it, scoped to a group those users are in.
#
# MFA ENFORCEMENT (deny-unless-aws:MultiFactorAuthPresent) is also absent, and
# that is a live gap rather than a settled decision — the account holds
# GLBA-scope data. It is left out because a badly scoped version of that policy
# locks the account's only human out, so it wants its own change with its own
# plan, not a rider on the root that introduces user management.
