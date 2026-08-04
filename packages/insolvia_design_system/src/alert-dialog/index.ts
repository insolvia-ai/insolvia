// The bundler resolves `./alert-dialog` to alert-dialog.web.tsx (Vite) or
// alert-dialog.native.tsx (Metro) by extension. Types come from the
// platform-agnostic props module, which itself reuses the dialog's machine.
export { AlertDialog } from './alert-dialog';
export type { AlertDialogRootOwnProps } from './alert-dialog.props';
