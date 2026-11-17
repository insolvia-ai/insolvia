"""The statutory dollar amounts: loader and resolution for `code/dollar-amounts`.

The Bankruptcy Code names dollar figures that the forms print and the means
test compares against — the § 707(b)(2) presumption thresholds, the § 707(b)
median add-ons, B107 question 6's § 547(c) payment floors. They are values
with effective dates, not constants in code (case-data-model.md, "Statutory
constants are configuration"): § 104(b) adjusts most of them every third
April 1, and a case records which set applied via `constants_set_id` — the
pinned release id of this series, written by packet assembly in the same
operation as the packet (effective-dating.md, "Float, then pin").

The registry (docs/adr/0014) holds the releases as committed directories
under `src/insolvia_api/regulatory/code/dollar-amounts/`, each a
`manifest.json` plus an `amounts.json` payload of named figures. The § 522
figures from the same Federal Register notices live in `exemptions/federal`
— one owner per figure; this series carries what the means test and the
SOFA read.

The rules are core/exemptions.py's, deliberately parallel (it is the first
registry consumer and the pattern-setter): every figure is a two-decimal
money string, cited and tiered — never UNVERIFIED — and resolution follows
effective-dating.md exactly, refusing dates before the earliest release.
tests/test_dollar_amounts.py runs the loader over the committed registry so
a malformed or unverified figure fails the pull request, not a filing.

Stdlib only; reading the registry shipped inside this package is
configuration access, not an external dependency.
"""

from __future__ import annotations

import json
import re
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import date
from decimal import Decimal
from functools import cache
from importlib import resources
from importlib.resources.abc import Traversable

from .exemptions import Source, Verification

# The next scheduled § 104(b) adjustment. The regulatory source register owns
# the calendar; this constant exists so a test can refuse to let the dataset
# outlive its own review date silently (core/exemptions.py's pattern).
NEXT_SECTION_104_ADJUSTMENT = date(2028, 4, 1)

DOLLAR_AMOUNTS_SERIES = "code/dollar-amounts"

_MONEY_RE = re.compile(r"^[1-9]\d*\.\d{2}$")
_DIRNAME_RE = re.compile(r"^(\d{4}-\d{2}-\d{2})(?:\+(\d+))?$")


@dataclass(frozen=True)
class DollarAmount:
    """One named statutory figure — what a form floor or a means-test
    threshold cites. `figure_since` is when the figure took force where that
    was verified; None means the figure is confirmed current but its own
    amendment date was not separately verified. `next_adjustment` is None for
    figures § 104 never touches (§ 547(c)(8)'s $600 is statutory text)."""

    amount_id: str
    citation: str
    description: str
    amount: str
    verification: Verification
    sources: tuple[Source, ...]
    figure_since: date | None
    next_adjustment: date | None
    notes: str = ""

    @property
    def value(self) -> Decimal:
        return Decimal(self.amount)


@dataclass(frozen=True)
class Release:
    """One immutable release of the series (effective-dating.md). Its
    release_id is what `case.constants_set_id` stores."""

    series_id: str
    effective_date: date
    sequence: int
    source_url: str
    source_published: date | None
    source_sha256: str | None
    notes: str
    amounts: tuple[DollarAmount, ...]

    @property
    def release_id(self) -> str:
        base = f"{self.series_id}@{self.effective_date.isoformat()}"
        return base if self.sequence == 1 else f"{base}+{self.sequence}"

    def amount(self, amount_id: str) -> DollarAmount:
        found = next((a for a in self.amounts if a.amount_id == amount_id), None)
        if found is None:
            raise KeyError(f"{self.release_id} has no amount {amount_id!r}")
        return found


# --- Loading and validation --------------------------------------------------


def _fail(where: str, problem: str) -> ValueError:
    return ValueError(f"malformed release {where}: {problem}")


def _str_field(data: Mapping[str, object], key: str, where: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise _fail(where, f"{key} missing or empty")
    return value


def _date_or_none(value: object, where: str, field_name: str) -> date | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise _fail(where, f"{field_name} {value!r} is not a date string")
    try:
        return date.fromisoformat(value)
    except ValueError as exc:
        raise _fail(where, f"{field_name} {value!r}: {exc}") from exc


def _sources(value: object, where: str) -> tuple[Source, ...]:
    if not isinstance(value, list) or not value:
        raise _fail(where, "sources missing or empty")
    parsed: list[Source] = []
    for raw in value:
        if not isinstance(raw, dict):
            raise _fail(where, "source is not an object")
        accessed = _date_or_none(raw.get("accessed"), where, "source.accessed")
        if accessed is None:
            raise _fail(where, "source.accessed missing")
        parsed.append(
            Source(
                title=_str_field(raw, "title", where),
                url=_str_field(raw, "url", where),
                accessed=accessed,
            )
        )
    return tuple(parsed)


def _amount(raw: object, where: str) -> DollarAmount:
    if not isinstance(raw, dict):
        raise _fail(where, "amount is not an object")
    amount_id = _str_field(raw, "amount_id", where)
    where = f"{where} amount {amount_id}"
    value = raw.get("amount")
    if not isinstance(value, str) or not _MONEY_RE.match(value):
        raise _fail(where, f"amount {value!r} is not a two-decimal money string")
    try:
        verification = Verification(raw.get("verification"))
    except ValueError as exc:
        raise _fail(where, str(exc)) from exc
    if verification is Verification.UNVERIFIED:
        # An unverified figure on this series would land on a signed federal
        # filing; the loader refuses rather than leaving it to a test.
        raise _fail(where, "a statutory dollar amount may never be unverified")
    notes = raw.get("notes", "")
    if not isinstance(notes, str):
        raise _fail(where, "notes must be a string")
    return DollarAmount(
        amount_id=amount_id,
        citation=_str_field(raw, "citation", where),
        description=_str_field(raw, "description", where),
        amount=value,
        verification=verification,
        sources=_sources(raw.get("sources"), where),
        figure_since=_date_or_none(raw.get("figure_since"), where, "figure_since"),
        next_adjustment=_date_or_none(
            raw.get("next_adjustment"), where, "next_adjustment"
        ),
        notes=notes,
    )


def _load_json(node: Traversable, where: str) -> dict[str, object]:
    try:
        parsed = json.loads(node.read_text(encoding="utf-8"))
    except (FileNotFoundError, ValueError) as exc:
        raise _fail(where, f"{node.name}: {exc}") from exc
    if not isinstance(parsed, dict):
        raise _fail(where, f"{node.name} is not a JSON object")
    return parsed


def _load_release(release_dir: Traversable, series_id: str) -> Release:
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
    sha256 = source.get("sha256")
    if sha256 is not None and not isinstance(sha256, str):
        raise _fail(where, "source.sha256 must be a string or null")
    manifest_notes = manifest.get("notes")
    if not isinstance(manifest_notes, str) or not manifest_notes.strip():
        raise _fail(where, "manifest notes missing — say what this release is")

    payload = _load_json(release_dir.joinpath("amounts.json"), where)
    amounts_raw = payload.get("amounts")
    if not isinstance(amounts_raw, list) or not amounts_raw:
        raise _fail(where, "payload amounts missing or empty")
    amounts = tuple(_amount(raw, where) for raw in amounts_raw)
    ids = [a.amount_id for a in amounts]
    if len(ids) != len(set(ids)):
        raise _fail(where, "duplicate amount ids")

    return Release(
        series_id=series_id,
        effective_date=effective,
        sequence=sequence,
        source_url=_str_field(source, "url", where),
        source_published=_date_or_none(
            source.get("published"), where, "source.published"
        ),
        source_sha256=sha256,
        notes=manifest_notes,
        amounts=amounts,
    )


def load_registry(root: Traversable) -> tuple[Release, ...]:
    """Load and validate every release of the series under a registry root.

    Raises ValueError on any malformed release — the loader-in-CI rule the
    registry model demands, run by tests/test_dollar_amounts.py so a bad
    release fails the pull request, not a filing.
    """
    series_dir = root.joinpath("code").joinpath("dollar-amounts")
    loaded = [
        _load_release(release_dir, DOLLAR_AMOUNTS_SERIES)
        for release_dir in sorted(series_dir.iterdir(), key=lambda n: n.name)
        if release_dir.is_dir()
    ]
    if not loaded:
        raise _fail(DOLLAR_AMOUNTS_SERIES, "series has no releases")
    loaded.sort(key=lambda r: (r.effective_date, r.sequence))
    ids = [r.release_id for r in loaded]
    if len(ids) != len(set(ids)):
        raise _fail(DOLLAR_AMOUNTS_SERIES, "duplicate release ids")
    return tuple(loaded)


@cache
def releases() -> tuple[Release, ...]:
    """The committed registry, shipped inside this package (ADR 0014)."""
    return load_registry(resources.files("insolvia_api").joinpath("regulatory"))


# --- Resolution (effective-dating.md) ----------------------------------------


def pick_release(candidates: tuple[Release, ...], as_of: date) -> Release:
    """The release with the greatest effective_date <= as_of, ties broken by
    highest sequence. Pure over its inputs, so correction tie-breaks are
    testable without fixture directories."""
    applicable = [r for r in candidates if r.effective_date <= as_of]
    if not applicable:
        earliest = min(r.effective_date for r in candidates)
        raise LookupError(
            f"no release of {candidates[0].series_id} is effective on or "
            f"before {as_of.isoformat()} (series begins {earliest.isoformat()}); "
            "refusing to compute on figures that do not describe that date"
        )
    return max(applicable, key=lambda r: (r.effective_date, r.sequence))


def resolve(as_of: date) -> Release:
    """The release in force on `as_of` — the case's filing date while a case
    floats, its pinned assembly date afterwards."""
    return pick_release(releases(), as_of)


def get(release_id: str) -> Release:
    """That exact release — must succeed forever for any id ever pinned as a
    case's `constants_set_id`."""
    for release in releases():
        if release.release_id == release_id:
            return release
    raise KeyError(f"series {DOLLAR_AMOUNTS_SERIES!r} has no release {release_id!r}")


def latest() -> Release:
    """Newest by (effective_date, sequence), even if still in the future."""
    return releases()[-1]
