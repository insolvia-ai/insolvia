# THE ONE THING EVERY DEV STACK SHARES, on this repo's side: the managed-login
# branding of the shared development pool's two app clients.
#
# The pool itself is insolvia-services' (its infra/envs/dev, ADR 0026) — one
# pool every developer machine signs in against, so that a federated identity
# provider (the day there is one) registers one redirect URI, and so that this
# repo's local setup never has to apply another repository. Its BRANDING is
# this repo's, for the reason it is this repo's on staging and prod: it is
# generated from this repo's brand by `npm run tokens`.
#
# Branding is per app client and a client can carry only one style, so it
# needs exactly ONE owner. A developer machine's infra/envs/dev cannot be it —
# every machine would try to create the same style. This root is: one state
# key, applied by scripts/dev-aws-setup.sh on every machine, every run, before
# the per-machine stack. Idempotent, so whoever applies last is irrelevant; the
# S3 lockfile serialises two machines applying at once. (humbugg's
# infra/envs/dev-shared, which holds its whole shared pool, is applied the
# same way.)
#
# Until it has applied once, the shared pool's sign-in page says "Login pages
# unavailable" — a client on managed login with no style does not fall back to
# defaults.

module "auth" {
  source = "../../modules/auth"

  project     = "insolvia"
  environment = "dev"

  # No role grants: development has no Lambda; the local API runs under each
  # developer's own IAM user.
}
