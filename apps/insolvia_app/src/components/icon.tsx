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
 * Metro transformer behind it. Three paths in a module are neither, and a
 * screen that wants a fourth adds a line here. If the set ever grows past a
 * couple of dozen, that is the moment to measure a real one.
 */
const PATHS = {
  home: '<path d="M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><polyline points="9 22 9 12 15 12 15 22"/>',
  folder: '<path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>',
  briefcase:
    '<rect x="2" y="7" width="20" height="14" rx="2" ry="2"/><path d="M16 21V5a2 2 0 0 0-2-2h-4a2 2 0 0 0-2 2v16"/>',
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
