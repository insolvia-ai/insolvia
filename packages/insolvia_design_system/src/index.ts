// @insolvia-ai/design-system — owned, platform-split UI primitives. The same
// six component names the outgoing web-only package exported; the CONSUMER'S
// bundler picks each component's .web / .native leaf by extension (see
// README.md). Keep this barrel the source of truth for what the package
// exports.
export { Accordion } from './accordion';
export { Button, buttonClass } from './button';
export type { ButtonIntent, ButtonSize, ButtonClassOptions } from './button';
export { Card } from './card';
export { Field } from './field';
export { Footer } from './footer';
export { NavBar } from './nav-bar';
