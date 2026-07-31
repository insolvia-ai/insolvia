// Rules live in the repo root's `eslint.base.js`. That file is not named
// `eslint.config.js` on purpose — see its header for what breaks when a
// discoverable flat config sits at the repo root.
//
// The one addition is this package's load-bearing rule, machine-enforced: a
// `*.props.ts` module is the platform-SHARED third of a component and must
// never import a renderer. One react-native import in a props file would drag
// react-native-web into marketing's bundle; one react-dom import would break
// the native leaves. The leaves themselves import their own platform freely —
// the ban is scoped to the shared modules only.
import base from '../../eslint.base.js';

export default [
  ...base,
  {
    files: ['**/*.props.ts', 'src/lib/**/*.ts'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: ['react-native', 'react-native/*', 'react-native-web', 'react-native-web/*'],
              message:
                'Shared props modules must stay renderer-free: react-native belongs in the .native leaf only.',
            },
            {
              group: ['react-dom', 'react-dom/*'],
              message:
                'Shared props modules must stay renderer-free: react-dom belongs in the .web leaf only.',
            },
            {
              group: ['@base-ui/*'],
              message:
                'Base UI was retired with insolvia_design_system_react; nothing here may depend on it.',
            },
          ],
        },
      ],
    },
  },
];
