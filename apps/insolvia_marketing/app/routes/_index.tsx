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

export const meta: MetaFunction = () =>
  seo({
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
