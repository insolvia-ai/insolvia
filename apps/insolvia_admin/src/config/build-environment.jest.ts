// build-environment.ts under Jest (jest.config.js maps it here): the same
// variable, read from the process environment as Vitest's `import.meta.env`
// did. Unset in a test run, so the tests build `local`.
export const buildEnvironment: string | undefined = process.env.VITE_INSOLVIA_ENV;
