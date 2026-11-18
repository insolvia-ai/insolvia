# AWS is paused

On 2026-11-05 every Insolvia environment in AWS was torn down to bring the bill
to near zero until there is a full MVP.
**There is no staging or production
right now.**

## What still exists in account 521762924626

| Kept | Why |
| --- | --- |
| Route53 zone `insolvia.ai` and the domain registration | Domain routing. The zone holds the Google Workspace mail records (MX, SPF, DKIM, verification) |
| Wildcard ACM certificate | Free, and `dev-auth.insolvia.ai` needs it |
| SES identity `insolvia.ai` and its DNS | Free, and keeps the sending domain verified |
| Cognito pool `insolvia-dev-users`, `dev-auth.insolvia.ai`, and `/insolvia/dev/auth/*` in SSM | Local sign-in. There is no local stand-in for Cognito |
| IAM user and admin group | Account access |
| `insolvia-shared-terraform-state-us-east-1` | Terraform state for what remains |

Everything else is gone, including the per-machine dev stacks, the container
repositories, and the GitHub deploy role. The `infra/` code is unchanged, so
the environments can be rebuilt from it. The KMS keys finish deleting on
2026-11-12 (staging, dev) and 2026-12-05 (prod). `DevOpsAgentSpace`, created by
Support Center, also remains; it has cost nothing.

## Local development

The whole system runs on a laptop with **no AWS session** (#624, ADR 0030
row 5): `scripts/dev-up.sh` starts Docker Postgres, a local S3 server and the
API, which runs jobs in process and seals under a development-only key. The
one AWS service is the shared dev Cognito pool. `scripts/dev-aws-seed.sh`
reaches it once per user — that run needs an AWS login — to write the pool's
ids into the local `.env` files and create your account; after that, seeding
and running need no AWS. [`scripts/README.md`](../../scripts/README.md) has
the detail.

The per-developer stack (`infra/envs/dev`, `scripts/dev-aws-setup.sh`) is
optional and stays unapplied while AWS is paused.

## Do not run while paused

The deploy workflows are disabled in GitHub (`gh workflow list --all`), because
`release.yml` deploys on every push to `main`. Do not re-enable them, and do not
`terraform apply` `shared`: it would recreate the container repositories and the
fixture bucket.

## Bringing AWS back

Follow `insolvia-services`' `docs/runbooks/aws-bootstrap.md`, then re-enable the
workflows:

1. Apply `ci-trust` by hand, because CI cannot apply it.
2. Apply `shared`, then push images with `scripts/bootstrap-ecr-images.sh`.
3. Apply `insolvia-services` staging, then `insolvia` staging. Repeat for prod.
4. Publish the seed fixtures with `scripts/dev-fixture.sh`. The bytes are
   committed in `seeds/fixtures/`.

The staging and prod roots now also create a VPC (`modules/network`) and an
Aurora cluster with its migrator Lambda (`modules/database`, ADR 0030). They
were written while paused and have never been applied. Before their first
apply, the deploy role needs the EC2, RDS and Secrets Manager actions listed
in #625's PR, which is a PR to insolvia-services and a human `ci-trust` apply.
The services also need IAM-token connect, and the migrator handler
(`insolvia_core.adapters.postgres.migrations.handler`) has to exist.
