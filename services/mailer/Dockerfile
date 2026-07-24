FROM public.ecr.aws/lambda/python:3.12 AS lambda

COPY requirements.txt ${LAMBDA_TASK_ROOT}/requirements.txt
RUN pip install --no-cache-dir -r ${LAMBDA_TASK_ROOT}/requirements.txt
COPY src/ ${LAMBDA_TASK_ROOT}/

CMD ["insolvia_mailer.entrypoints.api_lambda.handler"]

FROM python:3.12-slim AS development

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONPATH=/app/src \
    MAILER_RUNTIME=development

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY src/ src/

EXPOSE 8026
CMD ["gunicorn", "--bind", "0.0.0.0:8026", "--workers", "1", "--threads", "4", "insolvia_mailer.entrypoints.development_server:app"]
