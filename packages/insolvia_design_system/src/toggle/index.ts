// The bundler resolves `./toggle` to toggle.web.tsx (Vite) or toggle.native.tsx
// (Metro) by extension. Types come from the platform-agnostic props module.
export { Toggle } from './toggle';
export type { ToggleProps } from './toggle';
export type { ToggleOwnProps, ToggleState } from './toggle.props';
