// The shared settings live in the repo root's `jest.base.js`; see its header
// for why it is not a discoverable `jest.config.js`.
import base from '../../jest.base.js';

export default {
  ...base,
  testMatch: ['<rootDir>/src/**/*.test.ts'],
};
