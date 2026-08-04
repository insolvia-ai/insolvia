// WEB LEAF — plain React DOM + Tailwind. Shares its entire state model with
// the native leaf via checkbox-group.props; what lives here is the DOM: a
// `role="group"` wrapper, so a screen reader announces the set as related
// before landing on the first checkbox. Pair it with an `aria-label` or an
// `aria-labelledby` pointing at a caller-rendered heading — this component
// does not render one itself, the same "labeling is the caller's concern"
// split the brief draws for the standalone Checkbox.
import * as React from 'react';

import { cn } from '../lib/cn';
import {
  CheckboxGroupContext,
  useCheckboxGroupState,
  type CheckboxGroupRootOwnProps,
} from './checkbox-group.props';

export interface CheckboxGroupRootProps
  extends
    Omit<React.ComponentPropsWithoutRef<'div'>, 'defaultValue' | 'onChange'>,
    CheckboxGroupRootOwnProps {}

const CheckboxGroupRoot = React.forwardRef<HTMLDivElement, CheckboxGroupRootProps>(
  (
    { className, value, defaultValue, onValueChange, disabled = false, children, ...props },
    ref,
  ) => {
    const ctx = useCheckboxGroupState(value, defaultValue, onValueChange, disabled);
    return (
      <CheckboxGroupContext.Provider value={ctx}>
        <div ref={ref} role="group" className={cn('flex flex-col gap-sm', className)} {...props}>
          {children}
        </div>
      </CheckboxGroupContext.Provider>
    );
  },
);
CheckboxGroupRoot.displayName = 'CheckboxGroup.Root';

export const CheckboxGroup = {
  Root: CheckboxGroupRoot,
};
