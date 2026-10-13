# RETIRING. This root branded the shared development pool's two app clients
# from 2026-10-12 until the same day's amendment of ADR 0026: the sign-in
# page's style is auth's, so it moved to insolvia-services with the pool. Its
# infra/envs/dev ADOPTS the live styles with `import` blocks.
#
# What is left is the hand-over: this block lets go of the styles in this
# root's one shared state WITHOUT deleting them — a delete would leave every
# developer's sign-in page saying "Login pages unavailable" until the other
# side re-created them. scripts/dev-aws-setup.sh applies this root on every
# run, so the first machine to run it after this merges performs the hand-over
# for all of them; every run after is a no-op.
#
# Delete this whole root (and its call in scripts/dev-aws-common.sh) once its
# state holds nothing — `terraform -chdir=infra/envs/dev-shared state list`
# printing nothing — then remove the state object
# (s3://insolvia-shared-terraform-state-us-east-1/insolvia/dev-shared/terraform.tfstate).
removed {
  from = module.auth

  lifecycle {
    destroy = false
  }
}
