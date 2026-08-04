import type { MetaFunction } from "react-router";

import type { Route } from "./+types/_index";

import { Cta } from "../components/cta";
import { Faq } from "../components/faq";
import { Hero } from "../components/hero";
import { Jobs } from "../components/jobs";
import { Pillars } from "../components/pillars";
import { Holding } from "../components/holding";
import { seo } from "../lib/seo";
import { isPlaceholderSite } from "../lib/site-mode.server";

// The meta has to follow the body into placeholder mode, and forgetting that is
// an easy and expensive mistake: noindex keeps the page out of search results,
// but it does NOTHING about the browser tab or an unfurled link. Slack, iMessage
// and LinkedIn all read og:title/og:description directly, so a holding page
// with the real meta still pastes the full positioning into any chat it is
// shared in — which is exactly what placeholder mode exists to prevent.
export const meta: MetaFunction<typeof loader> = ({ data }) =>
  data?.placeholder
    ? seo({
        title: "Insolvia",
        description: "Something is being built here.",
        path: "/",
      })
    : seo({
        title: "Insolvia — Bankruptcy case prep, native to your MyCase practice",
        description:
          "AI-assisted bankruptcy case preparation for consumer-bankruptcy law firms on MyCase. " +
          "Native integration ends double data entry; AI kills the re-keying while the forms and " +
          "means test stay rule-based. Chapters 7, 11, and 13.",
        path: "/",
      });

export function loader() {
  return { placeholder: isPlaceholderSite() };
}

export default function Home({ loaderData }: Route.ComponentProps) {
  // Placeholder mode: occupy the domain, say nothing about the product. See
  // lib/site-mode.server.ts for why an environment might need to resolve
  // before its positioning is public.
  if (loaderData.placeholder) return <Holding />;

  return (
    <>
      <Hero />
      <Pillars />
      <Jobs />
      <Faq />
      <Cta />
    </>
  );
}
