import * as React from 'react';

import './preview.css';

import type { Preview } from '@storybook/react';

const preview: Preview = {
  // Generate an autodocs page for every component from its stories + prop types.
  tags: ['autodocs'],
  parameters: {
    controls: { expanded: true },
  },
  globalTypes: {
    theme: {
      description: 'Theme',
      toolbar: {
        title: 'Theme',
        icon: 'mirror',
        items: [
          { value: 'light', title: 'Light' },
          { value: 'dark', title: 'Dark' },
        ],
        dynamicTitle: true,
      },
    },
  },
  initialGlobals: {
    theme: 'light',
  },
  decorators: [
    (Story, context) => {
      // The toolbar switch stamps `data-theme` on the wrapper, which is exactly
      // the selector the generated theme.css keys its dark palette off — so
      // every story is a live dark-mode surface, no per-story wiring.
      const theme = context.globals.theme ?? 'light';
      // Fill the viewport in single-story canvas view; stay compact inside the
      // stacked story blocks of an autodocs page.
      const sizing = context.viewMode === 'docs' ? 'p-4' : 'min-h-screen p-8';

      return React.createElement(
        'div',
        { 'data-theme': theme, className: `bg-bg text-ink transition-colors ${sizing}` },
        React.createElement(Story),
      );
    },
  ],
};

export default preview;
