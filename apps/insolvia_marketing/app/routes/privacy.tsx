import type { ReactNode } from "react";
import { Link } from "react-router";

import { seo } from "../lib/seo";
import type { Route } from "./+types/privacy";

/**
 * /privacy — the public privacy policy (issue #80 / 6.8).
 *
 * This URL is a hard dependency of two things, so it must not move or 404:
 *
 *   1. Every transactional email's footer links it — see
 *      services/api/src/insolvia_api/core/mail.py `_PRIVACY_URL`, which is
 *      the literal string https://www.insolvia.ai/privacy. Changing this
 *      route's path means changing that constant in the same commit.
 *   2. AWS reviews it as part of the SES production-access request
 *      (docs/SES_PRODUCTION_ACCESS.md). A 404 here is a rejected request.
 *
 * The copy describes what Insolvia actually does *today* — a marketing site
 * that captures a waitlist, and transactional email — and says so explicitly,
 * because the product itself is not generally available yet. When the
 * application starts handling debtor data under GLBA (see
 * docs/regulatory-source-register.html), this policy is rewritten rather than
 * quietly stretched to cover it.
 */

/** Shown on the page and used as the policy's version marker. Bump it in the
 *  same commit as any substantive copy change — a policy whose date lies is
 *  worse than one with no date. */
const LAST_UPDATED = "24 July 2026";

const CONTACT_EMAIL = "hello@insolvia.ai";
const SECURITY_EMAIL = "security@insolvia.ai";

export const meta: Route.MetaFunction = () =>
  seo({
    title: "Privacy policy",
    description:
      "How Insolvia collects, uses, and protects personal information on www.insolvia.ai " +
      "and in the transactional email we send — what we collect, why, who processes it, " +
      "how long we keep it, and how to get it deleted.",
    path: "/privacy",
  });

function Section({
  id,
  heading,
  children,
}: {
  id: string;
  heading: string;
  children: ReactNode;
}) {
  return (
    <section id={id} className="flex scroll-mt-xl flex-col gap-md">
      <h2 className="font-heading text-2xl font-semibold text-ink">{heading}</h2>
      <div className="flex max-w-2xl flex-col gap-md text-base text-muted">{children}</div>
    </section>
  );
}

function Mail({ address }: { address: string }) {
  return (
    <a href={`mailto:${address}`} className="text-accent underline">
      {address}
    </a>
  );
}

export default function Privacy() {
  return (
    <article className="mx-auto w-full max-w-3xl px-lg py-xxl">
      <header className="flex flex-col gap-sm">
        <p className="text-sm font-medium uppercase tracking-wide text-accent">Legal</p>
        <h1 className="font-heading text-3xl font-semibold text-ink sm:text-4xl">
          Privacy policy
        </h1>
        <p className="text-sm text-muted">Last updated: {LAST_UPDATED}</p>
      </header>

      <div className="mt-xl flex flex-col gap-xl">
        <p className="max-w-2xl text-base text-muted">
          Insolvia builds bankruptcy case-preparation software for consumer-bankruptcy law
          firms. This policy explains what personal information we collect through{" "}
          <strong className="text-ink">www.insolvia.ai</strong> and the email we send from{" "}
          <strong className="text-ink">insolvia.ai</strong>, why we collect it, and what you
          can ask us to do with it.
        </p>

        <Section id="scope" heading="What this policy covers">
          <p>
            The Insolvia application is not yet generally available. Today this policy covers
            two things and nothing else: this marketing website, including its early-access
            waitlist form, and the transactional email we send to people who have given us
            their address.
          </p>
          <p>
            It does <strong className="text-ink">not</strong> describe how the Insolvia
            application will handle debtor and case data — Social Security numbers, financial
            records, and the rest — because the application is not in service. We will publish
            a separate, fuller policy covering that before any firm's client data reaches us.
          </p>
        </Section>

        <Section id="collect" heading="What we collect">
          <p>
            <strong className="text-ink">Information you give us.</strong> If you join the
            early-access waitlist we collect your name, your firm's name, your work email
            address, optionally which bankruptcy software you use today, and anything you
            choose to write in the free-text message field. The form asks you not to include
            client, debtor, or case information, and we do not want it there.
          </p>
          <p>
            <strong className="text-ink">Information we collect automatically.</strong> Our
            servers record ordinary request metadata — the host, path, HTTP method, response
            status, and how long a request took. We do not use advertising cookies, analytics
            cookies, tracking pixels, or third-party trackers, and the site sets no cookies of
            its own.
          </p>
          <p>
            <strong className="text-ink">Email delivery events.</strong> When we send you
            email, our email provider reports back whether it was delivered, bounced, or
            marked as spam. We record those outcomes so we can stop sending to addresses that
            do not want or cannot receive our mail.
          </p>
        </Section>

        <Section id="use" heading="How we use it">
          <p>
            Waitlist details are used to contact you about Insolvia's development and your
            early-access invitation, and to understand what kind of firms are interested.
            Request metadata is used to operate, secure, and debug the site. Delivery events
            are used to keep our sending reputation healthy and to honour opt-outs.
          </p>
          <p>
            We do not sell personal information, we do not share it with advertisers, and we
            do not use it to build profiles for anyone else.
          </p>
        </Section>

        <Section id="email" heading="Email we send, and how to stop it">
          <p>
            Mail from Insolvia is <strong className="text-ink">transactional</strong> — it is
            about your account or a request you made, such as confirming your email address or
            resetting a password — plus occasional updates to people who joined the waitlist.
            We do not run advertising campaigns or sell our list.
          </p>
          <p>
            You can stop receiving email from us at any time. Email{" "}
            <Mail address={CONTACT_EMAIL} /> and we will suppress your address. We also
            suppress addresses automatically when a message permanently bounces or is reported
            as spam, so a complaint is itself an effective opt-out. Suppression is kept as a
            one-way hash of the address rather than the address itself.
          </p>
          <p>
            Some messages are genuinely necessary to operate an account you hold — a password
            reset you asked for, for instance. If you have an account and opt out of email
            entirely, we may need to close the account, and we will tell you before we do.
          </p>
        </Section>

        <Section id="processors" heading="Who processes it for us">
          <p>
            We keep the list of third parties short and name it plainly rather than hiding
            behind &ldquo;service providers&rdquo;:
          </p>
          <ul className="flex list-disc flex-col gap-sm pl-lg">
            <li>
              <strong className="text-ink">Amazon Web Services</strong> — hosting, storage, and
              outbound email delivery (Amazon SES), in the United States.
            </li>
            <li>
              <strong className="text-ink">Google Workspace</strong> — our own mailboxes, so
              anything you email us is stored there.
            </li>
          </ul>
          <p>
            Each acts as a processor on our instructions. We do not send your information to
            anyone else except where the law requires it.
          </p>
        </Section>

        <Section id="retention" heading="How long we keep it">
          <p>
            Waitlist entries are kept until Insolvia launches and you have either taken up or
            declined an invitation, or until you ask us to delete them — whichever comes first.
            Email delivery records are kept for 90 days. Suppression entries are kept
            indefinitely on purpose: forgetting that you opted out is how you get emailed
            again by mistake. Request logs are kept for a short operational period and then
            expire automatically.
          </p>
        </Section>

        <Section id="security" heading="How we protect it">
          <p>
            Everything is encrypted in transit (TLS) and at rest. Access is least-privilege and
            granted to systems rather than to standing credentials; no client application — the
            website included — holds direct access to our data stores. If you believe you have
            found a vulnerability, please write to <Mail address={SECURITY_EMAIL} />; we would
            much rather hear from you than not.
          </p>
        </Section>

        <Section id="rights" heading="Your choices">
          <p>
            You can ask us for a copy of the personal information we hold about you, ask us to
            correct it, or ask us to delete it. Email <Mail address={CONTACT_EMAIL} /> and we
            will act on it — we do not require you to hold an account, use a specific form, or
            prove a legal basis for asking. Depending on where you live you may have additional
            statutory rights; we apply the above to everyone rather than checking your
            jurisdiction first.
          </p>
        </Section>

        <Section id="children" heading="Children">
          <p>
            Insolvia is professional software sold to law firms. It is not directed at
            children, and we do not knowingly collect personal information from anyone under
            16.
          </p>
        </Section>

        <Section id="changes" heading="Changes to this policy">
          <p>
            When this policy changes we update the date at the top of the page. If a change
            materially affects how we handle information you have already given us, we will
            tell the people affected by email rather than relying on you to re-read this page.
          </p>
        </Section>

        <Section id="contact" heading="Contact">
          <p>
            Questions, requests, or complaints: <Mail address={CONTACT_EMAIL} />. Insolvia is
            case-preparation software, not a law firm, and nothing here is legal advice.
          </p>
        </Section>

        <p className="text-base text-muted">
          <Link to="/" className="text-accent underline">
            Back home
          </Link>
        </p>
      </div>
    </article>
  );
}
