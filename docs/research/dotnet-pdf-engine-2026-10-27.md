# .NET PDF engine: can PDFsharp do what pypdf does for us? (2026-10-27)

This note answers spike [#514](https://github.com/insolvia-ai/insolvia/issues/514),
plan item 18.3 of the .NET and Postgres rewrite. ADR 0028 § Libraries names
PDFsharp 6 as the candidate PDF engine and sets the bar: B101, a flat
overlay form (B2010) and the radio and shared-widget quirks must be
field-equivalent to pypdf's output before the forms port (18.16) starts.

**This is a snapshot. It owns no fact.** The engine's behaviour is owned by
`services/api/src/insolvia_api/core/form_fill.py` and its goldens
(`services/api/tests/unit/test_form_fill.py`). The library decision is owned
by ADR 0028. Checked against `main` at `3e288fa` with pypdf 6.16.2, PDFsharp
6.2.4 and .NET SDK 10.0.302, on an Apple M5 (arm64).

---

## Verdict

**Yes, with one condition about the licence.** PDFsharp 6.2.4 reproduces
the fill engine. The results:

- All 22 forms and the three special paths come out field-equivalent.
- Every text appearance stream is byte-identical to pypdf's.
- The renders differ by 0.0000% of pixels.
- The .NET fill is 4 to 18 times faster.

The condition: **6.2.4 is the last MIT release.** PDFsharp 6.5.0 and
7.0.0-preview-2 were published on 2026-10-14 under AGPL-3.0-only or a
commercial licence. ADR 0028 rejected iText for exactly that licence.
The premise "PDFsharp 6 (MIT)" therefore holds only for a pinned 6.2.x,
and that line receives no further releases.

PDFsharp is used here as a **PDF object model**: open, walk dictionaries,
replace streams, save. The appearance streams come from a ~150-line port
of pypdf's own generator, not from PDFsharp. PDFsharp's high-level AcroForm
API cannot produce equivalent output (§ Gaps).

## Method

1. **Same input both sides.** `backend/spikes/pdf/python/dump_and_fill.py`
   imports the goldens' own fixture, `synthetic_values`. It runs it through
   `form_fill._per_widget`, the engine's validation and flattening, and
   writes the PDF-level instructions per case to `out/inputs/<case>.json`:
   - text values
   - button export states
   - direct widget states
   - overlay boxes
   - page stamps

   It then renders each case with today's engine (`fill_form`, and
   `form_overlay.stamp_pages` for stamps). Fixtures are the existing
   synthetic ones; there is no real data.
2. **The .NET side** (`backend/spikes/pdf/Program.cs`) applies those
   instructions to the same vendored `template.pdf` with PDFsharp.
3. **The checker** (`backend/spikes/pdf/python/compare.py`) reads both
   outputs back with pypdf and compares the following.
   - **fields:** every field node's `/FT`, plus the value *and PDF type* of `/V`.
   - **widget `/AS`:** every button widget's appearance state, in page order.
   - **text APs:** every text or choice widget's `/AP /N`, comparing the
     decoded stream bytes and the normalised dictionary.
   - **official pages:** each page's content streams, byte for byte, with the
     engine's overlay stream split out.
   - **text runs:** every text-showing operator as (page, text, x, y, size)
     after the CTM. This is how overlay boxes and stamps are positioned.
   - **goldens reader:** the goldens' own `read_form`, run over the *.NET*
     output and compared with the committed golden JSON.
   - **pixels:** both files rendered by PDFium (pypdfium2 5.14 / PDFium
     156.0.8076) at 144 dpi, with the form environment initialised. Reports
     the share of pixels that differ at all, the maximum over pages. The
     template-to-Python figure beside it shows the metric can see a fill.
     Without `init_forms`, PDFium skips widgets, and both sides would render
     as blank forms.

## Results

25 cases are checked:

- the 22 forms with full-coverage synthetic values
- `b101-widget-states`: the `WidgetStates(states=…)` escape hatch
- `b106d-broken-group`: `WidgetStates(indexes=…)` on B106D row 2.4, the real
  all-`On` defect
- `b2010-stamped`: the flat notice with the DRAFT watermark and print-date
  stamp, which is the overlay that B2010 actually receives

Across all 25 cases:

- 3,059 fields and 1,170 button widget states
- 2,436 text appearance streams and 13,081 text runs
- 115 rendered pages

| case | fields | widget /AS | text APs byte-equal | official pages | text runs | goldens reader | max px diff | (template→py) | ms py / .NET |
|---|---|---|---|---|---|---|---|---|---|
| **b101** | 159/159 | 103/103 | 143/143 | equal | 1667/1667 equal | pass | 0.0000% | (3.54%) | 175.4 / 40.1 |
| b101-widget-states | 159/159 | 103/103 | 2/2 | equal | 1667/1667 equal | — | 0.0000% | (0.03%) | 136.8 / 36.6 |
| **b106ab** (radio-heaviest) | 368/368 | 170/170 | 294/294 | equal | 1138/1138 equal | pass | 0.0000% | (2.99%) | 311.7 / 36.8 |
| b106c | 104/104 | 41/41 | 84/84 | equal | 355/355 equal | pass | 0.0000% | (4.40%) | 113.4 / 19.9 |
| b106d | 186/186 | 74/74 | 139/139 | equal | 387/387 equal | pass | 0.0000% | (3.21%) | 109.8 / 11.5 |
| b106d-broken-group | 186/186 | 74/74 | 117/117 | equal | 387/387 equal | — | 0.0000% | (0.02%) | 72.0 / 9.9 |
| b106dec | 15/15 | 7/7 | 9/9 | equal | 39/39 equal | pass | 0.0000% | (0.75%) | 15.8 / 1.1 |
| b106ef | 323/323 | 177/177 | 211/211 | equal | 823/823 equal | pass | 0.0000% | (2.92%) | 220.8 / 16.0 |
| b106g | 99/99 | 7/7 | 95/95 | equal | 140/140 equal | pass | 0.0000% | (4.80%) | 41.0 / 3.6 |
| b106h | 150/150 | 44/44 | 111/111 | equal | 226/226 equal | pass | 0.0000% | (4.61%) | 68.5 / 5.0 |
| b106i | 85/85 | 12/12 | 81/81 | equal | 268/268 equal | pass | 0.0000% | (2.47%) | 35.3 / 3.9 |
| b106j | 79/79 | 26/26 | 68/68 | equal | 261/261 equal | pass | 0.0000% | (2.04%) | 37.5 / 3.9 |
| b106j2 | 73/73 | 24/24 | 65/65 | equal | 246/246 equal | pass | 0.0000% | (2.05%) | 38.0 / 4.1 |
| b106sum | 28/28 | 9/9 | 23/23 | equal | 143/143 equal | pass | 0.0000% | (0.75%) | 19.6 / 1.1 |
| b107 | 535/535 | 139/139 | 467/467 | equal | 1167/1167 equal | pass | 0.0000% | (3.22%) | 450.0 / 25.2 |
| b108 | 58/58 | 43/43 | 40/40 | equal | 222/222 equal | pass | 0.0000% | (2.17%) | 37.6 / 3.8 |
| b113 (flat, 160 boxes) | 0/0 | 0/0 | 0/0 | equal | 793/793 equal | pass | 0.0000% | (1.08%) | 35.4 / 4.0 |
| b121 | 27/27 | 7/7 | 20/20 | equal | 85/85 equal | pass | 0.0000% | (1.40%) | 11.8 / 0.7 |
| b122a1 | 58/58 | 15/15 | 58/58 | equal | 306/306 equal | pass | 0.0000% | (1.24%) | 77.9 / 8.9 |
| b122a2 | 146/146 | 37/37 | 165/165 | equal | 811/811 equal | pass | 0.0000% | (1.84%) | 160.7 / 13.5 |
| b122c1 | 71/71 | 22/22 | 77/77 | equal | 409/409 equal | pass | 0.0000% | (1.64%) | 62.2 / 9.0 |
| b122c2 | 150/150 | 36/36 | 167/167 | equal | 791/791 equal | pass | 0.0000% | (1.90%) | 164.2 / 14.6 |
| b2010 (verbatim) | 0/0 | 0/0 | 0/0 | equal | 327/327 equal | pass | 0.0000% | (0.00%) | 0 / 0 |
| **b2010-stamped** | 0/0 | 0/0 | 0/0 | equal | 335/335 equal | — | 0.0000% | (1.74%) | 9.5 / 1.1 |
| b2030 (flat, 48 boxes) | 0/0 | 0/0 | 0/0 | equal | 88/88 equal | pass | 0.0000% | (3.44%) | 3.5 / 0.3 |

Timings are medians of 15 in-process runs. Each run starts from the template
bytes and ends at the output bytes, so it includes parse, fill and
serialise. Both sides were warm, with the .NET JIT already done. Lambda cold
start was not measured. The .NET output is deterministic: two fills give
identical bytes once the two save-time nondeterminisms below are pinned.
The outputs are not byte-identical to pypdf's, as ADR 0028 expects: the
serialisers differ, and files are 3 to 10% smaller.

## What it took to match

These are pypdf 6.16.2's observable behaviours that the port reproduces on
purpose. Each needs a keep-or-correct decision in 18.16:

- **Text appearance streams.** The port follows pypdf's
  `TextStreamAppearance`:
  - It handles the `/DA` parse, margins, `/Q` alignment, multiline wrap and
    shrink, comb cells, and auto-size when the size is 0.
  - It prints numbers the way Python's `str(float)` does
    (`16.544000000000096`, `8.0`, int `2` against float `2.0`). That is
    what makes the streams byte-equal.
  - It applies the court PDFs' quirk that a comb field with `/DA … 0 Tf` takes
    the auto-size branch, not the comb branch. This affects the SSN last-4
    and state boxes.
- **A button parent's `/V` is a text string.** pypdf writes `(/Chapter 7)`,
  not `/Chapter 7`, on a group's parent, and a name on each kid widget.
  Readers that compare `str(/V)` cannot tell the difference, and the
  checker compares the type too. The spec calls for a name, so 18.16 may
  correct this. It is a one-line change, and the goldens would move with it.
- **A shared `/AP /N` is overwritten in place.** The court PDFs give all of
  a field's widgets one `/N` stream; B106D's `Debtor 1` caption spans three
  pages. pypdf replaces that one object, so the last widget's box size wins
  for all of them. The first port gave each widget its own stream. That is
  arguably more correct, but it differed by 0.018% of pixels on B106D.
  The spike now mirrors pypdf.
- **The overwrite rules.**
  - `NeedAppearances` is set to `false`.
  - `/I` is removed from a choice field.
  - On the `/AP /N` replacement, `/Matrix` and `/FormType` are copied from
    the old stream, and `/Resources /Font` points at the same indirect
    `/DR /Helv` reference.
- **The overlay and stamp streams** are byte-for-byte the same bytes as
  `form_fill._overlay_stream` and `pdf_draw.text_ops`. This includes
  pypdf's rule for joining an array `/Contents` before stamping: each part
  gets a trailing newline unless it already ends in one.

## Gaps and their workarounds

| Gap | Effect | Workaround |
|---|---|---|
| **Licence: PDFsharp ≥ 6.5 is AGPL-3.0-only or commercial** (NuGet `licenseExpression`; docs.pdfsharp.net news, 2026-10-13/14) | Floating the version would pull AGPL code into the product | Pin `PDFsharp` **6.2.4** (MIT). The surface used is small (reader, `PdfDictionary`/`PdfArray`/`PdfReference`, `CreateStream`, `Save`), so carrying a fork of the MIT source is feasible if a bug fix is ever needed. The other option is buying the commercial licence. |
| **The high-level AcroForm API** (`PdfTextField.Text`, `PdfCheckBoxField.Checked`) | On B101, 152 of 155 fields threw "No appropriate font found for family name 'Courier New'. Implement IFontResolver". With a resolver it draws through `XGraphics` with an embedded TrueType font, a different appearance from pypdf's. It has no "select radio by export state" call, and no widget-state-only path for broken groups. | Do not use it. Port the appearance generator, as here. `--highlevel` in the spike reproduces the failure. |
| **PDFsharp nondeterminism on save** | Every save writes a fresh second `/ID` element and a regenerated XMP packet with random `xmpMM:DocumentID`/`InstanceID` GUIDs. It also rewrites `/Producer` and `/ModDate`. There is no option to turn this off in 6.2.4. | Pin `/ModDate`, set the second `/ID` to the first, and overwrite the two `uuid:` values in place with GUIDs derived from the template's hash. The packet is uncompressed and the values keep the same length, so xref offsets stay valid. `Determinism` in `Program.cs` does this. A fork could stop the regeneration properly. |
| **Header version** | pypdf writes `%PDF-1.3` over PDF-1.6 content; PDFsharp keeps `%PDF-1.6` | None needed. PDFsharp's behaviour is the correct one. |
| **Transitive dependency** `System.Security.Cryptography.Pkcs` 8.0.1 | NU1903, a high-severity advisory (GHSA-846f-f764-76mx) | A direct reference to 10.0.14 lifts it. |
| **Not ported** | Non-ASCII text: the `/DR /Helv` `/Differences` encoding and its cp1252 widths. List-box choice fields. Rotated widgets (`/MK /R`). Fonts other than Helvetica. No court form or synthetic fixture exercises them, and the port throws `NotSupportedException` rather than guessing. | 18.16 ports pypdf's encoding map. A debtor named "José" needs it, and the goldens should gain a non-ASCII case first. |
| **One renderer** | Pixel diffs come from PDFium only (no poppler or MuPDF here) | Byte-equal appearance streams over byte-equal page content leave nothing for another renderer to draw differently. |

## Alternatives, in case the MIT pin becomes untenable

None was needed, so none was built.

- **PdfPig** (Apache-2.0) reads well, and its `PdfDocumentBuilder` copies
  pages. It has no in-place AcroForm write and no field appearance support.
  It could serve as the checker's reader, but not as the filler.
- **QuestPDF** lays out new documents and is not a filler. Its licence is
  community/commercial.
- **PDFium** (BSD-3/Apache-2.0) through a .NET binding can fill and save
  forms, but it brings a native library into the Lambda image. It also
  generates its own appearances, which would not match pypdf.
- The next-cheapest path is the object model PDFsharp 6.2.4 already provides
  here, in a fork.

## Recommendation for 18.16

1. **Build `Insolvia.Forms`' engine on PDFsharp 6.2.4, pinned exactly**,
   with a comment that names the AGPL relicensing. Use the object-model
   approach from this spike, not the AcroForm API.
2. **Port the appearance generator as a pure function** with its own unit
   tests. Python-style number formatting is part of its contract while
   parity is being proven.
3. **Keep this spike's checker as 18.16's parity harness.** Run it in CI
   against pypdf's goldens until cut-over, then retire it. After cut-over,
   the .NET goldens are field-level (ADR 0028), and `compare.py`'s read-back
   is the shape to port.
4. **Decide the two pypdf quirks at the cut-over, not before.** These are
   the text-string `/V` on button parents and the shared-`/N` overwrite.
   Correcting either is a deliberate golden change.
5. **Amend ADR 0028's "PDFsharp 6 (MIT)" premise** to "PDFsharp 6.2.4,
   the last MIT release, pinned". This note owns no decision.

## Reproduce

From the repo root, in a hand-made venv. Do not run
`services/api/scripts/dev-setup.sh`, because it touches AWS:

```bash
python3.12 -m venv /tmp/pdfspike-venv
(cd services/api && /tmp/pdfspike-venv/bin/pip install -r requirements.txt pypdfium2 pillow pytest)
PYTHONPATH=services/api/src /tmp/pdfspike-venv/bin/python backend/spikes/pdf/python/dump_and_fill.py backend/spikes/pdf/out
(cd backend/spikes/pdf && dotnet run -c Release -- ../../.. out 15)
PYTHONPATH=services/api/src /tmp/pdfspike-venv/bin/python backend/spikes/pdf/python/compare.py backend/spikes/pdf/out
# the high-level API's failure:
(cd backend/spikes/pdf && dotnet run -c Release -- --highlevel ../../.. out)
```

The `requirements.txt` install brings in `insolvia_core` through its relative
path, so it has to run from `services/api`. The outputs, PDFs and
`report.json` land in `backend/spikes/pdf/out/`, which is gitignored.
