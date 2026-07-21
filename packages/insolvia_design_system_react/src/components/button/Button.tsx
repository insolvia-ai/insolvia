import type { Button as ButtonNamespace } from '@base-ui/react/button';

import * as React from 'react';
import { Button as BaseButton } from '@base-ui/react/button';

import { cn } from '../../lib/cn';
import { disabledStyles, focusRing } from '../../lib/styles';

export type ButtonIntent = 'primary' | 'secondary' | 'ghost';
export type ButtonSize = 'sm' | 'md' | 'lg';

export interface ButtonProps extends Omit<ButtonNamespace.Props, 'className'> {
  intent?: ButtonIntent;
  size?: ButtonSize;
  className?: string;
}

// Marketing needs exactly three weights of call-to-action. There is no `danger`
// intent because the semantic token set has no `danger-text` pair, and a
// marketing page has nothing destructive to offer.
const intentStyles: Record<ButtonIntent, string> = {
  primary: 'bg-primary text-primary-text hover:bg-primary-hover active:bg-primary-active',
  secondary: 'bg-surface-alt text-ink hover:bg-line active:bg-line',
  ghost: 'bg-transparent text-ink hover:bg-surface-alt active:bg-line',
};

const sizeStyles: Record<ButtonSize, string> = {
  sm: 'h-8 gap-1.5 px-3 text-sm',
  md: 'h-10 gap-2 px-4 text-sm',
  lg: 'h-12 gap-2 px-6 text-base',
};

export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, intent = 'primary', size = 'md', ...props }, ref) => {
    return (
      <BaseButton
        ref={ref}
        className={cn(
          'inline-flex cursor-pointer items-center justify-center whitespace-nowrap rounded-md font-body font-medium transition-colors',
          focusRing,
          disabledStyles,
          intentStyles[intent],
          sizeStyles[size],
          className,
        )}
        {...props}
      />
    );
  },
);

Button.displayName = 'Button';
