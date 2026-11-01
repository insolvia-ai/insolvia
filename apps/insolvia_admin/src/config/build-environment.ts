// The ONE value the build injects (`VITE_INSOLVIA_ENV`; environment.ts says
// why it is one). It is read here, alone, because `import.meta` does not
// exist in the CommonJS Jest runs the tests as (jest.config.js), so Jest maps
// this module to build-environment.jest.ts instead.
export const buildEnvironment = import.meta.env.VITE_INSOLVIA_ENV as string | undefined;
