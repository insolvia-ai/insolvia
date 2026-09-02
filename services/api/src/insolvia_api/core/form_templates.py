"""The form template registry: loader and resolution for the `form/*` series.

One series per official form (`form/b101`, `form/b106i`, ...), each release a
committed directory under `src/insolvia_api/regulatory/form/` per the
regulatory release registry model (docs/reference/effective-dating.md;
docs/adr/0014-the-repository-is-the-regulatory-release-registry.md). A release
is one printed revision of one official form, and its payload is three files:

    template.pdf    the official fillable PDF, vendored VERBATIM from
                    uscourts.gov — ADR 0014 anticipates exactly this ("one PDF
                    template per form revision"). The manifest records the
                    upstream URL and sha256, and this loader re-hashes the
                    committed bytes against it, so the template a filing is
                    rendered on is provably the court's own artifact.
    spec.json       the issue-9.2 curated field spec for that revision — the
                    logical fields, their types, the exact radio export
                    states, and which PDF widgets each claims.
    acroform.json   the issue-9.2 AcroForm dump — machine-extracted ground
                    truth about every widget in template.pdf.

spec.json/acroform.json are copies of `forms/specs/` and `forms/acroform/`
taken at ingestion. `forms/` stays the curation workspace holding exactly the
CURRENT revision (checked by forms/scripts/check.py); the registry holds every
revision ever released, immutably, shipped inside this service's image so a
pinned case resolves its template forever. tests/test_form_templates.py
refuses drift between the two while they describe the same revision.

The loader re-validates what the FILL ENGINE's correctness depends on — every
fillable widget claimed by exactly one spec field, every claim resolving, a
radio's options exactly the PDF's export states — rather than trusting that
forms/scripts/check.py once ran over these bytes: a past release is never
re-checked by that script again, and a template the engine cannot trust is a
mis-filled court form. Vocabulary rules (entity names, mapping shapes) remain
check.py's job; this loader owns the widget-level contract only.

Resolution follows effective-dating.md exactly, as core/exemptions.py's does
(the first registry consumer; the mechanics are deliberately parallel):
`resolve` picks the release effective on the case's filing date, `get` returns
a pinned release forever, resolution before a series' earliest release refuses
rather than guessing, and `form_revisions_as_of` produces the pin map a case
records at packet assembly (`case.form_revisions` in case-data-model.md).

Stdlib only. The fill itself lives in core/form_fill.py, which is where the
pypdf dependency belongs; this module is pure configuration access.
"""

from __future__ import annotations

import hashlib
import json
import re
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import date
from functools import cache
from importlib import resources
from importlib.resources.abc import Traversable

# Mirrors forms/scripts/check.py's FIELD_TYPES — the closed set a spec field
# may declare. The checker owns the curation-side rule; this copy is what lets
# the loader refuse a release whose spec was never checked.
FIELD_TYPES = frozenset(
    {
        "text",
        "money",
        "integer",
        "date",
        "year",
        "state",
        "district",
        "ssn_itin",
        "ein",
        "phone",
        "email",
        "signature",
        "checkbox",
        "radio",
    }
)

# Widget kinds a spec field may claim, by field type — same split check.py
# enforces: button types claim button widgets, everything else claims
# free-entry widgets. Pushbuttons (print/reset chrome) are not fillable.
_BUTTON_KINDS = frozenset({"checkbox", "radio"})
_ENTRY_KINDS = frozenset({"text", "choice", "signature"})

_DIRNAME_RE = re.compile(r"^(\d{4}-\d{2}-\d{2})(?:\+(\d+))?$")


@dataclass(frozen=True)
class Widget:
    """One AcroForm field of the official PDF, as the dump recorded it.

    `states` is the button's export values (empty for text/choice widgets) —
    the exact names the PDF's appearance dictionaries declare, quirks and all
    (forms/README.md's defect catalogue: shared widgets, broken groups, the
    misspelled `Dentor 2`). `choice_options` is a choice widget's own option
    list (the district dropdown); `max_len` is the PDF's character cap where
    it declares one (the SSN last-4 boxes, the two-letter state boxes) — the
    fill engine refuses values the box would silently truncate."""

    name: str
    kind: str
    pages: tuple[int, ...]
    states: tuple[str, ...] = ()
    choice_options: tuple[str, ...] = ()
    max_len: int | None = None


@dataclass(frozen=True)
class OptionSpec:
    """One radio option: the PDF's exact export state, and the canonical
    enum value the projection layer maps it from (None where the spec
    recorded no canonical value)."""

    value: str
    maps_to_value: str | None = None


@dataclass(frozen=True)
class FieldSpec:
    """One logical field of the form, with its widget claims resolved.

    `pdf_names` is the resolved claim set in PRINTED ROW ORDER: an explicit
    `names` claim keeps the spec's curated order, and a pattern's hits sort
    by the `NN.M` row marker in their names where every hit carries one
    (dump order otherwise — see `_pattern_order` for why the dump cannot be
    trusted). A field claiming several PDF fields is a repetition (two
    debtor columns, table rows); the fill engine addresses those instances
    by PDF name."""

    id: str
    type: str
    label: str
    pdf_names: tuple[str, ...]
    part: int | None = None
    line: str | None = None
    options: tuple[OptionSpec, ...] = ()
    notes: str = ""


@dataclass(frozen=True)
class FormRelease:
    """One immutable release of a form series (effective-dating.md)."""

    series_id: str
    effective_date: date
    sequence: int
    source_url: str
    source_sha256: str
    notes: str
    form: str
    official_number: str
    title: str
    revision: str
    template_pdf: bytes
    fields: tuple[FieldSpec, ...]
    widgets: Mapping[str, Widget]

    @property
    def release_id(self) -> str:
        base = f"{self.series_id}@{self.effective_date.isoformat()}"
        return base if self.sequence == 1 else f"{base}+{self.sequence}"

    @property
    def pin(self) -> str:
        """The value `case.form_revisions` stores for this series —
        `effective_date[+sequence]`, per effective-dating.md."""
        base = self.effective_date.isoformat()
        return base if self.sequence == 1 else f"{base}+{self.sequence}"

    def field(self, field_id: str) -> FieldSpec:
        found = next((f for f in self.fields if f.id == field_id), None)
        if found is None:
            raise KeyError(f"{self.release_id} has no field {field_id!r}")
        return found


# --- Loading and validation --------------------------------------------------


def _fail(where: str, problem: str) -> ValueError:
    return ValueError(f"malformed form release {where}: {problem}")


def _load_json(node: Traversable, where: str) -> dict[str, object]:
    try:
        parsed = json.loads(node.read_text(encoding="utf-8"))
    except (FileNotFoundError, ValueError) as exc:
        raise _fail(where, f"{node.name}: {exc}") from exc
    if not isinstance(parsed, dict):
        raise _fail(where, f"{node.name} is not a JSON object")
    return parsed


def _str_field(data: Mapping[str, object], key: str, where: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise _fail(where, f"{key} missing or empty")
    return value


def _widgets(dump: Mapping[str, object], where: str) -> dict[str, Widget]:
    raw_fields = dump.get("fields")
    if not isinstance(raw_fields, list) or not raw_fields:
        raise _fail(where, "acroform.json fields missing or empty")
    widgets: dict[str, Widget] = {}
    for raw in raw_fields:
        if not isinstance(raw, dict):
            raise _fail(where, "acroform field is not an object")
        name = _str_field(raw, "name", where)
        if name in widgets:
            raise _fail(where, f"duplicate acroform field name {name!r}")
        kind = _str_field(raw, "kind", where)
        pages = raw.get("pages", [])
        states = raw.get("states", [])
        if not isinstance(pages, list) or not all(isinstance(p, int) for p in pages):
            raise _fail(where, f"{name!r}: pages must be a list of integers")
        if not isinstance(states, list) or not all(isinstance(s, str) for s in states):
            raise _fail(where, f"{name!r}: states must be a list of strings")
        choice_options = raw.get("options") or []
        if not isinstance(choice_options, list) or not all(
            isinstance(o, str) for o in choice_options
        ):
            raise _fail(where, f"{name!r}: options must be a list of strings")
        max_len = raw.get("max_len")
        if max_len is not None and (not isinstance(max_len, int) or max_len < 1):
            raise _fail(where, f"{name!r}: max_len must be a positive integer")
        widgets[name] = Widget(
            name=name,
            kind=kind,
            pages=tuple(pages),
            states=tuple(states),
            choice_options=tuple(choice_options),
            max_len=max_len,
        )
    return widgets


_ROW_MARKER_RE = re.compile(r"^(\d+)\.(\d+)\b")


def _pattern_order(hits: list[str], dump_order: Mapping[str, int]) -> list[str]:
    """Order a pattern's hits for `pdf_names`.

    A pattern cannot say which hit is which printed row, so the loader
    derives it: when every hit starts with the official forms' `NN.M` row
    marker ("17.1 Checking account"), the marker is the row identity —
    reliable where the dump's field order is not. Otherwise the dump order
    stands, which is the best remaining approximation.
    """
    markers = [_ROW_MARKER_RE.match(name) for name in hits]
    if all(markers):
        return sorted(
            hits,
            key=lambda name: tuple(
                int(part)
                for part in _ROW_MARKER_RE.match(name).groups()  # type: ignore[union-attr]
            ),
        )
    return sorted(hits, key=lambda name: dump_order[name])


def _options(raw: object, where: str, fid: str) -> tuple[OptionSpec, ...]:
    if raw is None:
        return ()
    if not isinstance(raw, list):
        raise _fail(where, f"{fid}: options must be a list")
    parsed: list[OptionSpec] = []
    for item in raw:
        if not isinstance(item, dict):
            raise _fail(where, f"{fid}: option is not an object")
        maps_to_value = item.get("maps_to_value")
        if maps_to_value is not None and not isinstance(maps_to_value, str):
            raise _fail(where, f"{fid}: maps_to_value must be a string")
        parsed.append(
            OptionSpec(
                value=_str_field(item, "value", where),
                maps_to_value=maps_to_value,
            )
        )
    return tuple(parsed)


def _field(
    raw: object,
    where: str,
    widgets: Mapping[str, Widget],
    dump_order: Mapping[str, int],
) -> FieldSpec:
    if not isinstance(raw, dict):
        raise _fail(where, "spec field is not an object")
    fid = _str_field(raw, "id", where)

    ftype = _str_field(raw, "type", where)
    if ftype not in FIELD_TYPES:
        raise _fail(where, f"{fid}: unknown type {ftype!r}")

    part = raw.get("part")
    if part is not None and not isinstance(part, int):
        raise _fail(where, f"{fid}: part must be an integer or null")
    line = raw.get("line")
    if line is not None and not isinstance(line, str):
        raise _fail(where, f"{fid}: line must be a string or null")
    notes = raw.get("notes", "")
    if not isinstance(notes, str):
        raise _fail(where, f"{fid}: notes must be a string")

    pdf = raw.get("pdf")
    if not isinstance(pdf, dict) or not pdf:
        raise _fail(where, f"{fid}: pdf claim block missing")
    claimed: list[str] = []
    names = pdf.get("names", [])
    if not isinstance(names, list):
        raise _fail(where, f"{fid}: pdf.names must be a list")
    for name in names:
        if not isinstance(name, str) or name not in widgets:
            raise _fail(where, f"{fid}: claims unknown PDF field {name!r}")
        claimed.append(name)
    pattern = pdf.get("pattern")
    if pattern is not None:
        if not isinstance(pattern, str):
            raise _fail(where, f"{fid}: pdf.pattern must be a string")
        rx = re.compile(pattern)
        hits = [n for n in widgets if rx.fullmatch(n)]
        if not hits:
            raise _fail(where, f"{fid}: pattern matched nothing: {pattern!r}")
        claimed.extend(_pattern_order(hits, dump_order))

    # `pdf_names` is the PRINTED ROW ORDER the projection layer fills by.
    # An explicit `names` claim keeps the spec's own order: those lists were
    # curated against the page's geometry (the spec notes record the
    # x-position analysis), and the AcroForm dump's field order is NOT
    # reading order — B106A/B's dump interleaves line 17's row names as
    # 17.1, 17.6, 17.7, …, so sorting by dump position (what this loader
    # once did) put the second deposit's name on row six.
    claimed_ordered = tuple(dict.fromkeys(claimed))
    if len(claimed_ordered) != len(claimed):
        raise _fail(where, f"{fid}: claims the same PDF field twice")

    kinds = {widgets[n].kind for n in claimed_ordered}
    allowed = _BUTTON_KINDS if ftype in _BUTTON_KINDS else _ENTRY_KINDS
    if not kinds <= allowed:
        raise _fail(
            where,
            f"{fid}: type {ftype} claims widget kinds {sorted(kinds)}",
        )

    options = _options(raw.get("options"), where, fid)
    if ftype == "radio":
        states = {s for n in claimed_ordered for s in widgets[n].states}
        declared = {o.value for o in options}
        if declared != states:
            raise _fail(
                where,
                f"{fid}: options {sorted(declared)} != "
                f"PDF export states {sorted(states)}",
            )

    return FieldSpec(
        id=fid,
        type=ftype,
        label=_str_field(raw, "label", where),
        pdf_names=claimed_ordered,
        part=part,
        line=line,
        options=options,
        notes=notes,
    )


def _load_release(release_dir: Traversable, series_id: str) -> FormRelease:
    where = f"{series_id}/{release_dir.name}"
    match = _DIRNAME_RE.match(release_dir.name)
    if match is None:
        raise _fail(where, "directory name is not <effective_date>[+<sequence>]")
    effective = date.fromisoformat(match.group(1))
    sequence = int(match.group(2)) if match.group(2) else 1
    if sequence < 1:
        raise _fail(where, "sequence must be >= 1")

    manifest = _load_json(release_dir.joinpath("manifest.json"), where)
    if manifest.get("series_id") != series_id:
        raise _fail(where, f"manifest series_id {manifest.get('series_id')!r}")
    if manifest.get("effective_date") != effective.isoformat():
        raise _fail(where, "manifest effective_date disagrees with the path")
    if manifest.get("sequence") != sequence:
        raise _fail(where, "manifest sequence disagrees with the path")
    source = manifest.get("source")
    if not isinstance(source, dict):
        raise _fail(where, "manifest source missing")
    source_sha256 = _str_field(source, "sha256", where)
    manifest_notes = manifest.get("notes")
    if not isinstance(manifest_notes, str) or not manifest_notes.strip():
        raise _fail(where, "manifest notes missing — say what this release is")

    # The template is the court's own artifact, provably: the committed bytes
    # must hash to the sha256 the manifest recorded from uscourts.gov.
    try:
        template_pdf = release_dir.joinpath("template.pdf").read_bytes()
    except FileNotFoundError as exc:
        raise _fail(where, "template.pdf missing") from exc
    actual = hashlib.sha256(template_pdf).hexdigest()
    if actual != source_sha256:
        raise _fail(
            where,
            f"template.pdf sha256 {actual} != manifest source.sha256 "
            f"{source_sha256} — the committed template is not the official PDF",
        )

    dump = _load_json(release_dir.joinpath("acroform.json"), where)
    dump_source = dump.get("source")
    if (
        not isinstance(dump_source, dict)
        or dump_source.get("pdf_sha256") != source_sha256
    ):
        raise _fail(
            where, "acroform.json source.pdf_sha256 disagrees with the manifest"
        )
    widgets = _widgets(dump, where)
    dump_order = {name: i for i, name in enumerate(widgets)}

    spec = _load_json(release_dir.joinpath("spec.json"), where)
    form = _str_field(spec, "form", where)
    if form != series_id.removeprefix("form/"):
        raise _fail(where, f"spec form {form!r} disagrees with the series id")
    if spec.get("form") != dump.get("form"):
        raise _fail(where, "spec form disagrees with acroform form")
    revision = _str_field(spec, "revision", where)
    if revision != dump.get("revision"):
        raise _fail(where, "spec revision disagrees with acroform revision")
    for doc, doc_name in ((spec, "spec"), (dump, "acroform")):
        if doc.get("effective_date") != effective.isoformat():
            raise _fail(where, f"{doc_name} effective_date disagrees with the path")

    raw_fields = spec.get("fields")
    if not isinstance(raw_fields, list) or not raw_fields:
        raise _fail(where, "spec fields missing or empty")
    fields: list[FieldSpec] = []
    seen: set[str] = set()
    claimed_by: dict[str, str] = {}
    for raw in raw_fields:
        parsed = _field(raw, where, widgets, dump_order)
        if parsed.id in seen:
            raise _fail(where, f"duplicate field id {parsed.id}")
        seen.add(parsed.id)
        for name in parsed.pdf_names:
            if name in claimed_by:
                raise _fail(
                    where,
                    f"PDF field {name!r} claimed by both "
                    f"{claimed_by[name]} and {parsed.id}",
                )
            claimed_by[name] = parsed.id
        fields.append(parsed)

    # Full coverage: a fillable widget no field claims is a box the engine
    # could never fill — the executable form of "no missing fields".
    fillable = {n for n, w in widgets.items() if w.kind != "pushbutton"}
    uncovered = sorted(fillable - set(claimed_by))
    if uncovered:
        raise _fail(
            where,
            f"{len(uncovered)} fillable PDF fields not claimed by any spec "
            f"field: {uncovered[:5]}",
        )

    return FormRelease(
        series_id=series_id,
        effective_date=effective,
        sequence=sequence,
        source_url=_str_field(source, "url", where),
        source_sha256=source_sha256,
        notes=manifest_notes,
        form=form,
        official_number=_str_field(spec, "official_number", where),
        title=_str_field(spec, "title", where),
        revision=revision,
        template_pdf=template_pdf,
        fields=tuple(fields),
        widgets=widgets,
    )


def load_form_registry(root: Traversable) -> dict[str, tuple[FormRelease, ...]]:
    """Load and validate every form release under a registry root.

    Raises ValueError on any malformed release — tests/test_form_templates.py
    runs this over the committed registry, so a bad release fails the pull
    request that would have introduced it, not a filing.
    """
    form_dir = root.joinpath("form")
    series: dict[str, tuple[FormRelease, ...]] = {}
    for series_dir in sorted(form_dir.iterdir(), key=lambda n: n.name):
        if not series_dir.is_dir():
            continue
        series_id = f"form/{series_dir.name}"
        loaded = [
            _load_release(release_dir, series_id)
            for release_dir in sorted(series_dir.iterdir(), key=lambda n: n.name)
            if release_dir.is_dir()
        ]
        if not loaded:
            raise _fail(series_id, "series has no releases")
        loaded.sort(key=lambda r: (r.effective_date, r.sequence))
        ids = [r.release_id for r in loaded]
        if len(ids) != len(set(ids)):
            raise _fail(series_id, "duplicate release ids")
        series[series_id] = tuple(loaded)
    if not series:
        raise ValueError("the form template registry is empty")
    return series


@cache
def form_registry() -> dict[str, tuple[FormRelease, ...]]:
    """The committed registry, shipped inside this package (ADR 0014)."""
    return load_form_registry(resources.files("insolvia_api").joinpath("regulatory"))


# --- Resolution (effective-dating.md) ----------------------------------------


def form_series_ids() -> tuple[str, ...]:
    return tuple(sorted(form_registry()))


def form_releases(series_id: str) -> tuple[FormRelease, ...]:
    found = form_registry().get(series_id)
    if found is None:
        raise KeyError(f"unknown series {series_id!r}")
    return found


def pick_form_release(candidates: tuple[FormRelease, ...], as_of: date) -> FormRelease:
    """The release with the greatest effective_date <= as_of, ties broken by
    highest sequence. Pure over its inputs, so correction tie-breaks are
    testable without fixture directories."""
    applicable = [r for r in candidates if r.effective_date <= as_of]
    if not applicable:
        earliest = min(r.effective_date for r in candidates)
        raise LookupError(
            f"no release of {candidates[0].series_id} is effective on or "
            f"before {as_of.isoformat()} (series begins {earliest.isoformat()}); "
            "refusing to render a form revision that does not describe that date"
        )
    return max(applicable, key=lambda r: (r.effective_date, r.sequence))


def resolve_form(series_id: str, as_of: date) -> FormRelease:
    """The revision in force on `as_of` — the case's filing date while a case
    floats, its pinned assembly date afterwards."""
    return pick_form_release(form_releases(series_id), as_of)


def get_form(series_id: str, release_id: str) -> FormRelease:
    """That exact release — must succeed forever for any id ever pinned."""
    for release in form_releases(series_id):
        if release.release_id == release_id:
            return release
    raise KeyError(f"series {series_id!r} has no release {release_id!r}")


def latest_form(series_id: str) -> FormRelease:
    """Newest by (effective_date, sequence), even if still in the future."""
    return form_releases(series_id)[-1]


def form_revisions_as_of(as_of: date) -> dict[str, str]:
    """The pin map packet assembly records on the case: every form series
    resolved as of the assembly date, keyed exactly as `case.form_revisions`
    stores it (series id -> `effective_date[+sequence]`)."""
    return {
        series_id: resolve_form(series_id, as_of).pin for series_id in form_series_ids()
    }
