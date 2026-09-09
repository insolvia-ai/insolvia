// Rules live in the repo root's `eslint.base.js`. That file is not named
// `eslint.config.js` on purpose — see its header for what breaks when a
// discoverable flat config sits at the repo root.
//
// `.mjs`, not `.js`, unlike the other workspace members: this package.json has
// no `"type": "module"` because `metro.config.js` must stay CommonJS (Metro
// `require()`s it). Node resolves module type from the NEAREST package.json, so
// an ESM `eslint.config.js` here would be reparsed with a warning on every run.
//
// Four additions on top of the shared base, all because this is the only
// workspace member that renders UI and the only one with a CommonJS config file.
import base from '../../eslint.base.js';

/**
 * Every `<Text>` states a `fontFamily`.
 *
 * react-native-web gives a Text no family of its own — its base style is
 * `font: 14px System`, which the compiler turns into the platform's system
 * sans — so a Text that does not ask for `theme.typography.body` renders San
 * Francisco or Segoe beside the Open Sans of the Text next to it. That is
 * exactly what shipped: twenty-odd help lines, error lines and row labels in
 * the system face, invisible in a test (jsdom has no fonts) and obvious on a
 * page. A convention nobody checks is not a convention; this checks it.
 *
 * What counts as stated, by what the rule can see statically:
 *
 *   - an inline object in `style` carrying a `fontFamily` key — the app's own
 *     shape, `[styles.x, { color: …, fontFamily: theme.typography.body }]`;
 *   - a bare identifier in the array — `muted`, `ink`, `danger` — which every
 *     screen builds as `{ color, fontFamily }` at render time and which this
 *     rule takes as vouched for;
 *   - a conditional whose BOTH branches are one of the above.
 *
 * `styles.x` on its own does not count: a `StyleSheet.create` block CAN hold a
 * family (they do not vary by scheme), but none here does, and reading that
 * block from a lint rule would be reimplementing the bundler. Two exemptions,
 * both because the text inherits or has no glyph to set: a Text nested inside
 * another Text (react-native-web emits `font: inherit`), and an `aria-hidden`
 * Text, which is a glyph — an arrow, a sun — drawn by whatever face has it.
 */
const textStatesFamily = {
  meta: {
    type: 'problem',
    docs: { description: 'every <Text> asks for a brand family, or inherits one' },
    messages: {
      missing:
        '<Text> states no fontFamily, so react-native-web renders the system sans beside the ' +
        'brand face. Add `fontFamily: theme.typography.body` (or `.mono`) to the inline style, or ' +
        'use one of the render-time `muted`/`ink`/`danger` objects — see apps/insolvia_app/CLAUDE.md.',
    },
    schema: [],
  },
  create(context) {
    const isText = (node) =>
      node.type === 'JSXElement' &&
      node.openingElement.name.type === 'JSXIdentifier' &&
      node.openingElement.name.name === 'Text';

    const keyName = (property) =>
      property.key.type === 'Identifier'
        ? property.key.name
        : property.key.type === 'Literal'
          ? String(property.key.value)
          : null;

    const states = (expr) => {
      if (expr === null || expr === undefined) return false;
      switch (expr.type) {
        case 'ObjectExpression':
          return expr.properties.some(
            (property) => property.type === 'Property' && keyName(property) === 'fontFamily',
          );
        case 'ArrayExpression':
          return expr.elements.some((element) => states(element));
        case 'Identifier':
          return true;
        case 'ConditionalExpression':
          return states(expr.consequent) && states(expr.alternate);
        case 'TSAsExpression':
        case 'TSNonNullExpression':
          return states(expr.expression);
        default:
          return false;
      }
    };

    return {
      JSXOpeningElement(node) {
        if (node.name.type !== 'JSXIdentifier' || node.name.name !== 'Text') return;

        const attributes = node.attributes.filter((a) => a.type === 'JSXAttribute');
        if (attributes.some((a) => a.name.name === 'aria-hidden')) return;

        let ancestor = node.parent.parent;
        while (ancestor) {
          if (isText(ancestor)) return;
          ancestor = ancestor.parent;
        }

        const style = attributes.find((a) => a.name.name === 'style');
        const expr =
          style?.value?.type === 'JSXExpressionContainer' ? style.value.expression : null;
        if (!states(expr)) context.report({ node, messageId: 'missing' });
      },
    };
  },
};

export default [
  ...base,
  {
    files: ['src/**/*.tsx'],
    ignores: ['**/*.test.tsx'],
    plugins: { insolvia: { rules: { 'text-states-family': textStatesFamily } } },
    rules: { 'insolvia/text-states-family': 'error' },
  },
  { ignores: ['.expo/**', 'dist/**', 'expo-env.d.ts'] },
  {
    // The CommonJS config files. Each is loaded by a tool that `require()`s
    // them, and this package.json has no `"type": "module"`, so neither can be
    // ESM — Metro loads metro.config.js, Jest loads jest.config.js.
    //
    // jest.config.js has to `require('jest-expo/jest-preset')` specifically:
    // it COMPUTES `transformIgnorePatterns` from the preset's own array rather
    // than restating it, because the preset carries entries (standard-navigation)
    // that hand-copied lists silently drop. That computation is why the config
    // is a .js file at all instead of a `jest` key in package.json.
    files: ['metro.config.js', 'jest.config.js', 'jest.setup.js'],
    rules: { '@typescript-eslint/no-require-imports': 'off' },
  },
  {
    files: ['**/*.test.ts', '**/*.test.tsx'],
    languageOptions: {
      globals: {
        afterEach: 'readonly',
        beforeEach: 'readonly',
        describe: 'readonly',
        expect: 'readonly',
        it: 'readonly',
        jest: 'readonly',
      },
    },
  },
];
