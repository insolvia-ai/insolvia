# React Router (framework mode) SSR handler on AWS Lambda.
# The site depends only on public packages (its UI is its own source under
# app/ui/), so `npm ci --omit=dev` below needs no GitHub Packages registry auth
# and no .npmrc inside the image.
FROM public.ecr.aws/lambda/nodejs:22

WORKDIR ${LAMBDA_TASK_ROOT}

COPY package.json package-lock.json ./
RUN npm ci --omit=dev && npm cache clean --force

COPY build ./build
COPY server ./server

# server/index.mjs exports `handler`.
CMD ["server/index.handler"]
