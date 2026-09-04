# Build context is the REPO ROOT, not this directory — requirements.txt
# installs ../../packages/insolvia_core (ADR 0012/0016), which a services/mcp
# context cannot see. Every builder agrees: mcp-pr.yml, mcp-staging.yml, and
# scripts/bootstrap-ecr-images.sh all pass the root and name this file with -f.
FROM public.ecr.aws/lambda/python:3.12 AS lambda

# Mirror the checkout's layout, then install FROM the requirements file's
# directory: pip resolves a requirements file's relative paths against the
# current working directory, so the cd is what makes ../../packages resolve.
COPY packages/insolvia_core /build/packages/insolvia_core
COPY services/mcp/requirements.txt /build/services/mcp/requirements.txt
RUN cd /build/services/mcp && pip install --no-cache-dir -r requirements.txt
COPY services/mcp/src/ ${LAMBDA_TASK_ROOT}/

CMD ["insolvia_mcp.entrypoints.mcp_lambda.handler"]

FROM python:3.12-slim AS development

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONPATH=/app/src \
    INSOLVIA_ENV=local

WORKDIR /app
COPY packages/insolvia_core /build/packages/insolvia_core
COPY services/mcp/requirements.txt /build/services/mcp/requirements.txt
# Same cd-then-install rule as the lambda stage above. uvicorn arrives as the
# MCP SDK's own dependency — the streamable-HTTP app is ASGI, so uvicorn
# plays the role gunicorn plays for the WSGI API.
RUN cd /build/services/mcp && pip install --no-cache-dir -r requirements.txt
COPY services/mcp/src/ src/

EXPOSE 8788
CMD ["python", "-m", "uvicorn", "--host", "0.0.0.0", "--port", "8788", "insolvia_mcp.entrypoints.development_server:app"]
