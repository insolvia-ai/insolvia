import type { Config } from "@react-router/dev/config";

export default {
  // Server-side render by default — SEO/OG + fast first paint.
  ssr: true,

  // Single-fetch actions are CSRF-guarded: RR aborts with "Bad Request" when the
  // browser Origin host differs from the host of `request.url`. Behind CloudFront
  // → API Gateway the Lambda sees the API Gateway domain as its host (CloudFront
  // strips the viewer Host), while the browser Origin is the public site. Allow
  // the public hosts so POST actions (e.g. the waitlist form) aren't rejected.
  allowedActionOrigins: ["www.insolvia.ai", "insolvia.ai"],
} satisfies Config;
