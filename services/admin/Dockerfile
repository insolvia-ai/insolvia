# Build context is the REPO ROOT, not this directory — requirements.txt
# installs ../../packages/insolvia_core (issue #208), which a services/admin
# context cannot see. Every builder agrees: admin-pr.yml, admin-staging.yml,
# scripts/bootstrap-ecr-images.sh, and docker-compose.yml all pass the root
# and name this file with -f / `dockerfile:`. Same shape as services/api's.
FROM public.ecr.aws/lambda/python:3.12 AS lambda

COPY packages/insolvia_core /build/packages/insolvia_core
COPY services/admin/requirements.txt /build/services/admin/requirements.txt
# cd-then-install: pip resolves a requirements file's relative paths against
# the current working directory, not the file's location.
RUN cd /build/services/admin && pip install --no-cache-dir -r requirements.txt
COPY services/admin/src/ ${LAMBDA_TASK_ROOT}/

CMD ["insolvia_admin.entrypoints.api_lambda.handler"]

FROM python:3.12-slim AS development

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONPATH=/app/src \
    INSOLVIA_ENV=local

WORKDIR /app
COPY packages/insolvia_core /build/packages/insolvia_core
COPY services/admin/requirements.txt /build/services/admin/requirements.txt
RUN cd /build/services/admin && pip install --no-cache-dir -r requirements.txt
COPY services/admin/src/ src/

# 8090: the tenant API's dev server owns 8080.
EXPOSE 8090
CMD ["gunicorn", "--bind", "0.0.0.0:8090", "--workers", "1", "--threads", "4", "insolvia_admin.entrypoints.development_server:app"]
