# React Router (framework mode) SSR handler on AWS Lambda.
# @insolvia-ai/design-system is a devDependency bundled into the server build
# at build time (see vite.config.ts ssr.noExternal), so `npm ci --omit=dev`
# below only installs public runtime packages — no GitHub Packages registry
# auth (and no .npmrc) is needed inside the image.
FROM public.ecr.aws/lambda/nodejs:22

WORKDIR ${LAMBDA_TASK_ROOT}

COPY package.json package-lock.json ./
RUN npm ci --omit=dev && npm cache clean --force

COPY build ./build
COPY server ./server

# server/index.mjs exports `handler`.
CMD ["server/index.handler"]
