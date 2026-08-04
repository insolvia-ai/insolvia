// The bundler resolves `./dialog` to dialog.web.tsx (Vite) or dialog.native.tsx
// (Metro) by extension. Types come from the platform-agnostic props module.
export { Dialog } from './dialog';
export type { DialogRootOwnProps } from './dialog.props';
