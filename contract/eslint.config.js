// Rules live in the repo root's `eslint.base.js`. That file is not named
// `eslint.config.js` on purpose — see its header for what breaks when a
// discoverable flat config sits at the repo root.
//
// One addition: contract/.venv is where the retired Python contract stack
// (ADR 0028, plan 18.23) built its environment, and a checkout that ran it
// still has one; its site-packages ships JavaScript.
import base from '../eslint.base.js';

export default [{ ignores: ['.venv/**'] }, ...base];
