variable "aws_region" {
  description = "Default AWS region."
  type        = string
  default     = "us-east-1"
}

variable "human_users" {
  description = <<-EOT
    The account's human IAM users, keyed by IAM user name. This map is the
    whole point of the root: adding, moving or offboarding a person is an edit
    here plus `scripts/apply-account-access.sh`, not console clicking.

    The user name is the map key because IAM user names are unique per account
    and renaming one is a destroy/create — making it the key means a rename
    shows up in the plan as exactly that, instead of hiding inside an attribute
    diff.

    `groups` are validated against the groups this root declares (see
    `local.groups` in main.tf), so a typo fails at plan time rather than
    silently granting nothing.

    NOTE ON SECRETS: passwords (`aws_iam_user_login_profile`) and access keys
    (`aws_iam_access_key`) are deliberately NOT modelled here — both write the
    credential into Terraform state, and this account's state bucket is not a
    secret store. Console passwords are set by the person at first sign-in;
    access keys are created out of band. See main.tf § "What this root does not
    manage".
  EOT

  type = map(object({
    groups               = optional(list(string), [])
    attached_policy_arns = optional(list(string), [])
    extra_tags           = optional(map(string), {})
  }))

  default = {
    "andreas.savva" = {
      groups = ["admin"]

      # Redundant while this user is in the admin group — AdministratorAccess already
      # allows iam:ChangePassword — but it is attached in the live account and
      # this root is meant to be the complete picture of what the user holds.
      # It also survives a future move out of that group, which is when it starts
      # doing work.
      attached_policy_arns = ["arn:aws:iam::aws:policy/IAMUserChangePassword"]

      # `extra_tags` is intentionally empty. The hand-built account carried one
      # tag on this user whose KEY was an access key id and whose value named
      # the consumer — a provenance note. It is not reproduced here because
      # this repo is public: an access key id is not a secret, but publishing
      # "this AdministratorAccess user holds a long-lived key, and here is its
      # id" is a disclosure with no upside, and `aws iam list-access-keys
      # --user-name andreas.savva` answers the same question from inside the
      # account. `aws_iam_user` manages tags exclusively, so the first apply
      # DELETES that tag — deliberately, not by oversight.
      extra_tags = {}
    }
  }
}
