"""The Schedule C workbench's arithmetic (issue #346): which exemptions this
case may claim, what it has claimed against each statute, and what each
asset still leaves exposed — one pure function over the case's records.

WHY THIS IS SERVER-SIDE AND ONE FUNCTION. The app holds every number it
would need to work out "current value less liens less claims", and adding
them up there would give the firm a second answer to "how much of this
house is unexempt" that agrees with Schedule C only until somebody edits an
asset, a lien or a claim (ADR 0001, and the reasoning `core/liens.py` and
`core/case_summary.py` each carry). So the figures the exemptions panel
shows come from here, and the panel adds nothing up.

WHAT IT IS NOT. `case_summary.py`'s totals are the plain sums the summary
reports; this is the richer per-asset and per-statute view, and it reads the
same `exemption` records the same way (`asset_id`, `statute_citation`,
`amount`, `claims_full_fmv`) rather than defining a second total. It is also
not exemption planning: nothing here chooses a statute for an asset or moves
a claim between them — it reports what the preparer has done and what room
the registry leaves.

THE RULES.

    as_of            = the petition's expected filing date if one is typed,
                       else today — the floating case resolves the registry
                       as of today (effective-dating.md, "Float, then pin").
    election         = the case's `exemption_set`, honouring the domicile
                       state's opt-out rule from the registry: an opt-out
                       state forces § 522(b)(3) whatever is stored, and a
                       state that allows the election has no default — the
                       preparer must choose, and the table is empty until
                       they do. No guessing on a signed schedule.
    per statute      : limit    = the entry's amount (the statute's joint
                                  figure in a two-debtor case where the
                                  registry names one); a wildcard adds the
                                  unused homestead it may absorb, up to the
                                  registry's carry-over cap;
                       claimed  = the claims in this case citing that
                                  statute, counted as below;
                       available = max(limit - claimed, 0). Over-claiming
                                  shows as zero available, never an error —
                                  intake is progressive, and a claim typed
                                  before its limit was known is a fact to
                                  show, not a request to refuse.
    per asset        : current value = the value 106C prints (the portion
                                  owned), else the entire value;
                       liens    = the asset's secured total, from
                                  `core/liens.py` — the same figure B106D
                                  prints from;
                       net equity = max(current value - liens, 0);
                       claimed  = the claims on this asset, a dollar claim at
                                  its amount and a "100% of fair market
                                  value" claim at the asset's value capped by
                                  the statute's limit (that cap is what the
                                  election means — Schwab v. Reilly);
                       § 522(p) = where the asset is real property and a
                                  claim on it says it was acquired within
                                  1,215 days of filing, the claimed total
                                  counts only up to the registry's cap;
                       unexempt = max(net equity - claimed, 0).
    lookbacks        = the three § 522 windows as calendar dates, computed
                       from `as_of`: (o) ten years, (p) 1,215 days,
                       (q) five years; plus the § 522(b)(3)(A) 730-day
                       domicile period. Ten and five years step the year
                       back and clamp a leap day, because a statute counts
                       years, not days.
    domicile warning = raised when a B107 prior-address entry for Debtor 1
                       overlaps the 730 days before filing — the data the
                       case holds that can say the debtor moved recently.
                       Absent that data the warning cannot be raised, and
                       is not.

Every dollar figure is the registry's or the record's — nothing here holds a
constant. The § 522(p) cap is `us-homestead-1215-day-cap` on the federal
series (ADR 0017 keeps the federal caps beside the claimable entries, one
owner per figure), read through `federal_limits`; the state schemes come
from `schemes_for_state`, which refuses states outside the launch set and
dates before a series' baseline, and both refusals become `problems` here
rather than guesses.

Money is `Decimal` throughout and two-place STRINGS on the wire, the
`ClaimBody.amount` reasoning: these land on a filing.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date, timedelta
from decimal import Decimal
from typing import Final

from insolvia_core.assets import AssetBody
from insolvia_core.cases import Case
from insolvia_core.claims import ClaimBody
from insolvia_core.debtors import Debtor
from insolvia_core.exemption_claims import ExemptionBody
from insolvia_core.petitions import PetitionBody
from insolvia_core.sofa import PriorAddress, SofaEntryBody

from .exemptions import (
    ExemptionEntry,
    ExemptionScheme,
    StatutoryLimit,
    federal_limits,
    schemes_for_state,
)
from .form_projections.shared import CaseFile, amount
from .liens import derive_liens

# The two answers 106C line 1 admits — `insolvia_core.cases.EXEMPTION_SETS`,
# named here for the branches below.
STATE_ELECTION: Final = "state_and_federal_nonbankruptcy"
FEDERAL_ELECTION: Final = "federal"

# The § 522(p) cap's id on the `exemptions/federal` series.
HOMESTEAD_1215_DAY_CAP_ID: Final = "us-homestead-1215-day-cap"

# The § 522 windows, as the statute counts them.
SECTION_522O_YEARS: Final = 10
SECTION_522P_DAYS: Final = 1215
SECTION_522Q_YEARS: Final = 5
# § 522(b)(3)(A): the domicile that governs is the one held for the 730 days
# before filing (or, failing one, the greater part of the 180 days before
# that period).
DOMICILE_PERIOD_DAYS: Final = 730

_ZERO: Final = Decimal("0.00")
_CENT: Final = Decimal("0.01")


@dataclass(frozen=True)
class ExemptionCase:
    """What the analysis reads — the records with their ids where the panel
    needs to act on one (a claim it can remove, an asset it sits under),
    bodies otherwise. The route builds this from the stores; the tests build
    it by hand."""

    case: Case
    debtors: tuple[Debtor, ...] = ()
    petition: PetitionBody | None = None
    assets: tuple[tuple[str, AssetBody], ...] = ()
    claims: tuple[tuple[str, ClaimBody], ...] = ()
    exemptions: tuple[tuple[str, ExemptionBody], ...] = ()
    sofa_entries: tuple[SofaEntryBody, ...] = ()


@dataclass(frozen=True)
class ElectionOption:
    """One answer the case may store for 106C line 1, and the scheme it means."""

    value: str
    scheme_id: str
    name: str


@dataclass(frozen=True)
class Election:
    """106C line 1 as resolved for this case.

    `stored` is what the case record says; `effective` is what the analysis
    used — the same value where the law allows it, the state scheme where
    the state has opted out, and None where a choice is still owed.
    `opted_out` is None when the state is unknown or unsupported.
    """

    stored: str | None
    effective: str | None
    state: str | None
    opted_out: bool | None
    opt_out_citation: str | None
    options: tuple[ElectionOption, ...]


@dataclass(frozen=True)
class EntryAvailability:
    """One claimable statute and what this case has left under it.

    `limit` is None where the registry states no flat dollar cap — an
    unlimited homestead, a "reasonably necessary for support" entry — and so
    is `available`. `carryover` is the part of a wildcard's limit that is
    unused homestead carried over, so a screen can say where the room came
    from.
    """

    entry: ExemptionEntry
    limit: Decimal | None
    carryover: Decimal | None
    claimed: Decimal
    available: Decimal | None


@dataclass(frozen=True)
class AssetClaim:
    """One exemption record on an asset, and what it counts as."""

    exemption_id: str
    statute_citation: str | None
    amount: Decimal | None
    claims_full_fmv: bool | None
    acquired_within_1215_days: bool | None
    claimed: Decimal
    #: False when the citation is not in the effective scheme's table — typed
    #: before the election was made, or under the other scheme.
    known_statute: bool


@dataclass(frozen=True)
class AssetExemptions:
    """One asset's equity and what the claims on it cover."""

    asset_id: str
    description: str | None
    category: str | None
    current_value: Decimal | None
    liens: Decimal
    net_equity: Decimal | None
    claimed: Decimal
    unexempt: Decimal | None
    #: The § 522(p) cap where it binds this asset, else None.
    homestead_cap: Decimal | None
    #: True when the claims exceeded the cap and were counted only up to it.
    cap_applied: bool
    claims: tuple[AssetClaim, ...]
    #: The default amount for a NEW claim under each entry — min(unexempt,
    #: available), whichever of the two is known — keyed by entry id.
    suggestions: tuple[tuple[str, Decimal], ...]


@dataclass(frozen=True)
class Lookbacks:
    section_522o: date
    section_522p: date
    section_522q: date
    domicile_period_start: date


@dataclass(frozen=True)
class ExemptionAnalysis:
    as_of: date
    #: Which fact `as_of` came from: "expected_filing_date" or "today".
    as_of_source: str
    election: Election
    entries: tuple[EntryAvailability, ...]
    limits: tuple[StatutoryLimit, ...]
    lookbacks: Lookbacks
    assets: tuple[AssetExemptions, ...]
    warnings: tuple[str, ...]
    problems: tuple[str, ...]


# --- dates -------------------------------------------------------------------


def resolution_date(petition: PetitionBody | None, today: date) -> tuple[date, str]:
    """The registry resolution date and where it came from: the expected
    filing date while one is typed, today otherwise (a floating case)."""
    if petition is not None and petition.expected_filing_date is not None:
        return date.fromisoformat(petition.expected_filing_date), "expected_filing_date"
    return today, "today"


def years_before(day: date, years: int) -> date:
    """`day` with the year stepped back, a leap day clamped to the 28th —
    a statute's "N years" is a calendar count, not N x 365 days."""
    try:
        return day.replace(year=day.year - years)
    except ValueError:
        return day.replace(year=day.year - years, day=28)


def compute_lookbacks(as_of: date) -> Lookbacks:
    return Lookbacks(
        section_522o=years_before(as_of, SECTION_522O_YEARS),
        section_522p=as_of - timedelta(days=SECTION_522P_DAYS),
        section_522q=years_before(as_of, SECTION_522Q_YEARS),
        domicile_period_start=as_of - timedelta(days=DOMICILE_PERIOD_DAYS),
    )


# --- the election ------------------------------------------------------------


def _debtor_state(debtors: tuple[Debtor, ...]) -> str | None:
    debtor_1 = next((d for d in debtors if d.filing_role == "debtor_1"), None)
    if debtor_1 is None:
        return None
    state = debtor_1.residence_address.state
    return state.strip().upper() if state else None


def _schemes(
    state: str | None, as_of: date, problems: list[str]
) -> tuple[ExemptionScheme, ...]:
    if state is None:
        problems.append(
            "Debtor 1 has no residence state on file; which exemptions apply "
            "depends on it (§ 522(b)(3)(A))."
        )
        return ()
    try:
        return schemes_for_state(state, as_of)
    except KeyError:
        problems.append(
            f"Exemptions for {state} are not supported yet — Schedule C can be "
            "prepared for FL, TX and GA (ADR 0017)."
        )
    except LookupError as refusal:
        problems.append(str(refusal))
    return ()


def election_refusal(election: str, *, state: str | None, as_of: date) -> str | None:
    """Why `election` cannot be stored for a debtor domiciled in `state`, or
    None where it can (or where the state cannot tell — unknown, unsupported,
    or before its series' baseline, each of which the analysis reports on
    read). The PATCH route's check, so the case record never carries an
    answer the law forbids."""
    if election != FEDERAL_ELECTION or state is None:
        return None
    try:
        schemes = schemes_for_state(state, as_of)
    except (KeyError, LookupError):
        return None
    scheme = schemes[0]
    if scheme.opted_out_of_federal:
        return (
            f"{state} has opted out of the federal § 522(d) exemptions "
            f"({scheme.opt_out_citation}); the state scheme is the only answer."
        )
    return None


def _election(
    stored: str | None,
    state: str | None,
    schemes: tuple[ExemptionScheme, ...],
    problems: list[str],
) -> tuple[Election, ExemptionScheme | None]:
    if not schemes:
        return (
            Election(
                stored=stored,
                effective=None,
                state=state,
                opted_out=None,
                opt_out_citation=None,
                options=(),
            ),
            None,
        )
    state_scheme = schemes[0]
    federal = schemes[1] if len(schemes) > 1 else None
    options = [
        ElectionOption(
            value=STATE_ELECTION,
            scheme_id=state_scheme.scheme_id,
            name=state_scheme.name,
        )
    ]
    if federal is not None:
        options.append(
            ElectionOption(
                value=FEDERAL_ELECTION,
                scheme_id=federal.scheme_id,
                name=federal.name,
            )
        )
    opted_out = bool(state_scheme.opted_out_of_federal)

    effective: str | None
    if opted_out:
        if stored == FEDERAL_ELECTION:
            problems.append(
                f"The case elects the federal § 522(d) list, but {state} has "
                f"opted out ({state_scheme.opt_out_citation}); the state scheme "
                "is used."
            )
        effective = STATE_ELECTION
    elif stored is None:
        problems.append(
            f"Choose the exemption scheme: {state} lets the debtor claim the "
            "state scheme or the federal § 522(d) list (106C line 1)."
        )
        effective = None
    else:
        effective = stored

    scheme = (
        None
        if effective is None
        else federal
        if effective == FEDERAL_ELECTION
        else state_scheme
    )
    return (
        Election(
            stored=stored,
            effective=effective,
            state=state,
            opted_out=opted_out,
            opt_out_citation=state_scheme.opt_out_citation,
            options=tuple(options),
        ),
        scheme,
    )


# --- the arithmetic ----------------------------------------------------------


def _money(value: Decimal) -> Decimal:
    return value.quantize(_CENT)


def _asset_value(asset: AssetBody | None) -> Decimal | None:
    """The value 106C prints (the portion owned), falling back to the entire
    value while only that is typed."""
    if asset is None:
        return None
    for candidate in (asset.value_portion_owned, asset.value_entire):
        if candidate is not None:
            return _money(Decimal(candidate))
    return None


def _claimed_amount(
    claim: ExemptionBody, asset_value: Decimal | None, limit: Decimal | None
) -> Decimal:
    """What one claim counts as: its dollar amount, or — for a "100% of fair
    market value up to the statutory limit" election — the asset's value
    capped by the statute's limit where the statute has one."""
    if claim.claims_full_fmv:
        if asset_value is None:
            return _ZERO
        return _money(min(asset_value, limit)) if limit is not None else asset_value
    return _money(amount(claim.amount))


def _base_limit(entry: ExemptionEntry, joint: bool) -> Decimal | None:
    if joint and entry.joint_amount is not None:
        return Decimal(entry.joint_amount)
    return Decimal(entry.amount) if entry.amount is not None else None


def _authorities(citation: str) -> set[str]:
    """The ways a claim may cite one registry entry: the whole citation, or
    any one of the `;`-separated authorities it lists, each with or without
    a trailing parenthetical note — so a claim citing "Fla. Const. art. X,
    § 4(a)(1)" draws down the homestead entry whose registry citation goes
    on to name the implementing statute. Whitespace-normalised; nothing
    looser, because a citation that matches two entries would be a claim
    counted twice."""
    forms: set[str] = set()
    whole = " ".join(citation.split())
    forms.add(whole)
    for part in whole.split(";"):
        part = part.strip()
        forms.add(part)
        if part.endswith(")") and " (" in part:
            forms.add(part[: part.rindex(" (")].strip())
    return forms


def cites(entry: ExemptionEntry, citation: str | None) -> bool:
    if citation is None:
        return False
    return " ".join(citation.split()) in _authorities(entry.citation)


def _claimed_under(
    entry: ExemptionEntry,
    limit: Decimal | None,
    inputs: ExemptionCase,
    values: dict[str, Decimal | None],
) -> Decimal:
    total = _ZERO
    for _id, claim in inputs.exemptions:
        if not cites(entry, claim.statute_citation):
            continue
        asset_value = values.get(claim.asset_id or "")
        total += _claimed_amount(claim, asset_value, limit)
    return _money(total)


def _entries(
    scheme: ExemptionScheme | None,
    inputs: ExemptionCase,
    values: dict[str, Decimal | None],
    joint: bool,
) -> tuple[EntryAvailability, ...]:
    if scheme is None:
        return ()
    by_id = {entry.entry_id: entry for entry in scheme.entries}
    rows: list[EntryAvailability] = []
    for entry in scheme.entries:
        limit = _base_limit(entry, joint)
        carryover: Decimal | None = None
        homestead = (
            by_id.get(entry.wildcard_carryover_from)
            if entry.wildcard_carryover_from is not None
            else None
        )
        if homestead is not None and entry.wildcard_carryover_cap is not None:
            homestead_limit = _base_limit(homestead, joint)
            if homestead_limit is not None:
                unused = max(
                    homestead_limit
                    - _claimed_under(homestead, homestead_limit, inputs, values),
                    _ZERO,
                )
                carryover = _money(min(unused, Decimal(entry.wildcard_carryover_cap)))
                limit = (limit if limit is not None else _ZERO) + carryover
        claimed = _claimed_under(entry, limit, inputs, values)
        rows.append(
            EntryAvailability(
                entry=entry,
                limit=_money(limit) if limit is not None else None,
                carryover=carryover,
                claimed=claimed,
                available=(
                    _money(max(limit - claimed, _ZERO)) if limit is not None else None
                ),
            )
        )
    return tuple(rows)


def _homestead_cap(limits: tuple[StatutoryLimit, ...]) -> Decimal | None:
    return next(
        (
            Decimal(limit.amount)
            for limit in limits
            if limit.limit_id == HOMESTEAD_1215_DAY_CAP_ID
        ),
        None,
    )


def _assets(
    inputs: ExemptionCase,
    entries: tuple[EntryAvailability, ...],
    values: dict[str, Decimal | None],
    cap: Decimal | None,
) -> tuple[AssetExemptions, ...]:
    figures = derive_liens(
        CaseFile(case=inputs.case, assets=inputs.assets, claims=inputs.claims)
    )
    rows: list[AssetExemptions] = []
    for asset_id, asset in inputs.assets:
        current = values[asset_id]
        on_asset = figures.asset(asset_id)
        liens = on_asset.secured_total if on_asset is not None else _ZERO
        net_equity = (
            _money(max(current - liens, _ZERO)) if current is not None else None
        )

        claims: list[AssetClaim] = []
        for exemption_id, claim in inputs.exemptions:
            if claim.asset_id != asset_id:
                continue
            row = next(
                (r for r in entries if cites(r.entry, claim.statute_citation)), None
            )
            claims.append(
                AssetClaim(
                    exemption_id=exemption_id,
                    statute_citation=claim.statute_citation,
                    amount=(
                        _money(Decimal(claim.amount))
                        if claim.amount is not None
                        else None
                    ),
                    claims_full_fmv=claim.claims_full_fmv,
                    acquired_within_1215_days=claim.acquired_within_1215_days,
                    claimed=_claimed_amount(
                        claim, current, row.limit if row is not None else None
                    ),
                    known_statute=row is not None,
                )
            )
        claimed = _money(sum((c.claimed for c in claims), _ZERO))

        homestead_cap: Decimal | None = None
        cap_applied = False
        if (
            cap is not None
            and asset.category == "real_property"
            and any(c.acquired_within_1215_days for c in claims)
        ):
            homestead_cap = _money(cap)
            if claimed > homestead_cap:
                cap_applied = True
        counted = min(claimed, homestead_cap) if homestead_cap is not None else claimed
        unexempt = (
            _money(max(net_equity - counted, _ZERO)) if net_equity is not None else None
        )

        suggestions: list[tuple[str, Decimal]] = []
        for row in entries:
            known = [v for v in (unexempt, row.available) if v is not None]
            if known:
                suggestions.append((row.entry.entry_id, _money(min(known))))

        rows.append(
            AssetExemptions(
                asset_id=asset_id,
                description=asset.description,
                category=asset.category,
                current_value=current,
                liens=_money(liens),
                net_equity=net_equity,
                claimed=claimed,
                unexempt=unexempt,
                homestead_cap=homestead_cap,
                cap_applied=cap_applied,
                claims=tuple(claims),
                suggestions=tuple(suggestions),
            )
        )
    return tuple(rows)


# --- the domicile warning ----------------------------------------------------


def _prior_addresses(entries: tuple[SofaEntryBody, ...]) -> list[PriorAddress]:
    return [e.payload for e in entries if isinstance(e.payload, PriorAddress)]


def domicile_warning(
    inputs: ExemptionCase, state: str | None, as_of: date, period_start: date
) -> str | None:
    """§ 522(b)(3)(A), as far as the case file can tell: a B107 question-2
    prior address for Debtor 1 that overlaps the 730 days before filing means
    the debtor was not domiciled in one place for the whole period, and the
    exemptions of the state where they lived for the 180 days before that
    period may govern instead. A prior address with no end date is treated
    as overlapping — a move whose date is not yet typed is still a move."""
    for prior in _prior_addresses(inputs.sofa_entries):
        if prior.which_debtor not in ("debtor_1", "both"):
            continue
        moved_out = (
            date.fromisoformat(prior.to_date) if prior.to_date is not None else None
        )
        if moved_out is not None and moved_out < period_start:
            continue
        where = f" in {state}" if state else ""
        when = (
            f"on {moved_out.isoformat()}"
            if moved_out is not None
            else "on a date not yet recorded"
        )
        return (
            f"Debtor 1 left a prior address {when}, inside the 730 days before "
            f"{as_of.isoformat()}. Under § 522(b)(3)(A) the exemptions of the "
            "state where the debtor was domiciled for the 180 days before "
            f"{period_start.isoformat()} may govern, not the current "
            f"residence{where}. Confirm the domicile before claiming."
        )
    return None


# --- the whole ---------------------------------------------------------------


def analyse(inputs: ExemptionCase, *, today: date) -> ExemptionAnalysis:
    """The analysis of one already-loaded case. `today` is an argument so the
    floating case's resolution date is the caller's clock, not this
    module's — which is what keeps the function pure under pytest."""
    problems: list[str] = []
    warnings: list[str] = []

    as_of, as_of_source = resolution_date(inputs.petition, today)
    lookbacks = compute_lookbacks(as_of)
    state = _debtor_state(inputs.debtors)
    schemes = _schemes(state, as_of, problems)
    election, scheme = _election(inputs.case.exemption_set, state, schemes, problems)

    limits: tuple[StatutoryLimit, ...]
    try:
        limits = federal_limits(as_of)
    except LookupError as refusal:
        problems.append(str(refusal))
        limits = ()

    values = {asset_id: _asset_value(asset) for asset_id, asset in inputs.assets}
    joint = any(d.filing_role == "debtor_2" for d in inputs.debtors)
    entries = _entries(scheme, inputs, values, joint)
    assets = _assets(inputs, entries, values, _homestead_cap(limits))

    unknown = sorted(
        {
            claim.statute_citation
            for _id, claim in inputs.exemptions
            if scheme is not None
            and claim.statute_citation is not None
            and not any(cites(e, claim.statute_citation) for e in scheme.entries)
        }
    )
    if unknown:
        warnings.append(
            "Claims cite a statute outside the "
            f"{scheme.name if scheme else 'elected'} scheme: {', '.join(unknown)}. "
            "They do not draw down any limit in this table."
        )
    moved = domicile_warning(inputs, state, as_of, lookbacks.domicile_period_start)
    if moved is not None:
        warnings.append(moved)

    return ExemptionAnalysis(
        as_of=as_of,
        as_of_source=as_of_source,
        election=election,
        entries=entries,
        limits=limits,
        lookbacks=lookbacks,
        assets=assets,
        warnings=tuple(warnings),
        problems=tuple(problems),
    )


# --- the wire shape ----------------------------------------------------------


def _money_json(value: Decimal | None) -> str | None:
    return f"{_money(value):f}" if value is not None else None


def _entry_json(row: EntryAvailability) -> dict[str, object]:
    entry = row.entry
    return {
        "entryId": entry.entry_id,
        "category": entry.category.value,
        "description": entry.description,
        "citation": entry.citation,
        "unlimited": entry.unlimited,
        "limit": _money_json(row.limit),
        "perItemAmount": entry.per_item_amount,
        "carryover": _money_json(row.carryover),
        "claimed": _money_json(row.claimed),
        "available": _money_json(row.available),
        "notes": entry.notes,
    }


def _claim_json(claim: AssetClaim) -> dict[str, object]:
    return {
        "exemptionId": claim.exemption_id,
        "statuteCitation": claim.statute_citation,
        "amount": _money_json(claim.amount),
        "claimsFullFmv": claim.claims_full_fmv,
        "acquiredWithin1215Days": claim.acquired_within_1215_days,
        "claimed": _money_json(claim.claimed),
        "knownStatute": claim.known_statute,
    }


def _asset_json(row: AssetExemptions) -> dict[str, object]:
    return {
        "assetId": row.asset_id,
        "description": row.description,
        "category": row.category,
        "currentValue": _money_json(row.current_value),
        "liens": _money_json(row.liens),
        "netEquity": _money_json(row.net_equity),
        "claimed": _money_json(row.claimed),
        "unexempt": _money_json(row.unexempt),
        "homesteadCap": _money_json(row.homestead_cap),
        "capApplied": row.cap_applied,
        "claims": [_claim_json(c) for c in row.claims],
        "suggestions": {
            entry_id: _money_json(value) for entry_id, value in row.suggestions
        },
    }


def analysis_json(analysis: ExemptionAnalysis) -> dict[str, object]:
    """camelCase, money as two-place strings, and NULL where a figure is not
    derivable — not absent, because the panel labels every box and needs to
    know which one is missing (the standards route's rule, not the liens
    route's)."""
    election = analysis.election
    return {
        "asOf": analysis.as_of.isoformat(),
        "asOfSource": analysis.as_of_source,
        "election": {
            "stored": election.stored,
            "effective": election.effective,
            "state": election.state,
            "optedOut": election.opted_out,
            "optOutCitation": election.opt_out_citation,
            "options": [
                {"value": o.value, "schemeId": o.scheme_id, "name": o.name}
                for o in election.options
            ],
        },
        "entries": [_entry_json(row) for row in analysis.entries],
        "limits": [
            {
                "limitId": limit.limit_id,
                "citation": limit.citation,
                "description": limit.description,
                "amount": limit.amount,
            }
            for limit in analysis.limits
        ],
        "lookbacks": {
            "section522o": analysis.lookbacks.section_522o.isoformat(),
            "section522p": analysis.lookbacks.section_522p.isoformat(),
            "section522q": analysis.lookbacks.section_522q.isoformat(),
            "domicilePeriodStart": (
                analysis.lookbacks.domicile_period_start.isoformat()
            ),
        },
        "assets": [_asset_json(row) for row in analysis.assets],
        "warnings": list(analysis.warnings),
        "problems": list(analysis.problems),
    }
