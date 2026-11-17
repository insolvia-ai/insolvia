"""The exemption dataset: loader and resolution for the `exemptions/*` series.

Which states launch supports — Florida, Texas, Georgia — is a *provisional*
business decision recorded in
docs/adr/0017-launch-states-florida-texas-georgia.md. The data itself lives in
the regulatory release registry (docs/reference/effective-dating.md;
docs/adr/0014-the-repository-is-the-regulatory-release-registry.md): committed
release directories under `src/insolvia_api/regulatory/`, one series per
scheme —

    exemptions/federal   11 U.S.C. § 522(d) + the cross-scheme § 522 caps
    exemptions/fl        Florida (opt-out)
    exemptions/tx        Texas (federal election available)
    exemptions/ga        Georgia (opt-out)

Each release is `<series_id>/<effective_date>[+<sequence>]/` holding a
`manifest.json` (identity, upstream source, notes) and a `scheme.json`
payload — the payload shape this module owns, per effective-dating.md's
"payload shapes belong to 9.3, 9.5 and 10.1". Schedule C (Form 106C) assembly
reads it: property → claimed exemption with statute citation and amount, plus
the opt-out rule that decides what 106C line 1 may even answer.

This module is the registry's first consumer, so it also owes the loader that
validates it in CI: `registry()` parses and checks every committed release
(manifest well-formed, ids match paths, payload figures well-formed), and
tests/test_exemptions.py runs it plus dataset-consistency checks — a malformed
or internally inconsistent release fails the pull request, not a filing.

Three properties are load-bearing:

- **Every figure is cited and tiered.** A wrong dollar amount lands on a
  signed federal filing, so each entry carries the sources it was verified
  against and a `Verification` tier; an entry carrying a dollar amount may
  never be UNVERIFIED (enforced in tests).
- **Resolution follows effective-dating.md exactly**: `resolve` picks the
  release with the greatest effective date <= the filing date (ties to the
  highest sequence), `get` returns a pinned release forever, and resolution
  before a series' earliest release *refuses* rather than guessing — wrong
  data is worse than no answer, and this product cannot prepare a case filed
  before its verified baseline snapshots.
- **Money follows the repo rule**: fixed-scale two-decimal strings, never
  floats (case-data-model.md, "Value types").

Stdlib only, no framework, no AWS — reading the registry shipped inside this
package is configuration access, not an external dependency.
"""

from __future__ import annotations

import json
import re
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import date
from enum import Enum
from functools import cache
from importlib import resources
from importlib.resources.abc import Traversable

# The next scheduled § 104(b) adjustment of every federal figure. The
# regulatory source register owns the calendar; this constant exists so a test
# can refuse to let the dataset outlive its own review date silently.
NEXT_FEDERAL_ADJUSTMENT = date(2028, 4, 1)

# The launch set, per ADR 0017. Provisional: the design-partner firm's state
# amends this tuple when it is known.
LAUNCH_STATES = ("FL", "TX", "GA")

FEDERAL_SERIES = "exemptions/federal"

_MONEY_RE = re.compile(r"^[1-9]\d*\.\d{2}$")
_DIRNAME_RE = re.compile(r"^(\d{4}-\d{2}-\d{2})(?:\+(\d+))?$")


class Category(Enum):
    """What kind of property an exemption protects — the analyzer's axis."""

    HOMESTEAD = "homestead"
    PERSONAL_PROPERTY = "personal_property"
    VEHICLE = "vehicle"
    HOUSEHOLD_GOODS = "household_goods"
    JEWELRY = "jewelry"
    TOOLS_OF_TRADE = "tools_of_trade"
    WILDCARD = "wildcard"
    LIFE_INSURANCE = "life_insurance"
    HEALTH_AIDS = "health_aids"
    SUPPORT_AND_BENEFITS = "support_and_benefits"
    PERSONAL_INJURY = "personal_injury"
    WAGES = "wages"
    RETIREMENT = "retirement"


class Verification(Enum):
    """How the entry's figure was verified (ingestion-time provenance).

    - PRIMARY_CORROBORATED: two independent official government sources agree.
    - PRIMARY: one official government source (statute text, Federal
      Register, enrolled act).
    - MIRROR: unofficial mirror(s) of the statute text (FindLaw, Public.Law) —
      the official host was unreachable at ingestion time.
    - UNVERIFIED: carried from research notes; the figure or citation was NOT
      independently confirmed. Never acceptable on a dollar amount; flagged
      so Schedule C assembly can refuse or warn.
    """

    PRIMARY_CORROBORATED = "primary_corroborated"
    PRIMARY = "primary"
    MIRROR = "mirror"
    UNVERIFIED = "unverified"


@dataclass(frozen=True)
class Source:
    """One document a figure was checked against, and when."""

    title: str
    url: str
    accessed: date


@dataclass(frozen=True)
class ExemptionEntry:
    """One claimable exemption: what 106C's "specific laws" column cites.

    `amount` / `per_item_amount` / `joint_amount` are two-decimal money
    strings (repo Money rule). `amount is None` means the entry has no flat
    dollar cap — either `unlimited` is set (no value cap at all, though
    non-dollar limits like acreage may apply; see `notes`) or the cap is
    conditional and `notes` says how (e.g. "reasonably necessary for
    support", "counts against the § 42.001 aggregate").

    `joint_amount` is the figure the statute itself names for the two-spouse
    / family situation, not a computed doubling. Federal § 522(m) doubling in
    joint cases is a scheme-level rule, noted on the scheme.

    A wildcard that absorbs unused homestead names its homestead via
    `wildcard_carryover_from` (an entry id in the same scheme) and the
    statutory cap on the carried-over amount via `wildcard_carryover_cap`.

    `figure_since` is when this figure took force, where that was verified;
    None means the figure is confirmed current but the amendment date was
    not verified. Release-level supersession is the registry's job — a
    changed figure arrives as a new release of the series, never as an edit.
    """

    entry_id: str
    category: Category
    description: str
    citation: str
    amount: str | None
    unlimited: bool
    verification: Verification
    sources: tuple[Source, ...]
    per_item_amount: str | None = None
    joint_amount: str | None = None
    wildcard_carryover_from: str | None = None
    wildcard_carryover_cap: str | None = None
    figure_since: date | None = None
    notes: str = ""


@dataclass(frozen=True)
class ExemptionScheme:
    """A body of exemption law a debtor may claim under (106C line 1)."""

    scheme_id: str
    jurisdiction: str  # "US" or a two-letter state code
    name: str
    # Whether the state bars its residents from electing federal § 522(d).
    # None on the federal scheme itself, where the question is meaningless.
    opted_out_of_federal: bool | None
    opt_out_citation: str | None
    entries: tuple[ExemptionEntry, ...]
    notes: str = ""


@dataclass(frozen=True)
class StatutoryLimit:
    """A federal cap that is not itself claimable but limits what is.

    These apply regardless of which scheme the debtor uses — § 522(p)/(q) cap
    state homestead claims even in opt-out states, which is why 106C asks the
    § 522(q) question and why the exemption entity carries
    `acquired_within_1215_days` (case-data-model.md). Carried on the
    `exemptions/federal` series' payload.
    """

    limit_id: str
    citation: str
    description: str
    amount: str
    effective_date: date
    next_adjustment: date | None
    verification: Verification
    sources: tuple[Source, ...]
    notes: str = ""


@dataclass(frozen=True)
class Release:
    """One immutable release of an exemption series (effective-dating.md)."""

    series_id: str
    effective_date: date
    sequence: int
    source_url: str
    source_published: date | None
    source_sha256: str | None
    notes: str
    scheme: ExemptionScheme
    limits: tuple[StatutoryLimit, ...]

    @property
    def release_id(self) -> str:
        base = f"{self.series_id}@{self.effective_date.isoformat()}"
        return base if self.sequence == 1 else f"{base}+{self.sequence}"


# --- Loading and validation --------------------------------------------------


def _fail(where: str, problem: str) -> ValueError:
    return ValueError(f"malformed release {where}: {problem}")


def _money(value: object, where: str, field_name: str) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or not _MONEY_RE.match(value):
        raise _fail(where, f"{field_name} {value!r} is not a two-decimal money string")
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


def _str_field(data: Mapping[str, object], key: str, where: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise _fail(where, f"{key} missing or empty")
    return value


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


def _entry(raw: object, where: str) -> ExemptionEntry:
    if not isinstance(raw, dict):
        raise _fail(where, "entry is not an object")
    entry_id = _str_field(raw, "entry_id", where)
    where = f"{where} entry {entry_id}"
    try:
        category = Category(raw.get("category"))
        verification = Verification(raw.get("verification"))
    except ValueError as exc:
        raise _fail(where, str(exc)) from exc
    unlimited = raw.get("unlimited")
    if not isinstance(unlimited, bool):
        raise _fail(where, "unlimited must be a boolean")
    carry_from = raw.get("wildcard_carryover_from")
    if carry_from is not None and not isinstance(carry_from, str):
        raise _fail(where, "wildcard_carryover_from must be a string or null")
    notes = raw.get("notes", "")
    if not isinstance(notes, str):
        raise _fail(where, "notes must be a string")
    return ExemptionEntry(
        entry_id=entry_id,
        category=category,
        description=_str_field(raw, "description", where),
        citation=_str_field(raw, "citation", where),
        amount=_money(raw.get("amount"), where, "amount"),
        unlimited=unlimited,
        verification=verification,
        sources=_sources(raw.get("sources"), where),
        per_item_amount=_money(raw.get("per_item_amount"), where, "per_item_amount"),
        joint_amount=_money(raw.get("joint_amount"), where, "joint_amount"),
        wildcard_carryover_from=carry_from,
        wildcard_carryover_cap=_money(
            raw.get("wildcard_carryover_cap"), where, "wildcard_carryover_cap"
        ),
        figure_since=_date_or_none(raw.get("figure_since"), where, "figure_since"),
        notes=notes,
    )


def _limit(raw: object, where: str) -> StatutoryLimit:
    if not isinstance(raw, dict):
        raise _fail(where, "limit is not an object")
    limit_id = _str_field(raw, "limit_id", where)
    where = f"{where} limit {limit_id}"
    amount = _money(raw.get("amount"), where, "amount")
    effective = _date_or_none(raw.get("effective_date"), where, "effective_date")
    if amount is None or effective is None:
        raise _fail(where, "amount and effective_date are required")
    try:
        verification = Verification(raw.get("verification"))
    except ValueError as exc:
        raise _fail(where, str(exc)) from exc
    notes = raw.get("notes", "")
    if not isinstance(notes, str):
        raise _fail(where, "notes must be a string")
    return StatutoryLimit(
        limit_id=limit_id,
        citation=_str_field(raw, "citation", where),
        description=_str_field(raw, "description", where),
        amount=amount,
        effective_date=effective,
        next_adjustment=_date_or_none(
            raw.get("next_adjustment"), where, "next_adjustment"
        ),
        verification=verification,
        sources=_sources(raw.get("sources"), where),
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

    payload = _load_json(release_dir.joinpath("scheme.json"), where)
    entries_raw = payload.get("entries")
    if not isinstance(entries_raw, list) or not entries_raw:
        raise _fail(where, "payload entries missing or empty")
    limits_raw = payload.get("limits", [])
    if not isinstance(limits_raw, list):
        raise _fail(where, "payload limits must be a list")
    opted_out = payload.get("opted_out_of_federal")
    if opted_out is not None and not isinstance(opted_out, bool):
        raise _fail(where, "opted_out_of_federal must be a boolean or null")
    opt_out_citation = payload.get("opt_out_citation")
    if opt_out_citation is not None and not isinstance(opt_out_citation, str):
        raise _fail(where, "opt_out_citation must be a string or null")
    scheme_notes = payload.get("notes", "")
    if not isinstance(scheme_notes, str):
        raise _fail(where, "payload notes must be a string")

    scheme = ExemptionScheme(
        scheme_id=_str_field(payload, "scheme_id", where),
        jurisdiction=_str_field(payload, "jurisdiction", where),
        name=_str_field(payload, "name", where),
        opted_out_of_federal=opted_out,
        opt_out_citation=opt_out_citation,
        entries=tuple(_entry(raw, where) for raw in entries_raw),
        notes=scheme_notes,
    )
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
        scheme=scheme,
        limits=tuple(_limit(raw, where) for raw in limits_raw),
    )


def load_registry(root: Traversable) -> dict[str, tuple[Release, ...]]:
    """Load and validate every exemption release under a registry root.

    Raises ValueError on any malformed release — this is the loader the
    registry model requires of its first consumer, and the test suite runs
    it over the committed registry so a bad release fails CI, not a filing.
    """
    exemptions_dir = root.joinpath("exemptions")
    series: dict[str, tuple[Release, ...]] = {}
    for series_dir in sorted(exemptions_dir.iterdir(), key=lambda n: n.name):
        if not series_dir.is_dir():
            continue
        series_id = f"exemptions/{series_dir.name}"
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
        raise ValueError("the exemptions registry is empty")
    return series


@cache
def registry() -> dict[str, tuple[Release, ...]]:
    """The committed registry, shipped inside this package (ADR 0014)."""
    return load_registry(resources.files("insolvia_api").joinpath("regulatory"))


# --- Resolution (effective-dating.md) ----------------------------------------


def series_ids() -> tuple[str, ...]:
    return tuple(sorted(registry()))


def releases(series_id: str) -> tuple[Release, ...]:
    found = registry().get(series_id)
    if found is None:
        raise KeyError(f"unknown series {series_id!r}")
    return found


def pick_release(candidates: tuple[Release, ...], as_of: date) -> Release:
    """The release with the greatest effective_date <= as_of, ties broken by
    highest sequence.

    Pure over its inputs, so correction tie-breaks are testable without
    fixture directories.
    """
    applicable = [r for r in candidates if r.effective_date <= as_of]
    if not applicable:
        earliest = min(r.effective_date for r in candidates)
        raise LookupError(
            f"no release of {candidates[0].series_id} is effective on or "
            f"before {as_of.isoformat()} (series begins {earliest.isoformat()}); "
            "refusing to compute on data that does not describe that date"
        )
    return max(applicable, key=lambda r: (r.effective_date, r.sequence))


def resolve(series_id: str, as_of: date) -> Release:
    """The release in force on `as_of` — the case's filing date."""
    return pick_release(releases(series_id), as_of)


def get(series_id: str, release_id: str) -> Release:
    """That exact release — must succeed forever for any id ever pinned."""
    for release in releases(series_id):
        if release.release_id == release_id:
            return release
    raise KeyError(f"series {series_id!r} has no release {release_id!r}")


def latest(series_id: str) -> Release:
    """Newest by (effective_date, sequence), even if still in the future."""
    return releases(series_id)[-1]


def schemes_for_state(state: str, as_of: date) -> tuple[ExemptionScheme, ...]:
    """The schemes a debtor domiciled in `state` may elect between (106C l.1).

    Opt-out states return only their own scheme; election states return the
    state scheme and the federal set, both resolved as of the filing date.
    Raises KeyError for a state outside the launch set — callers must surface
    "unsupported state", never guess. (Which state's law governs at all is
    § 522(b)(3)(A)'s 730-day domicile rule — the analyzer's job, upstream of
    this lookup.)
    """
    series_id = f"exemptions/{state.lower()}"
    if state.upper() not in LAUNCH_STATES or series_id not in registry():
        raise KeyError(
            f"state {state!r} is not in the launch set {LAUNCH_STATES} (ADR 0017)"
        )
    scheme = resolve(series_id, as_of).scheme
    if scheme.opted_out_of_federal:
        return (scheme,)
    return (scheme, resolve(FEDERAL_SERIES, as_of).scheme)


def federal_limits(as_of: date) -> tuple[StatutoryLimit, ...]:
    """The § 522 caps in force on `as_of`, whatever scheme is elected."""
    return resolve(FEDERAL_SERIES, as_of).limits
