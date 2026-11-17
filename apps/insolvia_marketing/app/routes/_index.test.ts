import { describe, expect, it } from "vitest";

import { meta } from "./_index";

/**
 * Placeholder mode has to take the META with it, not just the body.
 *
 * This is the failure that already happened in production: the holding page
 * rendered correctly while `<title>` and the OpenGraph tags still carried the
 * full positioning. `noindex` hides a page from search engines and does nothing
 * about the browser tab or an unfurled link — Slack, iMessage and LinkedIn read
 * og:title/og:description straight off the response, so the page was still
 * pasting the product pitch into any chat it was shared in.
 */

// `meta` reads only `data`; the rest of MetaArgs (params, location, matches) is
// irrelevant here and constructing it would couple this test to the generated
// type rather than to the behaviour.
function metaFor(placeholder: boolean) {
  const descriptors = (meta as (args: unknown) => Array<Record<string, unknown>>)({
    data: { placeholder },
  });
  return JSON.stringify(descriptors);
}

describe("home page meta", () => {
  it("says nothing about the product in placeholder mode", () => {
    const rendered = metaFor(true);

    // The positioning, in the words that must not leak: the competitor, the
    // integration, and the audience.
    for (const leak of ["MyCase", "Best Case", "bankruptcy", "Chapters"]) {
      expect(rendered).not.toContain(leak);
    }
  });

  it("still sets a title and description in placeholder mode", () => {
    // Empty meta is not the goal — an untitled tab looks broken. Neutral is.
    const rendered = metaFor(true);

    expect(rendered).toContain("Insolvia");
    expect(rendered).toContain("Something is being built here.");
  });

  it("covers the social tags, not just the title", () => {
    // og:title is the one that unfurls in a chat client, and it is a separate
    // descriptor from <title> — fixing only the latter would look fixed while
    // still leaking.
    const rendered = metaFor(true);

    expect(rendered).toContain("og:title");
    expect(rendered).not.toContain("native to your MyCase practice");
  });

  it("serves the real positioning when placeholder mode is off", () => {
    // The regression that matters in the other direction: launching must not
    // silently keep the neutral meta.
    const rendered = metaFor(false);

    expect(rendered).toContain("MyCase");
    expect(rendered).toContain("Chapters 7, 11, and 13");
  });
});
