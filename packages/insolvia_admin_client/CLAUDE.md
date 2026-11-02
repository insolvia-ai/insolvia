# insolvia_admin_client — agent rules

The staff portal's client for the admin service, as a package with two
consumers (ADR 0029 rule 2). Human docs: [`README.md`](README.md). Gated by
`.github/workflows/admin-pr.yml`'s `Admin client` job (format, lint,
typecheck, test); the `Admin portal` job builds the portal against it.

- **Two consumers, two ways in.** `contract/` takes it as a root workspace
  member; the portal takes it as `file:../../packages/insolvia_admin_client`
  and stays OUTSIDE the workspace with its own lockfile (its `package.json`
  owns why). Never publish it, and never add the portal to `workspaces`.
- **No dependencies, runtime or dev.** Platform `fetch`; Jest, TypeScript and
  ESLint come from the workspace root. A dependency here would have to be
  installed twice — once by the root lockfile, once by the portal's.
- **Its source is compiled by both consumers' settings**: the workspace's
  `tsconfig.base.json` (NodeNext, `.ts` relative imports, `erasableSyntaxOnly`)
  and the portal's (bundler resolution, DOM lib). Keep it to code both accept.
- **`client.test.ts` pins the seam** against literal admin-service JSON, with
  a stubbed `fetch`; `contract/src/admin` proves the same client against the
  real service, and its coverage gate fails when a public method is never
  called there. A new method needs both in the same PR.
