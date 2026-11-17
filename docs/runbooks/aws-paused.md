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
| Cognito pool `insolvia-dev-users`, `dev-auth.insolvia.ai`, and `/insolvia/dev/auth/*` in SSM | Local sign-in. There is no local stand-in yet |
| IAM user and admin group | Account access |
| `insolvia-shared-terraform-state-us-east-1` | Terraform state for what remains |

Everything else is gone, including the per-machine dev stacks, the container
repositories, and the GitHub deploy role. The `infra/` code is unchanged, so
the environments can be rebuilt from it. The KMS keys finish deleting on
2026-11-12 (staging, dev) and 2026-12-05 (prod). `DevOpsAgentSpace`, created by
Support Center, also remains; it has cost nothing.

## Local development

The backend is Python again (main restored to its state before ADR 0028). That
backend has **no local stand-in for DynamoDB**: `scripts/dev-up.sh` talks to a
per-developer stack that `scripts/dev-aws-setup.sh` applies into
`infra/envs/dev`. While AWS is paused that stack does not exist, so the API
cannot run locally until someone decides to apply `infra/envs/dev` (it costs
money) or adds a local DynamoDB. Unit tests that use moto still run offline.

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
