// The bundler resolves `./button` to button.web.tsx (Vite) or button.native.tsx
// (Metro) by extension. Types come from the platform-agnostic props module.
export { Button } from './button';
export type { ButtonProps } from './button';
export { buttonClass } from './button.props';
export type { ButtonIntent, ButtonSize, ButtonClassOptions } from './button.props';
