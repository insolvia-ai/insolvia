// Jest's setupFilesAfterEnv: runs inside every test file, so this `afterAll`
// is each FILE's teardown — every scratch case it opened is deleted and every
// scratch client archived however its specs ended (support/actors.ts). The
// same on both targets, so the undo the deployed target depends on runs on
// every PR too.
import { afterAll } from '@jest/globals';

import { cleanUpScratch } from './support/actors.ts';

afterAll(cleanUpScratch);
