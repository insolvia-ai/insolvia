// Cuts Insolvia's marks — the wordmark and the "I" icon — from the display
// face, as outlines, plus the rasters a browser asks for by file name.
//
//   ./scripts/render-brand-marks.sh        (the entry point; runs this)
//
// Writes:
//   brand/wordmark.svg      "Insolvia." outlined, cropped to the ink
//   brand/icon.svg          the "I" on a rounded dark tile
//   apps/insolvia_app/public/icons/Icon-{192,512}.png           (+ maskable)
//   apps/insolvia_app/public/favicon.ico                        16/32/48
//
// OUTLINES, NOT TEXT. A logo that names a font family renders in whatever the
// viewer has installed, and a favicon has no stylesheet to load one from. So
// the letterforms are cut here, once, from the same face the app self-hosts
// (`apps/insolvia_app/public/fonts/cormorant-700.woff2`, fetched by
// `scripts/fetch-brand-fonts.sh`). Cormorant Garamond is SIL OFL 1.1, which
// allows exactly this — its notice is `apps/insolvia_app/public/fonts/OFL.txt`.
//
// No colour in the SVGs: each path carries an `id` naming its role, and
// `tool/render-brand-marks.ts` (`npm run tokens`) writes the coloured copies.
// The rasters cannot defer a colour, so they bake the dark scheme in here —
// which is why a colour change means re-running this too, and why
// `npm run tokens:check` says so when the two disagree.
//
// ONE GEOMETRY, TWO WRITERS. `place()` and `iconGeometry()` decide where every
// point goes; the SVG pen and the rasteriser both draw from them, so the PNGs
// cannot drift from the SVG.
//
// THE DEPENDENCIES, and why three. HarfBuzz (harfbuzzjs) shapes and outlines,
// so the kerning is the font's own — Cormorant kerns "In" and "vi"
// noticeably. HarfBuzz cannot read WOFF2, so woff2-encoder (Google's
// reference decoder, compiled to WebAssembly) decompresses the face in
// memory first; the .woff2 stays the only copy on disk. resvg rasterises.
// All three are root devDependencies: this runs on a developer's machine,
// never in a build.
//
// The SVG path data is written the way the Python cutter this replaced wrote
// it (fontTools' SVGPathPen, Python's float repr), so re-cutting an unchanged
// face is a zero-byte diff.

import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { join, relative } from 'node:path';

import { Resvg } from '@resvg/resvg-js';
import * as hb from 'harfbuzzjs';
import { decompress } from 'woff2-encoder';

import { palette, repoRoot } from './brand-palette.ts';

const ROOT = repoRoot();
const FACE = join(ROOT, 'apps/insolvia_app/public/fonts/cormorant-700.woff2');

// Tracking, as a fraction of the em: the display face's -0.5px at 20px.
const TRACKING = -0.5 / 20;

// The wordmark's box is 2.4:1 — the letters span its full width and the
// height is padded, which is what sets the size the app renders it at.
const WORDMARK = { ratio: 2.4 };

const ICON = {
  letter: 'I',
  // Corner radius, as a fraction of the tile — brand/radii.json's card corner
  // at icon scale.
  radius: 0.12,
  // The letter's ink height, as a fraction of the tile.
  cap: 0.58,
  // Lifts the letter off optical centre: a serif cap sits low without it.
  rise: 0.015,
  // A maskable icon may be cropped to a circle of 80% of its side, so its
  // content must sit inside that. Shrink the tile's content, keep the
  // ground full-bleed.
  maskable_safe: 0.7,
};

type Point = readonly [number, number];

/** fontTools' `Transform(xx, xy, yx, yy, dx, dy)`, applied as its pens do. */
type Transform = readonly [number, number, number, number, number, number];

function apply([xx, xy, yx, yy, dx, dy]: Transform, [x, y]: Point): Point {
  return [xx * x + yx * y + dx, xy * x + yy * y + dy];
}

/**
 * A glyph's contours as fontTools draws a TrueType glyph: a move to the first
 * on-curve point, then lines and runs of quadratic off-curve points each
 * ending on-curve, and no closing line (the close implies it).
 *
 * HarfBuzz hands back atomic quadratic segments, with the on-curve points a
 * TrueType outline leaves implied already filled in. Those are folded back
 * into runs — a segment whose end is exactly the midpoint of its control and
 * the next one's — so the midpoints are computed after the transform, as
 * fontTools computes them. Otherwise the same point would differ in its last
 * bit, and the committed SVG would churn on every re-cut.
 */
type Segment = { kind: 'line'; to: Point } | { kind: 'qcurve'; points: Point[] };
type Contour = { start: Point; segments: Segment[] };

function outline(font: hb.Font, glyph: number): Contour[] {
  const contours: Contour[] = [];
  let current: Contour | undefined;
  const funcs = new hb.DrawFuncs();
  funcs.setMoveToFunc((x, y) => {
    current = { start: [x, y], segments: [] };
    contours.push(current);
  });
  funcs.setLineToFunc((x, y) => current?.segments.push({ kind: 'line', to: [x, y] }));
  funcs.setQuadraticToFunc((cx, cy, x, y) => {
    const segments = current!.segments;
    const last = segments.at(-1);
    if (last?.kind === 'qcurve') {
      const control = last.points.at(-2)!;
      const end = last.points.at(-1)!;
      if (end[0] === (control[0] + cx) / 2 && end[1] === (control[1] + cy) / 2) {
        last.points.splice(-1, 1, [cx, cy], [x, y]);
        return;
      }
    }
    segments.push({
      kind: 'qcurve',
      points: [
        [cx, cy],
        [x, y],
      ],
    });
  });
  funcs.setCubicToFunc(() => {
    throw new Error('a cubic outline: this face is TrueType, and the cutter expects quadratics');
  });
  funcs.setClosePathFunc(() => {
    const last = current?.segments.at(-1);
    if (
      last?.kind === 'line' &&
      last.to[0] === current!.start[0] &&
      last.to[1] === current!.start[1]
    ) {
      current!.segments.pop();
    }
    current = undefined;
  });
  font.drawGlyph(glyph, funcs);
  return contours;
}

/**
 * fontTools' BasePen: a quadratic run is split at its implied on-curve
 * points, in the pen's own (already transformed) coordinates.
 */
interface Pen {
  moveTo(p: Point): void;
  lineTo(p: Point): void;
  qCurveToOne(control: Point, end: Point): void;
  closePath(): void;
}

function draw(contours: Contour[], transform: Transform, pen: Pen): void {
  for (const { start, segments } of contours) {
    pen.moveTo(apply(transform, start));
    for (const segment of segments) {
      if (segment.kind === 'line') {
        pen.lineTo(apply(transform, segment.to));
        continue;
      }
      const points = segment.points.map((p) => apply(transform, p));
      for (let i = 0; i < points.length - 2; i++) {
        const [x, y] = points[i]!;
        const [nx, ny] = points[i + 1]!;
        pen.qCurveToOne(points[i]!, [0.5 * (x + nx), 0.5 * (y + ny)]);
      }
      pen.qCurveToOne(points.at(-2)!, points.at(-1)!);
    }
    pen.closePath();
  }
}

/** Python's `str(float)`: the shortest round-trip digits, always a point. */
function pyFloat(n: number): string {
  if (Object.is(n, -0)) return '-0.0';
  if (Number.isInteger(n) && Math.abs(n) < 1e16) return n.toFixed(1);
  const abs = Math.abs(n);
  if (abs < 1e-4 || abs >= 1e16) {
    // Python pads the exponent to two digits: 1e-05, not 1e-5.
    return n.toExponential().replace(/e([+-])(\d)$/, 'e$10$2');
  }
  return String(n);
}

/** fontTools' SVGPathPen, number for number. */
class SvgPathPen implements Pen {
  private readonly commands: string[] = [];
  private lastCommand: string | undefined;
  private last: Point | undefined;

  moveTo(p: Point): void {
    if (this.lastCommand === 'M') this.commands.pop();
    this.commands.push(`M${pyFloat(p[0])} ${pyFloat(p[1])}`);
    this.lastCommand = 'M';
    this.last = p;
  }

  lineTo(p: Point): void {
    const [x, y] = p;
    const last = this.last;
    if (last && x === last[0] && y === last[1]) return;
    let command: string | undefined;
    let points: string;
    if (last && x === last[0]) {
      command = 'V';
      points = pyFloat(y);
    } else if (last && y === last[1]) {
      command = 'H';
      points = pyFloat(x);
    } else if (this.lastCommand === 'M') {
      points = ` ${pyFloat(x)} ${pyFloat(y)}`;
    } else {
      command = 'L';
      points = `${pyFloat(x)} ${pyFloat(y)}`;
    }
    if (command) this.lastCommand = command;
    this.commands.push((command ?? '') + points);
    this.last = p;
  }

  qCurveToOne(control: Point, end: Point): void {
    this.commands.push(
      `Q${pyFloat(control[0])} ${pyFloat(control[1])} ${pyFloat(end[0])} ${pyFloat(end[1])}`,
    );
    this.lastCommand = 'Q';
    this.last = end;
  }

  closePath(): void {
    this.commands.push('Z');
    this.lastCommand = 'Z';
    this.last = undefined;
  }

  get d(): string {
    return this.commands.join('');
  }
}

type Bounds = [number, number, number, number];

/** fontTools' BoundsPen: the ink box, curve extrema included. */
class BoundsPen implements Pen {
  bounds: Bounds | undefined;
  private current: Point | undefined;

  private add([x, y]: Point): void {
    const b = this.bounds;
    this.bounds = b
      ? [Math.min(b[0], x), Math.min(b[1], y), Math.max(b[2], x), Math.max(b[3], y)]
      : [x, y, x, y];
  }

  moveTo(p: Point): void {
    this.add(p);
    this.current = p;
  }

  lineTo(p: Point): void {
    this.add(p);
    this.current = p;
  }

  qCurveToOne(control: Point, end: Point): void {
    this.add(end);
    const [xMin, yMin, xMax, yMax] = this.bounds!;
    const inside =
      xMin <= control[0] && control[0] <= xMax && yMin <= control[1] && control[1] <= yMax;
    if (!inside) {
      for (const p of quadraticExtrema(this.current!, control, end)) this.add(p);
    }
    this.current = end;
  }

  closePath(): void {
    this.current = undefined;
  }
}

/** fontTools' calcQuadraticBounds, as the points whose box it takes. */
function quadraticExtrema(start: Point, control: Point, end: Point): Point[] {
  const [cx, cy] = start;
  const bx = (control[0] - cx) * 2.0;
  const by = (control[1] - cy) * 2.0;
  const ax = end[0] - cx - bx;
  const ay = end[1] - cy - by;
  const roots: number[] = [];
  if (ax * 2.0 !== 0) roots.push(-bx / (ax * 2.0));
  if (ay * 2.0 !== 0) roots.push(-by / (ay * 2.0));
  return [
    ...roots
      .filter((t) => 0 <= t && t < 1)
      .map((t): Point => [ax * t * t + bx * t + cx, ay * t * t + by * t + cy]),
    start,
    end,
  ];
}

type Face = { font: hb.Font; upem: number };

async function openFace(): Promise<Face> {
  const sfnt = await decompress(readFileSync(FACE));
  const face = new hb.Face(new hb.Blob(sfnt));
  return { font: new hb.Font(face), upem: face.upem };
}

function glyphOf({ font }: Face, character: string): number {
  const glyph = font.nominalGlyph(character.codePointAt(0)!);
  if (glyph === undefined)
    throw new Error(`${relative(ROOT, FACE)} has no glyph for "${character}"`);
  return glyph;
}

type Placed = { glyph: number; dx: number; dy: number };

/** Shape `text`, position each glyph, and return the run with its ink box. */
function place(face: Face, text: string, tracking: number): { run: Placed[]; bounds: Bounds } {
  const buffer = new hb.Buffer();
  buffer.addText(text);
  buffer.guessSegmentProperties();
  hb.shape(face.font, buffer, [hb.Feature.fromString('kern')!, hb.Feature.fromString('liga')!]);

  const run: Placed[] = [];
  let penX = 0.0;
  const positions = buffer.getGlyphPositions();
  for (const [i, info] of buffer.getGlyphInfos().entries()) {
    const position = positions[i]!;
    run.push({ glyph: info.codepoint, dx: penX + position.xOffset, dy: position.yOffset });
    penX += position.xAdvance + tracking * face.upem;
  }

  const pen = new BoundsPen();
  for (const { glyph, dx, dy } of run) draw(outline(face.font, glyph), [1, 0, 0, 1, dx, dy], pen);
  return { run, bounds: pen.bounds! };
}

function svg(
  paths: Record<string, string>,
  width: number,
  height: number,
  label: string,
  note: string,
): string {
  const body = Object.entries(paths)
    .map(([id, d]) => `  <path id="${id}" d="${d}"/>`)
    .join('\n');
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${pyRound(width)} ${pyRound(height)}"` +
    ` role="img" aria-label="${label}">\n` +
    '  <!--\n' +
    '    GENERATED by scripts/render-brand-marks.sh — do not hand-edit.\n' +
    `    ${note}\n` +
    '    No colour here on purpose: `npm run tokens` writes the coloured copies.\n' +
    `  -->\n${body}\n</svg>\n`
  );
}

/** Python's `round()`: half to even. */
function pyRound(n: number): number {
  const floor = Math.floor(n);
  const fraction = n - floor;
  if (fraction !== 0.5) return Math.round(n);
  return floor % 2 === 0 ? floor : floor + 1;
}

function write(path: string, data: string | Uint8Array): void {
  mkdirSync(join(ROOT, path, '..'), { recursive: true });
  writeFileSync(join(ROOT, path), data);
}

function writeWordmark(face: Face): void {
  const period = glyphOf(face, '.');
  const {
    run,
    bounds: [xmin, ymin, xmax, ymax],
  } = place(face, 'Insolvia.', TRACKING);

  // The letters span the box's full width — only the height is padded.
  const width = xmax - xmin;
  const inkHeight = ymax - ymin;
  const height = width / WORDMARK.ratio;
  const left = 0.0;
  const top = (height - inkHeight) / 2; // centre the ink vertically

  const pens = { mark: new SvgPathPen(), dot: new SvgPathPen() };
  for (const { glyph, dx, dy } of run) {
    // translate the ink box to the origin, then flip y — fonts are y-up
    const pen = glyph === period ? pens.dot : pens.mark;
    draw(outline(face.font, glyph), [1, 0, 0, -1, dx - xmin + left, ymax + top + dy], pen);
  }

  write(
    'brand/wordmark.svg',
    svg(
      { mark: pens.mark.d, dot: pens.dot.d },
      width,
      height,
      'Insolvia',
      '"Insolvia." shaped, tracked, outlined and cropped to the ink. `mark` is the ' +
        "letters, `dot` the brass full stop — the same two roles the app's <Wordmark> uses.",
    ),
  );
  process.stdout.write(
    `  brand/wordmark.svg  ${pyRound(width)}x${pyRound(height)}  ${(width / height).toFixed(2)}:1\n`,
  );
}

/** The transform that centres the icon's letter on a `side` tile, y down. */
function iconGeometry(face: Face, side: number): { contours: Contour[]; transform: Transform } {
  const contours = outline(face.font, glyphOf(face, ICON.letter));
  const pen = new BoundsPen();
  draw(contours, [1, 0, 0, 1, 0, 0], pen);
  const [xmin, ymin, xmax, ymax] = pen.bounds!;

  const scale = (ICON.cap * side) / (ymax - ymin);
  const dx = (side - (xmax - xmin) * scale) / 2 - xmin * scale;
  const dy = (side + (ymax - ymin) * scale) / 2 + ymin * scale - ICON.rise * side;
  return { contours, transform: [scale, 0, 0, -scale, dx, dy] };
}

/** The rounded tile, as Python's f-string wrote it: `0` stays an int. */
function ground(side: number): string {
  const r = pyFloat(ICON.radius * side);
  const s = pyFloat(side);
  const far = pyFloat(side - ICON.radius * side);
  return (
    `M${r},0 H${far} A${r},${r} 0 0 1 ${s},${r} V${far} ` +
    `A${r},${r} 0 0 1 ${far},${s} H${r} A${r},${r} 0 0 1 0,${far} ` +
    `V${r} A${r},${r} 0 0 1 ${r},0 Z`
  );
}

function writeIcon(face: Face): void {
  const side = 512.0;
  const { contours, transform } = iconGeometry(face, side);
  const pen = new SvgPathPen();
  draw(contours, transform, pen);

  write(
    'brand/icon.svg',
    svg(
      { ground: ground(side), letter: pen.d },
      side,
      side,
      'Insolvia',
      'The "I" tile. `ground` is the dark chrome square, `letter` the ivory glyph — ' +
        'the icon is dark in both schemes, like the case rail, because it is chrome.',
    ),
  );
  process.stdout.write(`  brand/icon.svg  ${side}x${side}\n`);
}

/**
 * The icon tile as pixels, from the same glyph the SVG uses: a rounded ground
 * inset by `inset` on a transparent square, the letter on top. resvg draws
 * it, antialiased, at `size` pixels.
 */
function raster(
  face: Face,
  side: number,
  size: number,
  colours: { ground: string; letter: string },
  inset = 1.0,
): Buffer {
  const tile = pyRound(side * inset);
  const pad = Math.floor((side - tile) / 2);
  const { contours, transform } = iconGeometry(face, tile);
  const pen = new SvgPathPen();
  draw(contours, [transform[0], 0, 0, transform[3], transform[4] + pad, transform[5] + pad], pen);

  const document =
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${side} ${side}">` +
    `<rect x="${pad}" y="${pad}" width="${tile}" height="${tile}" rx="${ICON.radius * tile}" fill="${colours.ground}"/>` +
    `<path d="${pen.d}" fill="${colours.letter}"/></svg>`;
  return new Resvg(document, { fitTo: { mode: 'width', value: size } }).render().asPng();
}

/**
 * An .ico of PNG frames — what browsers have read since Vista, and smaller
 * than the bitmap kind. ICONDIR, one ICONDIRENTRY per frame, the PNGs.
 */
function ico(frames: ReadonlyArray<{ size: number; png: Buffer }>): Buffer {
  const header = Buffer.alloc(6 + 16 * frames.length);
  header.writeUInt16LE(0, 0); // reserved
  header.writeUInt16LE(1, 2); // type: icon
  header.writeUInt16LE(frames.length, 4);
  let offset = header.length;
  for (const [i, { size, png }] of frames.entries()) {
    const entry = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, entry);
    header.writeUInt8(size >= 256 ? 0 : size, entry + 1);
    header.writeUInt8(0, entry + 2); // no palette
    header.writeUInt8(0, entry + 3); // reserved
    header.writeUInt16LE(1, entry + 4); // colour planes
    header.writeUInt16LE(32, entry + 6); // bits per pixel
    header.writeUInt32LE(png.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  }
  return Buffer.concat([header, ...frames.map(({ png }) => png)]);
}

function writeRasters(face: Face): void {
  // The tile is chrome — dark in both schemes, like the case rail — so both
  // roles come from the DARK scheme whatever the viewer's browser is doing.
  const dark = palette(ROOT).dark;
  const colours = { ground: dark['bg']!, letter: dark['ink']! };

  const icons = 'apps/insolvia_app/public/icons';
  for (const size of [192, 512]) {
    write(`${icons}/Icon-${size}.png`, raster(face, size, size, colours));
    write(
      `${icons}/Icon-maskable-${size}.png`,
      raster(face, size, size, colours, ICON.maskable_safe),
    );
    process.stdout.write(`  ${icons}/Icon-${size}.png (+ maskable)\n`);
  }

  // Each frame drawn at its own size from the 256px tile's geometry, rather
  // than one large raster shrunk: a 16px letter rasterised at 16px is sharper
  // than one resampled down to it.
  const frames = [16, 32, 48].map((size) => ({ size, png: raster(face, 256, size, colours) }));
  write('apps/insolvia_app/public/favicon.ico', ico(frames));
  process.stdout.write('  apps/insolvia_app/public/favicon.ico  16/32/48\n');
}

async function main(): Promise<number> {
  if (!existsSync(FACE)) {
    process.stderr.write(`missing ${relative(ROOT, FACE)} — run ./scripts/fetch-brand-fonts.sh\n`);
    return 1;
  }

  process.stdout.write(`cutting marks from ${relative(ROOT, FACE)}\n`);
  const face = await openFace();
  writeWordmark(face);
  writeIcon(face);
  writeRasters(face);
  process.stdout.write('done. Run `npm run tokens` to colour the SVGs for each consumer.\n');
  return 0;
}

process.exitCode = await main();
