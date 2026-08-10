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
