// `process.env` save/restore for tests — what Vitest's `vi.stubEnv` /
// `vi.unstubAllEnvs` did, which Jest has no equivalent of. Call `restoreEnv()`
// in an `afterEach` of every file that calls `stubEnv`.
const saved = new Map<string, string | undefined>();

/** Sets `name` for this test, remembering its value from before the first stub. */
export function stubEnv(name: string, value: string): void {
  if (!saved.has(name)) saved.set(name, process.env[name]);
  process.env[name] = value;
}

/** Puts every stubbed variable back, deleting the ones that were unset. */
export function restoreEnv(): void {
  for (const [name, value] of saved) {
    if (value === undefined) delete process.env[name];
    else process.env[name] = value;
  }
  saved.clear();
}
