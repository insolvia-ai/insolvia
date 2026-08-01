import { type RouteConfig } from "@react-router/dev/routes";
import { flatRoutes } from "@react-router/fs-routes";

// `flatRoutes()` turns EVERY file under app/routes/ into a route module — test
// files included. React Router strips server-only exports (`loader`, `action`)
// from route modules when it builds the client bundle, so a colocated test that
// imports a loader fails the build with a MISSING_EXPORT naming the TEST file,
// which reads as the test being wrong rather than the route config.
//
// Typecheck, lint and the test run all pass in that state — only `npm run
// build` catches it. Removing this option is how it comes back.
export default flatRoutes({
  ignoredRouteFiles: ["**/*.test.{ts,tsx}"],
}) satisfies RouteConfig;
