# React Router (framework mode) SSR handler on AWS Lambda.
# @insolvia/design-system is a file: path dep bundled into the server build at
# build time (see vite.config.ts ssr.noExternal), so this runtime install only
# needs public packages — no monorepo layout or registry auth in the image.
FROM public.ecr.aws/lambda/nodejs:22

WORKDIR ${LAMBDA_TASK_ROOT}

COPY package.json package-lock.json ./
RUN npm ci --omit=dev && npm cache clean --force

COPY build ./build
COPY server ./server

# server/index.mjs exports `handler`.
CMD ["server/index.handler"]
