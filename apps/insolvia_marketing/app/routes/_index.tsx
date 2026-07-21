import type { MetaFunction } from "react-router";

import { Cta } from "../components/cta";
import { Faq } from "../components/faq";
import { Hero } from "../components/hero";
import { Jobs } from "../components/jobs";
import { Pillars } from "../components/pillars";

export const meta: MetaFunction = () => [
  { title: "Insolvia — Bankruptcy case prep, native to your MyCase practice" },
  {
    name: "description",
    content:
      "AI-assisted bankruptcy case preparation for consumer-bankruptcy law firms on MyCase. " +
      "Native integration ends double data entry; AI kills the re-keying while the forms and " +
      "means test stay rule-based. Chapters 7, 11, and 13.",
  },
];

export default function Home() {
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
