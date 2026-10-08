# Build context is the REPO ROOT, not this directory — requirements.txt
# installs ../../packages/insolvia_core (ADR 0012), and the worker imports the
# API's core (the approval digest and the filing set — services/filing/README.md
# says why), neither of which a services/filing context can see. Every builder
# agrees: filing-pr.yml, filing-staging.yml and scripts/bootstrap-ecr-images.sh
# all pass the root and name this file with -f.
#
# WHAT IS NOT HERE: services/filing/fake/ — the fake CM/ECF is a dev/CI
# fixture and never enters an image (tests/unit/test_packaging.py reads the
# COPY lines below to hold that), and the API's web layer is copied as source
# but never imported (tests/unit/test_architecture.py).
FROM public.ecr.aws/lambda/python:3.12 AS lambda

# Mirror the checkout's layout, then install FROM the requirements file's
# directory: pip resolves a requirements file's relative paths against the
# current working directory, so the cd is what makes ../../packages resolve.
COPY packages/insolvia_core /build/packages/insolvia_core
COPY services/filing/requirements.txt /build/services/filing/requirements.txt
RUN cd /build/services/filing && pip install --no-cache-dir -r requirements.txt
COPY services/api/src/ ${LAMBDA_TASK_ROOT}/
COPY services/filing/src/ ${LAMBDA_TASK_ROOT}/

CMD ["insolvia_filing.entrypoints.filing_lambda.handler"]
