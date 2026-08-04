// NATIVE LEAF — React Native primitives, faithful switch. Shares the exact
// on/off state with the web leaf (switch.props); what is reimplemented here
// is the a11y surface and the visual: RN has no <button role="switch">, so
// Root is a Pressable with accessibilityRole="switch", and Thumb is a plain
// View translated by an inline `transform` computed from `checked` at render
// time (the RN equivalent of the web leaf's data-state Tailwind variant).
// Checked/disabled are reported BOTH ways — see radio-group.native.tsx's
// header comment for why `aria-checked`/`aria-disabled` (not
// `accessibilityState` alone) are what the app-web build actually renders
// through react-native-web, which is the only target that ships (ADR 0004).
import { Pressable, StyleSheet, View, type PressableProps } from 'react-native';

import { radii } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import {
  SwitchContext,
  useSwitchContext,
  useSwitchState,
  type SwitchRootOwnProps,
} from './switch.props';

export interface SwitchRootProps
  extends Omit<PressableProps, 'onPress' | 'style' | 'disabled'>,
    SwitchRootOwnProps {
  style?: PressableProps['style'];
}

const SwitchRoot = ({
  checked,
  defaultChecked = false,
  onCheckedChange,
  disabled = false,
  style,
  children,
  ...props
}: SwitchRootProps) => {
  const state = useSwitchState(checked, defaultChecked, onCheckedChange, disabled);
  const c = useNativeColors();

  return (
    <SwitchContext.Provider value={{ checked: state.checked, disabled: state.disabled }}>
      <Pressable
        accessibilityRole="switch"
        accessibilityState={{ checked: state.checked, disabled: state.disabled }}
        aria-checked={state.checked}
        disabled={state.disabled}
        onPress={state.toggle}
        style={(pressState) => [
          styles.track,
          {
            backgroundColor: state.checked ? c.primary : c.line,
            opacity: state.disabled ? 0.5 : pressState.pressed ? 0.9 : 1,
          },
          typeof style === 'function' ? style(pressState) : style,
        ]}
        {...props}
      >
        {children}
      </Pressable>
    </SwitchContext.Provider>
  );
};

const SwitchThumb = () => {
  const { checked } = useSwitchContext('Thumb');
  const c = useNativeColors();
  return (
    <View
      style={[styles.thumb, { backgroundColor: c.card, transform: [{ translateX: checked ? 22 : 2 }] }]}
    />
  );
};

export const Switch = {
  Root: SwitchRoot,
  Thumb: SwitchThumb,
};

const styles = StyleSheet.create({
  track: { width: 44, height: 24, borderRadius: radii.pill, justifyContent: 'center' },
  thumb: { width: 20, height: 20, borderRadius: radii.pill },
});
