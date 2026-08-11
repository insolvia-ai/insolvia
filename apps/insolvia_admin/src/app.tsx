import { useMemo, type ReactNode } from "react";
import {
  BrowserRouter,
  Link,
  Navigate,
  Route,
  Routes,
  useLocation,
} from "react-router";
import { Button } from "@insolvia-ai/design-system";

import { config } from "./config/environment";
import { AdminClient } from "./api/client";
import { SessionProvider, useSession } from "./session/session";
import { ClientContext } from "./api/use-client";
import { AuthCallbackScreen } from "./screens/auth-callback";
import { FirmDetailScreen } from "./screens/firm-detail";
import { FirmsScreen } from "./screens/firms";
import { ProvisionScreen } from "./screens/provision";
import { SignInScreen } from "./screens/sign-in";

/** Renders children only for a signed-in staff member; otherwise the sign-in
 * screen, remembering where they were headed. A COURTESY, never a control —
 * the admin service verifies every request itself. */
function RequireStaff({ children }: { children: ReactNode }) {
  const session = useSession();
  const location = useLocation();
  if (!session.signedIn) {
    return <SignInScreen returnTo={location.pathname} />;
  }
  return <>{children}</>;
}

function Shell({ children }: { children: ReactNode }) {
  const session = useSession();
  return (
    <div className="min-h-screen">
      <header className="flex items-center justify-between border-b border-line px-6 py-3">
        <div className="flex items-baseline gap-6">
          <span className="font-serif text-lg font-bold">Insolvia Admin</span>
          <Link to="/firms" className="text-sm text-primary hover:underline">
            Firms
          </Link>
        </div>
        <div className="flex items-center gap-3 text-sm">
          {config.environment !== "production" ? (
            <span className="rounded bg-surface-alt px-2 py-0.5 text-xs uppercase tracking-wide text-muted">
              {config.environment}
            </span>
          ) : null}
          <span className="text-muted">{session.email}</span>
          <Button intent="ghost" size="sm" onClick={() => session.signOut()}>
            Sign out
          </Button>
        </div>
      </header>
      <main className="mx-auto max-w-4xl px-6 py-8">{children}</main>
    </div>
  );
}

function Wiring({ children }: { children: ReactNode }) {
  const session = useSession();
  const client = useMemo(
    () => new AdminClient(config.apiBaseUrl, session.token),
    [session.token],
  );
  return <ClientContext.Provider value={client}>{children}</ClientContext.Provider>;
}

export function App() {
  return (
    <BrowserRouter>
      <SessionProvider>
        <Wiring>
          <Routes>
            <Route path="/" element={<Navigate to="/firms" replace />} />
            <Route path="/auth/callback" element={<AuthCallbackScreen />} />
            <Route
              path="/firms"
              element={
                <RequireStaff>
                  <Shell>
                    <FirmsScreen />
                  </Shell>
                </RequireStaff>
              }
            />
            <Route
              path="/firms/new"
              element={
                <RequireStaff>
                  <Shell>
                    <ProvisionScreen />
                  </Shell>
                </RequireStaff>
              }
            />
            <Route
              path="/firms/:firmId"
              element={
                <RequireStaff>
                  <Shell>
                    <FirmDetailScreen />
                  </Shell>
                </RequireStaff>
              }
            />
            <Route path="*" element={<Navigate to="/firms" replace />} />
          </Routes>
        </Wiring>
      </SessionProvider>
    </BrowserRouter>
  );
}
