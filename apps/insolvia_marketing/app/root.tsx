import type { ReactNode } from "react";
import {
  Link,
  Links,
  Meta,
  Outlet,
  Scripts,
  ScrollRestoration,
  data,
  isRouteErrorResponse,
  useRouteLoaderData,
  type LinksFunction,
} from "react-router";
import { buttonClass, Footer, NavBar } from "@insolvia-ai/design-system";

import { ORGANIZATION_JSONLD, isProductionHost } from "./lib/seo";
import { isPlaceholderSite } from "./lib/site-mode.server";
import stylesheet from "./styles/app.css?url";
import type { Route } from "./+types/root";

export const links: LinksFunction = () => [{ rel: "stylesheet", href: stylesheet }];

// Issue #48: every non-production host (staging, PR previews, direct
// CloudFront/API-Gateway URLs, localhost) is noindexed — via the X-Robots-Tag
// response header here and the <meta name="robots"> in <head> below.
export function loader({ request }: Route.LoaderArgs) {
  // Placeholder mode noindexes unconditionally, production host or not: a
  // holding page indexed as the site's canonical content is worse than no
  // index at all, and the real launch should get a clean first crawl.
  const placeholder = isPlaceholderSite();
  const noindex = placeholder || !isProductionHost(request);
  return data(
    { noindex, placeholder },
    noindex ? { headers: { "X-Robots-Tag": "noindex, nofollow" } } : undefined,
  );
}

// Forward the loader's X-Robots-Tag to the document response. Routes without
// their own `headers` export inherit this one (deepest export wins).
export function headers({ loaderHeaders }: Route.HeadersArgs) {
  return loaderHeaders;
}

export function Layout({ children }: { children: ReactNode }) {
  const rootData = useRouteLoaderData<typeof loader>("root");
  // Fail closed: if the root loader didn't run (e.g. an error page), noindex.
  const noindex = rootData?.noindex ?? true;
  return (
    <html lang="en">
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        {noindex ? <meta name="robots" content="noindex, nofollow" /> : null}
        <Meta />
        <Links />
        <script
          type="application/ld+json"
          dangerouslySetInnerHTML={{ __html: ORGANIZATION_JSONLD }}
        />
      </head>
      <body className="min-h-screen bg-bg font-body text-ink antialiased">
        {children}
        <ScrollRestoration />
        <Scripts />
      </body>
    </html>
  );
}

export default function App() {
  const rootData = useRouteLoaderData<typeof loader>("root");
  // Fail closed the same way the noindex flag does: no root data means an
  // error page, where the marketing chrome is noise at best.
  const placeholder = rootData?.placeholder ?? false;

  // Placeholder mode drops the navigation entirely — every link in it points at
  // marketing anchors that this deployment does not serve.
  //
  // The footer is KEPT, minus the Explore group, and that is deliberate rather
  // than an oversight: the privacy policy has to stay reachable site-wide
  // without searching, because AWS reviews exactly that as part of the SES
  // production-access request (docs/runbooks/ses-production-access.md) and
  // every transactional email footer points at the same URL. A holding page
  // that broke the only link to it would quietly undermine issue #28.
  if (placeholder) {
    return (
      <div className="flex min-h-screen flex-col">
        <main className="flex-1">
          <Outlet />
        </main>
        <Footer.Root>
          <Footer.Group title="Legal">
            <Footer.Link href="/privacy">Privacy policy</Footer.Link>
          </Footer.Group>
          <Footer.Note>
            &copy; {new Date().getFullYear()} Insolvia. All rights reserved.
          </Footer.Note>
        </Footer.Root>
      </div>
    );
  }

  return (
    <div className="flex min-h-screen flex-col">
      <NavBar.Root>
        <NavBar.Brand href="/">Insolvia</NavBar.Brand>
        <NavBar.Links className="hidden sm:flex">
          <NavBar.Link href="/#why">Why Insolvia</NavBar.Link>
          <NavBar.Link href="/#product">Product</NavBar.Link>
          <NavBar.Link href="/#faq">FAQ</NavBar.Link>
        </NavBar.Links>
        <NavBar.Actions>
          <Link to="/waitlist" className={buttonClass({ intent: "primary", size: "sm" })}>
            Early access
          </Link>
        </NavBar.Actions>
      </NavBar.Root>
      <main className="flex-1">
        <Outlet />
      </main>
      <Footer.Root>
        <div className="flex flex-wrap gap-xxl">
          <Footer.Group title="Explore">
            <Footer.Link href="/#why">Why Insolvia</Footer.Link>
            <Footer.Link href="/#product">Product</Footer.Link>
            <Footer.Link href="/#faq">FAQ</Footer.Link>
            <Footer.Link href="/waitlist">Early access</Footer.Link>
          </Footer.Group>
          {/* /privacy is linked from every page on purpose: AWS reviews a
              site-wide, reachable-without-searching privacy policy as part of
              the SES production-access request (docs/runbooks/ses-production-access.md),
              and every transactional email footer points at the same URL. */}
          <Footer.Group title="Legal">
            <Footer.Link href="/privacy">Privacy policy</Footer.Link>
          </Footer.Group>
        </div>
        <Footer.Note>
          &copy; {new Date().getFullYear()} Insolvia. All rights reserved. Insolvia is
          case-preparation software, not a law firm, and does not provide legal advice.
        </Footer.Note>
      </Footer.Root>
    </div>
  );
}

export function ErrorBoundary({ error }: { error: unknown }) {
  const title = isRouteErrorResponse(error)
    ? `${error.status} ${error.statusText}`
    : "Something went wrong";
  const detail = isRouteErrorResponse(error)
    ? error.data
    : error instanceof Error
      ? error.message
      : "Unknown error";
  return (
    <main className="mx-auto flex min-h-screen max-w-2xl flex-col justify-center gap-4 px-6">
      <h1 className="font-heading text-4xl text-primary">{title}</h1>
      <p className="text-muted">{String(detail)}</p>
      <a href="/" className="text-primary underline">
        Back home
      </a>
    </main>
  );
}
