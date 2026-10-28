// Rules live in the repo root's `eslint.base.js`. That file is not named
// `eslint.config.js` on purpose — see its header for what breaks when a
// discoverable flat config sits at the repo root.
//
// One addition: contract/.venv is the Python contract stack's environment
// (scripts/dev-test.sh builds it), and site-packages ships JavaScript.
import base from '../eslint.base.js';

export default [{ ignores: ['.venv/**'] }, ...base];
