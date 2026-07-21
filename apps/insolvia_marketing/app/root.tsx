import type { ReactNode } from "react";
import {
  Links,
  Meta,
  Outlet,
  Scripts,
  ScrollRestoration,
  isRouteErrorResponse,
  type LinksFunction,
} from "react-router";
import { Button, Footer, NavBar } from "@insolvia/design-system";

import stylesheet from "./styles/app.css?url";

export const links: LinksFunction = () => [{ rel: "stylesheet", href: stylesheet }];

export function Layout({ children }: { children: ReactNode }) {
  return (
    <html lang="en">
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <Meta />
        <Links />
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
          <Button intent="primary" size="sm" nativeButton={false} render={<a href="/waitlist" />}>
            Early access
          </Button>
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
      <a href="/" className="text-accent underline">
        Back home
      </a>
    </main>
  );
}
