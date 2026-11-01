// The shared settings live in the repo root's `jest.base.js`; see its header
// for why it is not a discoverable `jest.config.js`.
import base from '../jest.base.js';

export default {
  ...base,
  testMatch: ['<rootDir>/src/**/*.test.ts'],
  // The client-coverage gate (src/coverage.ts).
  globalSetup: '<rootDir>/src/coverage.setup.ts',
  globalTeardown: '<rootDir>/src/coverage.teardown.ts',
  // One stack, one seeded firm: files run one after another so firm-wide
  // writes (questionnaire, checklist, users) never interleave.
  maxWorkers: 1,
  // Jest has one timeout, for tests and hooks alike.
  testTimeout: 30_000,
};
