import { Image, StyleSheet } from 'react-native';

/**
 * The line icons the app draws, by name.
 *
 * Each is a Feather icon (https://feathericons.com), copied as its 24×24 path
 * data: a 2px round-capped stroke, no fill. Feather is MIT-licensed —
 * copyright (c) 2013-2023 Cole Bemis — and this comment is the notice that
 * licence asks to travel with the copied portions: permission is granted
 * free of charge to use, copy, modify and distribute, provided the notice
 * appears, and the software is provided "as is" without warranty of any kind.
 * Feather rather than an icon font or an SVG runtime because of what those
 * cost: `@expo/vector-icons` is a font file per family and a dependency ADR
 * 0004 asks to be measured first, and `react-native-svg` is a renderer with a
 * Metro transformer behind it. Eleven paths in a module are neither, and a
 * consumer that wants a twelfth adds a line here. If the set ever grows past
 * a couple of dozen, that is the moment to measure a real one.
 */
const PATHS = {
  home: '<path d="M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><polyline points="9 22 9 12 15 12 15 22"/>',
  folder: '<path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>',
  briefcase:
    '<rect x="2" y="7" width="20" height="14" rx="2" ry="2"/><path d="M16 21V5a2 2 0 0 0-2-2h-4a2 2 0 0 0-2 2v16"/>',
  grid: '<rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="14" y="14" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/>',
  clipboard:
    '<path d="M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2"/><rect x="8" y="2" width="8" height="4" rx="1" ry="1"/>',
  'file-text':
    '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="16" y1="13" x2="8" y2="13"/><line x1="16" y1="17" x2="8" y2="17"/><polyline points="10 9 9 9 8 9"/>',
  'check-square':
    '<polyline points="9 11 12 14 22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/>',
  list: '<line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/><line x1="3" y1="6" x2="3.01" y2="6"/><line x1="3" y1="12" x2="3.01" y2="12"/><line x1="3" y1="18" x2="3.01" y2="18"/>',
  package:
    '<line x1="16.5" y1="9.4" x2="7.5" y2="4.21"/><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><polyline points="3.27 6.96 12 12.01 20.73 6.96"/><line x1="12" y1="22.08" x2="12" y2="12"/>',
  users:
    '<path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
  'arrow-left': '<line x1="19" y1="12" x2="5" y2="12"/><polyline points="12 19 5 12 12 5"/>',
} as const;

export type IconName = keyof typeof PATHS;

export interface IconProps {
  name: IconName;
  /** The stroke colour — a resolved colour, since it is baked into the image. */
  color: string;
  /** The square's side, in dp. */
  size?: number;
}

/**
 * The SVG as a data URI, coloured.
 *
 * The colour is part of the image rather than a style because an `Image` has
 * no `currentColor`: react-native-web paints it as a background, and nothing
 * outside the SVG can reach its stroke. So there is one image per colour,
 * which for a rail that is dark in both schemes is two.
 *
 * RAW MARKUP AFTER THE PREFIX, NOT ENCODED. react-native-web's `Image`
 * recognises exactly `data:image/svg+xml;utf8,` and runs `encodeURIComponent`
 * over what follows itself (its `resolveAssetUri`, for the `#` in a colour).
 * Encoding here too encodes the `%` signs a second time, the browser gets an
 * SVG it cannot parse, and the loader reports a failure that paints nothing —
 * with no error anywhere.
 */
function svgUri(name: IconName, color: string): string {
  const svg =
    // `width`/`height` as well as the viewBox: an SVG with no intrinsic size
    // reports a natural width of 0, which react-native-web's loader reads as
    // "failed to load" and paints nothing.
    `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" ` +
    `fill="none" stroke="${color}" ` +
    // 1.75 rather than Feather's 2: at 16px beside 14px text a 2-unit stroke
    // reads heavier than the letters it labels.
    `stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">${PATHS[name]}</svg>`;
  return `data:image/svg+xml;utf8,${svg}`;
}

/**
 * A line icon, decorative: `alt=""` keeps it out of every accessible name,
 * and the control around it names itself in full.
 */
/**
 * 16 by default: the cap height of the 14px label beside it plus a little,
 * which is what keeps a row reading as text with a mark rather than a mark
 * with a caption. The collapsed rail asks for 20, where the icon is the row.
 */
export function Icon({ name, color, size = 16 }: IconProps) {
  return (
    <Image
      alt=""
      source={{ uri: svgUri(name, color) }}
      style={[styles.icon, { height: size, width: size }]}
    />
  );
}

const styles = StyleSheet.create({
  icon: {
    resizeMode: 'contain',
  },
});
