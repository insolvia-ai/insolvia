import { describe, expect, it } from "vitest";

import { resolveConfig } from "./environment";

describe("resolveConfig", () => {
  it("defaults to local when unset — the dev server's state", () => {
    expect(resolveConfig(undefined).environment).toBe("local");
    expect(resolveConfig("").environment).toBe("local");
  });

  it("resolves each environment to its own client and host", () => {
    const staging = resolveConfig("staging");
    const production = resolveConfig("production");
    expect(staging.googleClientId).not.toBe(production.googleClientId);
    expect(staging.apiBaseUrl).toBe("https://staging-admin-api.insolvia.ai");
    expect(production.apiBaseUrl).toBe("https://admin-api.insolvia.ai");
  });

  it("refuses an unknown environment rather than quietly building local", () => {
    expect(() => resolveConfig("prod")).toThrow(/VITE_INSOLVIA_ENV/);
  });
});
