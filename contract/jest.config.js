// The shared settings live in the repo root's `jest.base.js`; see its header
// for why it is not a discoverable `jest.config.js`.
import base from '../jest.base.js';

export default {
  ...base,
  testMatch: ['<rootDir>/src/**/*.test.ts'],
  // The client-coverage gate (src/coverage.ts), and the deployed target's
  // refusal and sign-in (src/support/target.ts).
  globalSetup: '<rootDir>/src/global.setup.ts',
  globalTeardown: '<rootDir>/src/global.teardown.ts',
  // Each file's scratch records, undone when it ends (src/support/actors.ts).
  setupFilesAfterEnv: ['<rootDir>/src/scratch.setup.ts'],
  // One stack, one seeded firm: files run one after another so firm-wide
  // writes (questionnaire, checklist, users) never interleave.
  maxWorkers: 1,
  // Every spec by name, so a spec a target skips is listed with its reason.
  verbose: true,
  // Jest has one timeout, for tests and hooks alike. The deployed target
  // crosses the internet and real sign-ins; 30 s is the offline budget.
  testTimeout: process.env.CONTRACT_TARGET === 'deployed' ? 120_000 : 30_000,
};
