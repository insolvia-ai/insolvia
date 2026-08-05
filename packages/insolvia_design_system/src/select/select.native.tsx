// NATIVE LEAF — React Native primitives over the shared grammar
// (select.props). Same WAI-ARIA select-only combobox as the web leaf: focus
// stays on the trigger and the list is addressed through
// `aria-activedescendant`, so one key handler drives everything.
//
// THIS LEAF IS THE ONE THE APP SHIPS, on web included (the resolveRequest
// override in apps/insolvia_app/metro.config.js), which is why it implements
// keyboard interaction at all — every other complex widget in this package
// puts its arrow keys in the `.web` leaf, where the app never sees them. On a
// real device the key handlers are simply inert.
//
// The list renders INLINE and absolutely positioned rather than in a Modal, as
// Dialog does. A Modal owns focus, and this pattern requires focus to stay on
// the trigger; the cost is that a very long list on a small screen is better
// served by a Modal, which is a change to make when a native client exists.
import * as React from 'react';
import {
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
  type PressableProps,
  type ViewProps,
} from 'react-native';

import { radii, spacing } from '@insolvia-ai/tokens';

import { FieldContext } from '../field/field.props';
import { useNativeColors } from '../lib/native-theme';
import {
  getListboxId,
  getOptionId,
  selectKeyIntent,
  useSelectState,
  useTypeahead,
  type SelectOwnProps,
} from './select.props';

export interface SelectProps extends Omit<ViewProps, 'children'>, SelectOwnProps {
  /** Names the control when it is not inside a `<Field.Root>`. */
  'aria-label'?: string;
}

export const Select = ({
  options,
  value,
  defaultValue,
  onValueChange,
  placeholder = 'Select…',
  disabled = false,
  name: _name,
  style,
  ...props
}: SelectProps) => {
  const field = React.useContext(FieldContext);
  const c = useNativeColors();
  const state = useSelectState({ options, value, defaultValue, onValueChange });
  const { open, setOpen, active, setActive, commit, selected, rootId } = state;

  const activeRef = React.useRef(active);
  activeRef.current = active;
  const typeahead = useTypeahead(
    options,
    React.useCallback(() => activeRef.current, []),
  );

  const handleKeyDown = (event: { key: string; altKey?: boolean; preventDefault?: () => void }) => {
    const intent = selectKeyIntent(event.key, event.altKey ?? false, {
      open,
      options,
      value: state.value,
      active,
      typing: typeahead.isTyping(),
    });
    if (intent.kind === 'none') return;
    // Tab has to keep moving focus; everything else would scroll the page or
    // submit the form. Same rule as the web leaf.
    if (event.key !== 'Tab') event.preventDefault?.();

    switch (intent.kind) {
      case 'open':
        setOpen(true);
        setActive(intent.active);
        break;
      case 'close':
        setOpen(false);
        break;
      case 'active':
        setActive(intent.active);
        break;
      case 'commit':
        commit(intent.value);
        break;
      case 'typeahead': {
        const match = typeahead.push(intent.char);
        if (match === null) break;
        if (open) setActive(match);
        else commit(match);
        break;
      }
    }
  };

  const listboxId = getListboxId(rootId);
  const invalid = field?.invalid ?? false;

  // react-native-web forwards these to the DOM but React Native's own types
  // carry none of them: `onKeyDown` is web-only, and `aria-controls` /
  // `aria-activedescendant` / `aria-describedby` are outside RN's
  // AccessibilityProps. Contained here and documented, the same shape Dialog's
  // native leaf uses for `aria-describedby`.
  const webOnly = {
    onKeyDown: handleKeyDown,
    'aria-controls': listboxId,
    ...(open && active !== null ? { 'aria-activedescendant': getOptionId(rootId, active) } : {}),
    ...(field?.describedBy === undefined ? {} : { 'aria-describedby': field.describedBy }),
  } as Partial<PressableProps>;

  return (
    <View style={[styles.root, style]} {...props}>
      <Pressable
        nativeID={field?.controlId}
        role="combobox"
        aria-expanded={open}
        // The native leaf points the control back at the label, the direction
        // Field's native leaf establishes (the web leaf points label -> control
        // with htmlFor instead).
        aria-labelledby={field?.labelId}
        aria-label={props['aria-label']}
        aria-invalid={invalid}
        aria-disabled={disabled}
        disabled={disabled}
        onPress={() => {
          if (open) setOpen(false);
          else {
            setOpen(true);
            setActive(state.value ?? null);
          }
        }}
        // Focus leaving the trigger closes the list. An option press does NOT
        // reach here — the list cancels the focus change; see its onMouseDown.
        onBlur={() => setOpen(false)}
        style={[
          styles.trigger,
          {
            backgroundColor: disabled ? c.surfaceAlt : c.card,
            borderColor: invalid ? c.danger : c.line,
          },
        ]}
        {...webOnly}
      >
        <Text
          numberOfLines={1}
          style={[styles.triggerLabel, { color: selected && !disabled ? c.ink : c.muted }]}
        >
          {selected?.label ?? placeholder}
        </Text>
        <Text aria-hidden style={[styles.chevron, { color: c.muted }]}>
          ▾
        </Text>
      </Pressable>

      {open && (
        <View
          nativeID={listboxId}
          // React Native's `Role` union has `combobox` and `option` but not
          // `listbox` — an omission in RN's types, not in the platform:
          // react-native-web passes the string straight to the DOM, and the
          // combobox's `aria-controls` above is pointing at this element.
          //
          // `onMouseDown` is the load-bearing part, not the role. Under
          // react-native-web the trigger's blur fires BEFORE an option's
          // onPressIn (measured, not assumed), so any "am I pressing an
          // option?" flag is still false when blur runs — the list would
          // unmount between pointerdown and pointerup and the press would never
          // complete. Cancelling the default on mousedown stops focus leaving
          // the trigger at all, which is the same fix the web leaf uses.
          {...({
            role: 'listbox',
            onMouseDown: (event: { preventDefault: () => void }) => event.preventDefault(),
          } as unknown as Partial<ViewProps>)}
          style={[styles.list, { backgroundColor: c.card, borderColor: c.line }]}
        >
          <ScrollView keyboardShouldPersistTaps="always">
            {options.map((option) => {
              const isSelected = option.value === state.value;
              return (
                <Pressable
                  key={option.value}
                  nativeID={getOptionId(rootId, option.value)}
                  role="option"
                  aria-selected={isSelected}
                  aria-disabled={option.disabled ?? false}
                  disabled={option.disabled ?? false}
                  onPress={() => {
                    if (!option.disabled) commit(option.value);
                  }}
                  style={[
                    styles.option,
                    option.value === active && { backgroundColor: c.surfaceAlt },
                  ]}
                >
                  <Text
                    style={[
                      styles.optionLabel,
                      { color: option.disabled ? c.muted : c.ink },
                      isSelected && styles.optionLabelSelected,
                    ]}
                  >
                    {option.label}
                  </Text>
                </Pressable>
              );
            })}
          </ScrollView>
        </View>
      )}
    </View>
  );
};

const styles = StyleSheet.create({
  root: { position: 'relative' },
  trigger: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: spacing.sm,
    // 44dp, the WCAG 2.5.5 target-size floor the app enforces — deliberately
    // taller than Field's 40dp control, which is a text input rather than a
    // press target.
    height: 44,
    paddingHorizontal: spacing.sm,
    borderWidth: 1,
    borderRadius: radii.md,
  },
  triggerLabel: { flexShrink: 1, fontSize: 14 },
  chevron: { fontSize: 12 },
  list: {
    position: 'absolute',
    top: 44 + spacing.xs,
    left: 0,
    right: 0,
    zIndex: 10,
    maxHeight: 240,
    borderWidth: 1,
    borderRadius: radii.md,
    paddingVertical: spacing.xs,
  },
  option: {
    paddingHorizontal: spacing.sm,
    paddingVertical: spacing.xs,
    minHeight: 44,
    justifyContent: 'center',
  },
  optionLabel: { fontSize: 14 },
  optionLabelSelected: { fontWeight: '500' },
});
