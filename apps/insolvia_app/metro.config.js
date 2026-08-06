// Metro configuration for the Insolvia app.
//
// The app is an npm workspace member, so its dependencies are hoisted to the
// repo root. Metro does not walk up the tree the way Node's resolver does, so a
// default config finds them. The three settings below are Expo's documented
// monorepo recipe:
//
//   watchFolders          — watch the whole repo, so an edit to a workspace
//                           package triggers a rebuild (and Metro is allowed to
//                           read files outside this directory at all).
//   nodeModulesPaths      — resolve from the app first, then the hoisted root.
//   disableHierarchicalLookup — turn OFF the implicit walk up the tree, so the
//                           two paths above are the ONLY resolution roots. A
//                           dependency missing from this package.json then
//                           fails here instead of silently resolving from the
//                           root — the trap the root package.json warns about.
//
// @insolvia-ai/tokens and @insolvia-ai/design-system export TypeScript source
// with no build step, which Metro transforms like any other source file. Both
// are now PUBLISHED packages installed into node_modules rather than workspace
// members symlinked into packages/ — see the design-system block below, which
// is the one place that distinction changes the config.

const { getDefaultConfig } = require('expo/metro-config');
const path = require('path');

const projectRoot = __dirname;
const workspaceRoot = path.resolve(projectRoot, '../..');

const config = getDefaultConfig(projectRoot);

config.watchFolders = [workspaceRoot];
config.resolver.nodeModulesPaths = [
  path.resolve(projectRoot, 'node_modules'),
  path.resolve(workspaceRoot, 'node_modules'),
];
config.resolver.disableHierarchicalLookup = true;

// ── @insolvia-ai/design-system: this app renders the .native leaves on EVERY
// platform, web included ─────────────────────────────────────────────────────
//
// The design system is platform-split: each component is a shared props module
// plus a `.web.tsx` leaf (plain DOM + Tailwind classes, for the marketing
// site's Vite) and a `.native.tsx` leaf (RN primitives + StyleSheet). This app
// is an RN app on every platform — react-native-web renders the native leaves
// in the browser, exactly like its own components — and it has NO Tailwind
// pipeline (ADR 0004, decision 3a), so a `.web` leaf here would render
// unstyled: its className strings would reference CSS that is never built.
//
// Metro's own platform logic resolves `.web.tsx` first when bundling for web,
// which is right for a DOM app and wrong for this one. The override below is
// scoped to imports ORIGINATING inside the design-system package and tries the
// `.native` leaf first; anything that isn't a leaf pair (props modules, the
// barrel) falls through to normal resolution. Package `exports` conditions
// cannot express this — conditions select entry points, not the platform
// suffix of the package's internal relative imports.
// The package now installs into node_modules/@insolvia-ai/design-system rather
// than being a workspace member at packages/insolvia_design_system, so this
// match moved with it. Both segments are matched together: `design-system`
// alone would also match a directory of that name anywhere in the tree, and
// this override must apply to exactly one package's internal imports.
const packageDir = `${path.sep}@insolvia-ai${path.sep}design-system${path.sep}`;
const defaultResolveRequest = config.resolver.resolveRequest;
config.resolver.resolveRequest = (context, moduleName, platform) => {
  const resolve = defaultResolveRequest ?? context.resolveRequest;
  if (context.originModulePath.includes(packageDir) && moduleName.startsWith('.')) {
    try {
      return resolve(context, `${moduleName}.native`, platform);
    } catch {
      // Not a leaf pair (e.g. './button.props' or the barrel) — resolve as-is.
    }
  }
  return resolve(context, moduleName, platform);
};

module.exports = config;
