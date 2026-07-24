import type { ReactNode } from "react";
import { Form, Link, data, useNavigation } from "react-router";
import { Button } from "@insolvia-ai/design-system";

import { SITE_NAME } from "../lib/seo";
import { submitUnsubscribe, tokenFrom } from "../lib/unsubscribe.server";
import type { Route } from "./+types/unsubscribe";

/**
 * /unsubscribe — the page an email's unsubscribe link lands on (#80 / 6.8).
 *
 * Two entry points, one action:
 *
 *   1. A person clicks "Unsubscribe" in an email footer. They land here with
 *      ?token=..., see a confirmation, and press the button. The confirmation
 *      step exists because link scanners in corporate mail gateways follow
 *      every URL in an incoming message — without it, a scanner would
 *      unsubscribe people who never clicked anything. That is also why the
 *      loader does nothing but read: a GET here must stay safe to fetch.
 *   2. A mail client honours RFC 8058 and POSTs `List-Unsubscribe=One-Click`
 *      straight to this URL. No form, no confirmation, no JavaScript — the
 *      action below handles it and renders the same success state. That POST
 *      carries no token in its body, only in the query string, which is why
 *      `tokenFrom` reads both places.
 *
 * The page never displays the address being unsubscribed. It could not anyway
 * (the token is opaque to this layer — only the API holds the key that reads
 * it) and it should not: the URL may be sitting in a shared or forwarded
 * inbox.
 */

export const meta: Route.MetaFunction = () => [
  { title: `Unsubscribe · ${SITE_NAME}` },
  // Not in SEO_ROUTES, and explicitly noindexed. A per-recipient URL carrying
  // a signing token has no business in a search index — root.tsx's site-wide
  // noindex only covers non-production hosts, so production needs this here.
  { name: "robots", content: "noindex, nofollow" },
];

export async function loader({ request }: Route.LoaderArgs) {
  // Read-only. Whether a token is present decides which state renders;
  // nothing is suppressed until the action runs.
  const token = new URL(request.url).searchParams.get("token")?.trim() ?? "";
  return { token };
}

export async function action({ request }: Route.ActionArgs) {
  const form = await request.formData();
  const token = tokenFrom(form, new URL(request.url));

  if (!token) {
    return data(
      { ok: false as const, reason: "invalid-token" as const },
      { status: 400 },
    );
  }

  const result = await submitUnsubscribe(token);
  if (result.ok) {
    return data({ ok: true as const, reason: null });
  }

  return data(
    { ok: false as const, reason: result.reason },
    { status: result.reason === "invalid-token" ? 400 : 503 },
  );
}

function Panel({ children }: { children: ReactNode }) {
  return (
    <section className="mx-auto w-full max-w-2xl px-lg py-xxl">
      <div className="flex flex-col items-start gap-md rounded-lg border border-line bg-card p-xl">
        {children}
      </div>
    </section>
  );
}

function BackHome() {
  return (
    <Button intent="secondary" size="md" nativeButton={false} render={<Link to="/" />}>
      Back home
    </Button>
  );
}

function Contact() {
  return (
    <a href="mailto:hello@insolvia.ai" className="text-accent underline">
      hello@insolvia.ai
    </a>
  );
}

export default function Unsubscribe({ loaderData, actionData }: Route.ComponentProps) {
  const navigation = useNavigation();
  const submitting = navigation.state !== "idle";
  const reason = actionData?.ok === false ? actionData.reason : null;

  if (actionData?.ok === true) {
    return (
      <Panel>
        <h1 className="font-heading text-3xl font-semibold text-ink">
          You&rsquo;ve been unsubscribed
        </h1>
        <p className="text-base text-muted">
          We won&rsquo;t send any more email to that address. Anything already on its way
          may take a few minutes to stop.
        </p>
        <p className="text-base text-muted">
          If you have an Insolvia account, messages strictly necessary to operate it — a
          password reset you asked for, for instance — may still need to reach you.{" "}
          <Link to="/privacy" className="text-accent underline">
            Our privacy policy
          </Link>{" "}
          explains what happens then.
        </p>
        <BackHome />
      </Panel>
    );
  }

  // No token at all, or a token the API rejected: the same dead end, and
  // deliberately the same message. Distinguishing them would tell a visitor
  // something about a token they may not own.
  if (!loaderData.token || reason === "invalid-token") {
    return (
      <Panel>
        <h1 className="font-heading text-3xl font-semibold text-ink">
          This unsubscribe link isn&rsquo;t valid
        </h1>
        <p className="text-base text-muted">
          It may have been truncated by your email client — long links sometimes break
          across lines. Try it again from the original email, or write to <Contact /> and
          we&rsquo;ll take care of it by hand.
        </p>
        <BackHome />
      </Panel>
    );
  }

  return (
    <Panel>
      <h1 className="font-heading text-3xl font-semibold text-ink">
        Unsubscribe from Insolvia email
      </h1>
      <p className="text-base text-muted">
        Confirm below and we&rsquo;ll stop sending email to the address this link was sent
        to. You don&rsquo;t need an account, and you don&rsquo;t need to tell us why.
      </p>

      {/* A plain document POST — no JavaScript required, which is the state a
          fair number of email clients open links in. The token rides in a
          hidden field rather than relying on the form inheriting the query
          string; the action also accepts it from the query string, which is
          what makes a mail client's own one-click POST work with no form. */}
      <Form method="post" className="flex flex-col gap-md">
        <input type="hidden" name="token" value={loaderData.token} />
        <Button type="submit" intent="primary" size="lg" disabled={submitting}>
          {submitting ? "Unsubscribing…" : "Unsubscribe"}
        </Button>
      </Form>

      {reason === "unavailable" ? (
        <p className="text-sm text-danger">
          Something went wrong on our end and nothing was changed. Please try again, or
          write to <Contact /> and we&rsquo;ll do it by hand.
        </p>
      ) : null}
    </Panel>
  );
}
