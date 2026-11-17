import { afterEach, describe, expect, it, vi } from "vitest";

import { isPlaceholderSite } from "./site-mode.server";

/**
 * The switch between the real marketing site and a holding page, read from the
 * Lambda's `INSOLVIA_SITE_MODE` (set by `infra/modules/marketing_site`).
 *
 * The load-bearing property is the direction it fails in: unset or unrecognised
 * must serve the FULL site. Getting that backwards would mean a typo, a dropped
 * Lambda environment, or a local `npm run dev` silently hiding the site — a
 * failure nobody would notice, as opposed to one that is immediately obvious.
 */
describe("isPlaceholderSite", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("is true for the placeholder value", () => {
    vi.stubEnv("INSOLVIA_SITE_MODE", "placeholder");

    expect(isPlaceholderSite()).toBe(true);
  });

  it.each(["PLACEHOLDER", "Placeholder", "  placeholder  "])(
    "accepts %j — casing and stray whitespace are not a config error worth an outage",
    (value) => {
      vi.stubEnv("INSOLVIA_SITE_MODE", value);

      expect(isPlaceholderSite()).toBe(true);
    },
  );

  it.each([
    ["unset", undefined],
    ["empty", ""],
    ["the explicit full value", "full"],
    ["a typo", "placehodler"],
    ["something unrelated", "true"],
  ])("fails OPEN for %s — serves the real site", (_label, value) => {
    vi.stubEnv("INSOLVIA_SITE_MODE", value as string);

    expect(isPlaceholderSite()).toBe(false);
  });
});
