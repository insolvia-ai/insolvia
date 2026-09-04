# Build context is the REPO ROOT, not this directory — requirements.txt
# installs ../../packages/insolvia_core (issue #208), which a services/api
# context cannot see. Every builder agrees: api-pr.yml, api-staging.yml,
# scripts/bootstrap-ecr-images.sh, and docker-compose.yml all pass the root
# and name this file with -f / `dockerfile:`.
FROM public.ecr.aws/lambda/python:3.12 AS lambda

# Mirror the checkout's layout, then install FROM the requirements file's
# directory: pip resolves a requirements file's relative paths against the
# current working directory, so the cd is what makes ../../packages resolve.
COPY packages/insolvia_core /build/packages/insolvia_core
COPY services/api/requirements.txt /build/services/api/requirements.txt
RUN cd /build/services/api && pip install --no-cache-dir -r requirements.txt
COPY services/api/src/ ${LAMBDA_TASK_ROOT}/

CMD ["insolvia_api.entrypoints.api_lambda.handler"]

# The pipeline worker's image (ADR 0018) — same source tree, different
# handler, its OWN image and ECR repository (insolvia-shared-jobs) rather
# than a CMD override on the API's. The split looks redundant while the two
# stages are byte-identical, and it is the point of ADR 0015's rule: when
# 9.6/9.7 bring PDF assembly and model SDKs, their weight lands HERE (as
# worker-only pip installs in this stage), and the API image never grows
# them. Built and pushed by the same workflows that build the lambda stage;
# seeded for a fresh environment by scripts/bootstrap-ecr-images.sh jobs.
FROM lambda AS worker

# The worker-only weight (ADR 0015): the Anthropic SDK behind the AI review
# (issue #97). Same cd-then-install rule as the lambda stage; the -r chain
# re-resolves requirements.txt, which pip finds already satisfied.
COPY services/api/requirements-worker.txt /build/services/api/requirements-worker.txt
RUN cd /build/services/api && pip install --no-cache-dir -r requirements-worker.txt

CMD ["insolvia_api.entrypoints.worker_lambda.handler"]

FROM python:3.12-slim AS development

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONPATH=/app/src \
    INSOLVIA_ENV=local

WORKDIR /app
COPY packages/insolvia_core /build/packages/insolvia_core
COPY services/api/requirements.txt /build/services/api/requirements.txt
# Same cd-then-install rule as the lambda stage above.
RUN cd /build/services/api && pip install --no-cache-dir -r requirements.txt
COPY services/api/src/ src/

EXPOSE 8080
CMD ["gunicorn", "--bind", "0.0.0.0:8080", "--workers", "1", "--threads", "4", "insolvia_api.entrypoints.development_server:app"]
