# Insolvia.Forms

The PDF engine of the .NET backend (plan 18.16, [ADR 0028 § Libraries](../../../docs/adr/0028-backend-moves-to-dotnet-and-postgres.md)):
it fills what `Insolvia.Engines` projects into the official court templates,
draws the flat forms, stamps and measures the output, and assembles the
packet, the filing set and the approval digest. Pure — bytes in, bytes out;
storing a packet is the worker's job (18.17).

| Python (`services/api/src/insolvia_api/core/`) | C# | Tests |
|---|---|---|
| `form_fill.py` | `FormFill`, `Pdf/TextAppearance`, `Pdf/Fonts`, `Pdf/PyNum`, `Pdf/Determinism` | `FormFillTests`, `GoldenTests`, `AppearanceTests` |
| `form_templates.py` (the `template.pdf` half) | `TemplatePdfs`, `Templates/**` | `TemplateFileTests` |
| `form_overlay.py` | `FormOverlay` | `FormOverlayTests` |
| `pdf_draw.py` | `PdfDraw` | `FormOverlayTests`, `AmendmentCoverSheetTests` |
| `pdf_measure.py` | `PdfMeasure` | `PdfMeasureTests` |
| `packets.py` | `Packets` | `PacketTests` |
| `packet_assembly.py` | `PacketAssembly`, `CaseData` | `PacketAssemblyTests` |
| `amendment_cover_sheet.py` | `AmendmentCoverSheet` | `AmendmentCoverSheetTests` |
| `filing_set.py` | `FilingSets` | `FilingSetTests` |
| `filing_approval.py` (the basis and its digest) | `ApprovalDigest` | `ApprovalDigestTests` |
| `case_summary.py` | `CaseSummaries` | `CaseSummaryTests` |
| `forms_hub.py` | `FormsHub` | `FormsHubTests` |

## PDFsharp is pinned at 6.2.4, and used only as an object model

6.2.4 is the last MIT release; 6.5.0 and later are AGPL-3.0-only or
commercial. Upgrading is a licensing decision, never a dependency bump
(`Directory.Packages.props` pins it with `[6.2.4]`). Its AcroForm API is not
used: the appearance streams are a port of pypdf 6.16.2's
`TextStreamAppearance`, and PDFsharp only opens, edits dictionaries and
saves. PDFsharp stamps every save with a random second `/ID`, random XMP
GUIDs and the clock; `Pdf/Determinism` pins all three, so the same input
gives the same bytes (`GoldenTests` fills every form twice).

## pypdf's quirks — what was kept and what was corrected

pypdf 6.16.2 is the oracle until cut-over (18.22). Three of its behaviours
were decided on explicitly:

1. **A button field's `/V` — corrected.** pypdf writes a radio group's (or
   checkbox's) parent `/V` as a *text string* `(/Chapter 7)`; PDF 32000
   § 12.7.4.2 says a *name*. The .NET engine writes the name. Readers that
   compare `str(/V)` — the goldens' own reader among them — see no
   difference, the widgets' `/AS` (what prints) are untouched, and the
   rendered pixels are identical; `Parity/compare.py` counts this one
   difference as "quirk 1" and fails on any other.
2. **The shared `/AP /N` overwrite — kept.** The court PDFs give all of a
   field's widgets one `/N` stream (B106D's "Debtor 1" caption spans three
   pages), and pypdf replaces that one object, so the last widget's box size
   wins for all of them. Giving each widget its own stream is arguably more
   correct, but it changes 0.018 % of B106D's pixels and the object graph
   the court authored. The difference is sub-point; the port keeps pypdf's
   behaviour so the parity gate stays at zero, and a correction is a later,
   deliberate golden change.
3. **Text the field's font cannot encode — refused (new).** pypdf, given a
   character outside the `/DR` font's encoding (the court `/Helv` covers
   all of cp1252, so this means e.g. `ő`, `ą`, `Ș`), tries other code pages
   in a Python *set*'s iteration order — which `PYTHONHASHSEED` randomises
   per process, so the same input could render two ways — and otherwise
   prints `?`. The .NET engine refuses with a `FormFillException` naming the
   field and the characters, the same rule the overlay already applies
   ("a character Helvetica cannot draw"). Everything the font's encoding
   carries — accents, `ñ`, `ß`, `€`, curly quotes, `—` — fills exactly as
   pypdf fills it (`b101-non-ascii`, `b106i-non-ascii` in the parity run).

What the court forms never exercise is still ported as pypdf does it, and
pinned against pypdf's own output by `AppearanceTests` over
`Fixtures/edge.pdf`: list boxes (options joined, the selection boxed), a
rotated widget (`/MK /R` as a `/Matrix`), a page `/Rotate`, any simple font
in `/DR` (the 14 core fonts by pypdf's metrics, any other by its `/Widths`),
and a `/DA` font missing from `/DR` (a core font by name, else Helvetica,
added to `/DR` with the `/DA` rewritten). Composite (Type0) and Type3 fonts,
a `/ToUnicode` map and `[export, display]` list options are refused, as
pypdf cannot fill them either.

## The goldens and the parity gate

- `tests/Insolvia.Forms.Tests/Goldens/<form>.json` — the .NET goldens:
  release, the sha256 of the .NET output (re-baselined from pypdf's, as ADR
  0028 accepts), and the field read-back. `GoldenTests` also holds the
  read-back to the Python goldens' (`services/api/tests/unit/goldens/`).
  Regenerate with `UPDATE_FORM_GOLDENS=1 dotnet test --filter GoldenTests`
  and review the diff field by field.
- `tests/Insolvia.Forms.Tests/Parity/run.sh` — the gate against pypdf:
  every form, the special paths, non-ASCII text and the packet assemblies of
  the four parity case files, compared on fields, widget states,
  appearance-stream bytes, official pages, text runs and PDFium pixels.
  CI runs it (`backend-pr.yml`, "Forms parity") until cut-over.
