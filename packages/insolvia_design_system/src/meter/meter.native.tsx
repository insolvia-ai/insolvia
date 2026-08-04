// NATIVE LEAF — React Native primitives, visually identical to Progress's
// native leaf but always determinate and ranged over [min, max].
// RN's `AccessibilityRole` union has no "meter" entry (see progress.native.tsx
// for Progress's own role) and, unlike an aria-* prop, it is a closed string
// union rather than an escape-hatch cast target, so it isn't worth a contained
// cast here — this follows the conventions brief's other sanctioned option
// verbatim: `accessibilityRole="progressbar"` with a comment. A meter is
// exactly "a non-interactive gauge over a known range," which is what
// `progressbar` communicates to assistive tech in RN's own vocabulary; the
// value/min/max triple below carries the actual meter semantics regardless of
// which role name ships them.
import * as React from 'react';
import { StyleSheet, View, type ViewProps } from 'react-native';

import { radii } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import {
  MeterRootContext,
  meterPercent,
  useMeterRootContext,
  type MeterRootOwnProps,
} from './meter.props';

export interface MeterRootProps extends Omit<ViewProps, 'children'>, MeterRootOwnProps {
  children?: React.ReactNode;
}

const MeterRoot = ({ value, min = 0, max = 100, children, style, ...props }: MeterRootProps) => {
  const percent = meterPercent(value, min, max);

  return (
    <View
      accessibilityRole="progressbar"
      accessibilityValue={{ now: value, min, max }}
      style={[styles.root, style]}
      {...props}
    >
      <MeterRootContext.Provider value={{ value, min, max, percent }}>
        {children}
      </MeterRootContext.Provider>
    </View>
  );
};

const MeterTrack = ({ style, ...props }: ViewProps) => {
  const c = useNativeColors();
  return <View style={[styles.track, { backgroundColor: c.surfaceAlt }, style]} {...props} />;
};

const MeterIndicator = ({ style, ...props }: ViewProps) => {
  const { percent } = useMeterRootContext('Indicator');
  const c = useNativeColors();

  return (
    <View
      style={[styles.indicator, { backgroundColor: c.primary, width: `${percent}%` }, style]}
      {...props}
    />
  );
};

export const Meter = {
  Root: MeterRoot,
  Track: MeterTrack,
  Indicator: MeterIndicator,
};

const styles = StyleSheet.create({
  root: { width: '100%' },
  track: { height: 8, width: '100%', overflow: 'hidden', borderRadius: radii.pill },
  indicator: { height: '100%', borderRadius: radii.pill },
});
