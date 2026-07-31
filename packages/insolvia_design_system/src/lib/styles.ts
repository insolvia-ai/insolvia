// Shared web style fragments for the UI primitives. Plain Tailwind class
// strings — platform-agnostic text, no react-dom/react-native import — so they
// can live beside the props modules. The native leaves ignore them and style
// off @insolvia-ai/tokens instead.

export const focusRing =
  'outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-bg';

export const disabledStyles =
  'disabled:pointer-events-none disabled:opacity-50 disabled:cursor-not-allowed';
