// Metro configuration for the Insolvia app.
//
// The app is an npm workspace member, so its dependencies are hoisted to the
// repo root and @insolvia-ai/tokens is a symlink into packages/. Metro does not
// walk up the tree the way Node's resolver does, so a default config finds
// neither. The three settings below are Expo's documented monorepo recipe:
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
// @insolvia-ai/tokens exports `./src/tokens.ts` — TypeScript source, no build
// step — which Metro transforms like any other source file.

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

module.exports = config;
