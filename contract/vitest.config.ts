import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['src/**/*.test.ts'],
    globalSetup: ['src/coverage.setup.ts'],
    // One stack, one seeded firm: files run one after another so firm-wide
    // writes (questionnaire, checklist, users) never interleave.
    fileParallelism: false,
    sequence: { concurrent: false },
    testTimeout: 30_000,
    hookTimeout: 30_000,
  },
});
