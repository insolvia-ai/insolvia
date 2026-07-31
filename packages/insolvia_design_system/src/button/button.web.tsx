// WEB LEAF — plain React DOM + Tailwind. No react-native. Vite resolves this.
import * as React from 'react';

import { buttonClass, type ButtonIntent, type ButtonSize } from './button.props';

export interface ButtonProps extends React.ComponentPropsWithoutRef<'button'> {
  intent?: ButtonIntent;
  size?: ButtonSize;
}

// A plain `<button>` for real buttons (form submits). Anything that *navigates*
// is a `<Link>`/`<a>` styled with `buttonClass`, not this. `type` defaults to
// "button" so a button inside a form never submits by accident.
export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, intent = 'primary', size = 'md', type, ...props }, ref) => {
    return (
      <button
        ref={ref}
        type={type ?? 'button'}
        className={buttonClass({ intent, size, className })}
        {...props}
      />
    );
  },
);

Button.displayName = 'Button';
