#!/usr/bin/env python3
"""Ingest one UST means-testing dataset release into the regulatory registry.

The U.S. Trustee Program republishes the data the 122A/122C forms need —
Census median family income by state and household size, the IRS National
Standards, and the IRS Local Standards — as XLS/XLSX files on per-period
pages under https://www.justice.gov/ust/means-testing (issue #99; the
regulatory source register owns the cadence). Each period becomes a release
of an effective-dated series per docs/reference/effective-dating.md:

    ust/census-median-family-income   the § 707(b)(7) median table
    ust/irs-national-standards        National Standards + out-of-pocket health care
    ust/irs-local-standards           housing/utilities by county + transportation

This script is the mechanical half of "ingestion is a reviewed PR": it
downloads the period's spreadsheets, converts them to the JSON payload shape
core/ust_data.py validates, and writes the release directory (manifest.json +
dataset.json) under services/api/src/insolvia_api/regulatory/. The DIFF of
that directory is the review surface — read the numbers, spot-check them
against the UST's own HTML tables, then commit. Merge is the release.

Stdlib only (the XLSX is unzipped and parsed as XML) so a fresh clone can run
it with bare python3, exactly like forms/scripts/check.py.

Usage (each subcommand writes one release directory):

  # Census medians — effective date from the UST notice ("applies to cases
  # filed on or after ..."), period from the page URL that carries the file:
  python3 scripts/ingest-ust-data.py medians --period 20260401 --effective 2026-04-01

  # IRS National Standards (+ out-of-pocket health care):
  python3 scripts/ingest-ust-data.py national --period 20260715 --effective 2026-07-15

  # IRS Local Standards, scoped to the launch states (ADR 0017). The
  # transportation MSA county definitions are NOT in any spreadsheet — the
  # UST publishes them only on the per-region HTML pages
  # (IRS_Trans_Exp_Stds_SO.htm and siblings), so they are curated by hand
  # into a small JSON file this command embeds verbatim; see --msa-file.
  python3 scripts/ingest-ust-data.py local --period 20260715 --effective 2026-07-15 \
      --msa-file /tmp/msa-counties.json

After writing, edit the manifest's `notes` if anything about the source was
odd, then run the api test gate — tests/test_ust_data.py validates every
committed release and fails the PR on a malformed one.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import urllib.request
import zipfile
from datetime import date
from decimal import Decimal, InvalidOperation
from io import BytesIO
from pathlib import Path
from xml.etree import ElementTree as ET

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT_ROOT = REPO_ROOT / "services/api/src/insolvia_api/regulatory"

BASE = "https://www.justice.gov/ust/eo/bapcpa/{period}/bci_data/docs/{name}.xlsx"

# The launch set (ADR 0017): the local-standards payload is scoped to these
# states because nothing downstream computes for any other state — the
# exemptions registry draws the same line. Widening the launch set means a new
# release of ust/irs-local-standards with the new states included.
LAUNCH_STATES = ("FL", "GA", "TX")

STATE_CODES = {
    "Alabama": "AL",
    "Alaska": "AK",
    "Arizona": "AZ",
    "Arkansas": "AR",
    "California": "CA",
    "Colorado": "CO",
    "Connecticut": "CT",
    "Delaware": "DE",
    "District of Columbia": "DC",
    "Florida": "FL",
    "Georgia": "GA",
    "Hawaii": "HI",
    "Idaho": "ID",
    "Illinois": "IL",
    "Indiana": "IN",
    "Iowa": "IA",
    "Kansas": "KS",
    "Kentucky": "KY",
    "Louisiana": "LA",
    "Maine": "ME",
    "Maryland": "MD",
    "Massachusetts": "MA",
    "Michigan": "MI",
    "Minnesota": "MN",
    "Mississippi": "MS",
    "Missouri": "MO",
    "Montana": "MT",
    "Nebraska": "NE",
    "Nevada": "NV",
    "New Hampshire": "NH",
    "New Jersey": "NJ",
    "New Mexico": "NM",
    "New York": "NY",
    "North Carolina": "NC",
    "North Dakota": "ND",
    "Ohio": "OH",
    "Oklahoma": "OK",
    "Oregon": "OR",
    "Pennsylvania": "PA",
    "Rhode Island": "RI",
    "South Carolina": "SC",
    "South Dakota": "SD",
    "Tennessee": "TN",
    "Texas": "TX",
    "Utah": "UT",
    "Vermont": "VT",
    "Virginia": "VA",
    "Washington": "WA",
    "West Virginia": "WV",
    "Wisconsin": "WI",
    "Wyoming": "WY",
    # Territories, as the medians table names them.
    "Guam": "GU",
    "Northern Mariana Islands": "MP",
    "Puerto Rico": "PR",
    "Virgin Islands": "VI",
}

_M = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"


def read_sheet_rows(data: bytes, sheet_index: int = 0) -> list[list[str]]:
    """The first worksheet of an XLSX as rows of cell strings (stdlib: an
    XLSX is a zip of XML). Enough for the UST's flat tables; anything this
    cannot read is a format change a human must look at anyway."""
    with zipfile.ZipFile(BytesIO(data)) as z:
        shared: list[str] = []
        if "xl/sharedStrings.xml" in z.namelist():
            root = ET.fromstring(z.read("xl/sharedStrings.xml"))
            for si in root.findall(f"{_M}si"):
                shared.append("".join(t.text or "" for t in si.iter(f"{_M}t")))
        sheets = sorted(
            n for n in z.namelist() if re.fullmatch(r"xl/worksheets/sheet\d+\.xml", n)
        )
        root = ET.fromstring(z.read(sheets[sheet_index]))
        rows: list[list[str]] = []
        for row in root.iter(f"{_M}row"):
            cells: dict[int, str] = {}
            for c in row.findall(f"{_M}c"):
                ref = c.get("r") or ""
                col_match = re.match(r"[A-Z]+", ref)
                if col_match is None:
                    continue
                idx = 0
                for ch in col_match.group(0):
                    idx = idx * 26 + (ord(ch) - 64)
                v = c.find(f"{_M}v")
                if v is None:
                    is_node = c.find(f"{_M}is")
                    value = (
                        "".join(x.text or "" for x in is_node.iter())
                        if is_node is not None
                        else ""
                    )
                else:
                    value = v.text or ""
                    if c.get("t") == "s":
                        value = shared[int(value)]
                cells[idx - 1] = value
            width = max(cells) + 1 if cells else 0
            rows.append([cells.get(i, "").strip() for i in range(width)])
        return rows


def money(value: str, where: str) -> str:
    """A spreadsheet number as the repo's two-decimal money string. The UST
    files carry whole dollars; anything else is a format change to review."""
    cleaned = value.replace(",", "").replace("$", "").strip()
    if not re.fullmatch(r"[1-9]\d*(\.\d+)?", cleaned):
        raise SystemExit(f"{where}: {value!r} is not a positive dollar figure")
    whole, _, frac = cleaned.partition(".")
    frac = (frac + "00")[:2]
    return f"{whole}.{frac}"


def fetch(url: str) -> tuple[bytes, str]:
    print(f"  fetching {url}")
    # justice.gov answers 403 to urllib's default User-Agent; identify as a
    # plain client the way curl does.
    request = urllib.request.Request(url, headers={"User-Agent": "curl/8"})
    with urllib.request.urlopen(request, timeout=60) as response:
        data = response.read()
    return data, hashlib.sha256(data).hexdigest()


def write_release(
    out_root: Path,
    series_id: str,
    effective: str,
    sequence: int,
    manifest: dict[str, object],
    dataset: dict[str, object],
) -> None:
    dirname = effective if sequence == 1 else f"{effective}+{sequence}"
    release_dir = out_root / series_id / dirname
    if release_dir.exists():
        raise SystemExit(
            f"{release_dir} already exists — releases are append-only; a "
            "correction is a new directory with the next +sequence"
        )
    release_dir.mkdir(parents=True)
    (release_dir / "manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    (release_dir / "dataset.json").write_text(
        json.dumps(dataset, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(f"  wrote {release_dir}")


def manifest_for(
    series_id: str,
    effective: str,
    sequence: int,
    url: str,
    sha256: str,
    notes: str,
) -> dict[str, object]:
    return {
        "series_id": series_id,
        "effective_date": effective,
        "sequence": sequence,
        "source": {"url": url, "published": None, "sha256": sha256},
        "notes": notes,
    }


def source_entry(title: str, url: str, accessed: str) -> dict[str, str]:
    return {"title": title, "url": url, "accessed": accessed}


def ingest_medians(args: argparse.Namespace) -> None:
    url = BASE.format(period=args.period, name="median_income")
    data, sha = fetch(url)
    rows = read_sheet_rows(data)

    medians: dict[str, list[str]] = {}
    additions: set[str] = set()
    for row in rows:
        if not row or not row[0]:
            continue
        name = row[0].strip()
        if name in STATE_CODES:
            values = [v for v in row[1:] if v]
            if len(values) != 4:
                raise SystemExit(f"medians: {name} has {len(values)} columns, not 4")
            medians[STATE_CODES[name]] = [money(v, f"medians {name}") for v in values]
        else:
            footnote = re.search(r"Add \$([\d,]+) for each individual", name)
            if footnote:
                additions.add(money(footnote.group(1), "medians footnote"))
    if len(medians) != len(STATE_CODES):
        missing = sorted(set(STATE_CODES.values()) - set(medians))
        raise SystemExit(f"medians: rows missing for {missing}")
    if len(additions) != 1:
        raise SystemExit(f"medians: excess-person footnotes disagree: {additions}")

    page = f"https://www.justice.gov/ust/means-testing/{args.period}"
    dataset = {
        "kind": "census-median-family-income",
        "verification": "primary",
        "sources": [
            source_entry("UST Census median family income (XLSX)", url, args.accessed),
            source_entry(
                "UST median family income HTML table (same period)",
                f"https://www.justice.gov/ust/eo/bapcpa/{args.period}"
                "/bci_data/median_income_table.htm",
                args.accessed,
            ),
        ],
        "annual_medians": {code: medians[code] for code in sorted(medians)},
        "excess_person_annual_addition": additions.pop(),
        "notes": (
            "Annual median family income by state/territory for household "
            "sizes 1-4 (the table's '1 Earner' column is the one-person "
            "figure, per the UST's own 122A-1 line 13 usage). Households "
            "above 4 add the excess-person figure per person — 12x the "
            "monthly amount in 11 U.S.C. 707(b)(7)(A)(iii), which "
            "code/dollar-amounts carries. Converted mechanically by "
            "scripts/ingest-ust-data.py from the UST XLSX (sha256 in the "
            "manifest)."
        ),
    }
    manifest = manifest_for(
        "ust/census-median-family-income",
        args.effective,
        args.sequence,
        url,
        sha,
        f"Census Bureau median family income as republished by the UST for "
        f"cases filed on or after {args.effective} (period page {page}). "
        "Converted mechanically; spot-check the diff against the UST's HTML "
        "table before merging.",
    )
    write_release(
        args.out_root,
        "ust/census-median-family-income",
        args.effective,
        args.sequence,
        manifest,
        dataset,
    )


def ingest_national(args: argparse.Namespace) -> None:
    url = BASE.format(period=args.period, name="national_expense_standards")
    data, sha = fetch(url)
    rows = read_sheet_rows(data)

    components: list[dict[str, object]] = []
    allowances: list[str] | None = None
    additional: str | None = None
    food_and_clothing: list[str] | None = None
    five_percent_cap: list[str] | None = None
    food_and_clothing_extra: str | None = None
    five_percent_cap_extra: str | None = None
    for row in rows:
        if not row or not row[0]:
            continue
        label = row[0].strip()
        values = [v for v in row[1:] if v]
        if label == "Total":
            if len(values) != 4:
                raise SystemExit("national: Total row does not have 4 columns")
            allowances = [money(v, "national Total") for v in values]
        elif label.startswith("For each additional person"):
            additional = money(values[0], "national additional person")
        elif label == "Food & Clothing" and len(values) == 4:
            food_and_clothing = [money(v, "national food & clothing") for v in values]
        elif label == "5% of Food & Clothing" and len(values) == 4:
            # B122A-2 line 30's cap: the optional additional food-and-clothing
            # allowance, published alongside the components.
            five_percent_cap = [money(v, "national 5% cap") for v in values]
        elif label == "Food & Clothing" and len(values) == 1:
            food_and_clothing_extra = money(values[0], "food & clothing extra person")
        elif label == "5% of Food & Clothing" and len(values) == 1:
            five_percent_cap_extra = money(values[0], "5% cap extra person")
        elif len(values) == 4 and label != "Expense":
            components.append(
                {
                    "item": label,
                    "monthly": [money(v, f"national {label}") for v in values],
                }
            )
    if (
        allowances is None
        or additional is None
        or not components
        or food_and_clothing is None
        or five_percent_cap is None
        or food_and_clothing_extra is None
        or five_percent_cap_extra is None
        or len(food_and_clothing) != 4
        or len(five_percent_cap) != 4
    ):
        raise SystemExit("national: table shape changed — review the XLSX")

    oop_url = BASE.format(period=args.period, name="national_oop_healthcare")
    oop_data, oop_sha = fetch(oop_url)
    oop_rows = read_sheet_rows(oop_data)
    under_65: str | None = None
    over_65: str | None = None
    for row in oop_rows:
        cells = [v for v in row if v]
        if len(cells) == 2 and cells[0] == "Under 65":
            under_65 = money(cells[1], "oop under 65")
        if len(cells) == 2 and cells[0] == "65 and Older":
            over_65 = money(cells[1], "oop 65 and older")
    if under_65 is None or over_65 is None:
        raise SystemExit("oop healthcare: table shape changed — review the XLSX")

    dataset = {
        "kind": "irs-national-standards",
        "verification": "primary",
        "sources": [
            source_entry("UST IRS National Standards (XLSX)", url, args.accessed),
            source_entry(
                "UST IRS out-of-pocket health care standards (XLSX)",
                oop_url,
                args.accessed,
            ),
        ],
        "monthly_allowances": {
            str(size): value for size, value in enumerate(allowances, start=1)
        },
        "each_additional_person": additional,
        "components": components,
        "food_and_clothing": food_and_clothing,
        "food_and_clothing_each_additional_person": food_and_clothing_extra,
        "additional_food_clothing_cap": five_percent_cap,
        "additional_food_clothing_cap_each_additional_person": five_percent_cap_extra,
        "oop_healthcare": {"under_65": under_65, "65_and_older": over_65},
        "notes": (
            "IRS National Standards for food, clothing and other items "
            "(122A-2 line 6) by household size 1-4 plus a per-person "
            "addition above 4, with the component lines as published; the "
            "combined food-and-clothing figure and its published 5% cap "
            "(line 30's optional additional allowance); and the "
            "out-of-pocket health care allowance per person (line 7), "
            "under 65 / 65 and older. Converted mechanically by "
            "scripts/ingest-ust-data.py (sha256s: manifest and sources)."
        ),
    }
    manifest = manifest_for(
        "ust/irs-national-standards",
        args.effective,
        args.sequence,
        url,
        sha,
        f"IRS National Standards as republished by the UST for cases filed "
        f"on or after {args.effective}; includes the out-of-pocket health "
        f"care table ({oop_url}, sha256 {oop_sha}). Converted mechanically; "
        "spot-check the diff against the UST's HTML tables before merging.",
    )
    write_release(
        args.out_root,
        "ust/irs-national-standards",
        args.effective,
        args.sequence,
        manifest,
        dataset,
    )


def ingest_local(args: argparse.Namespace) -> None:
    housing_url = BASE.format(period=args.period, name="housing_util_standards_FIPS")
    housing_data, housing_sha = fetch(housing_url)
    rows = read_sheet_rows(housing_data)  # sheet 1 is the flat all-county table
    header = rows[0]
    expected = [
        "FIPS Code",
        "State Initials",
        "State Name",
        "County",
        "Non-Mort 1",
        "Mort 1",
        "Non-Mort 2",
        "Mort 2",
        "Non-Mort 3",
        "Mort 3",
        "Non-Mort 4",
        "Mort 4",
        "Non-Mort 5+",
        "Mort 5+",
    ]
    if [h.strip() for h in header[: len(expected)]] != expected:
        raise SystemExit(f"housing: header changed — review the XLSX: {header}")

    housing: dict[str, list[dict[str, object]]] = {code: [] for code in LAUNCH_STATES}
    for row in rows[1:]:
        if len(row) < 14 or row[1] not in housing:
            continue
        where = f"housing {row[1]} {row[3]}"
        housing[row[1]].append(
            {
                "fips": row[0],
                "county": row[3],
                "non_mortgage": [money(row[i], where) for i in (4, 6, 8, 10, 12)],
                "mortgage_rent": [money(row[i], where) for i in (5, 7, 9, 11, 13)],
            }
        )
    for code, counties in housing.items():
        if not counties:
            raise SystemExit(f"housing: no counties parsed for {code}")

    transport_url = BASE.format(period=args.period, name="transportation_standards")
    transport_data, transport_sha = fetch(transport_url)
    trows = [[c for c in row if c] for row in read_sheet_rows(transport_data)]
    public_transit: str | None = None
    ownership: dict[str, str] = {}
    region_costs: dict[str, dict[str, str]] = {}
    msa_costs: dict[str, dict[str, str]] = {}
    section = ""
    for row in trows:
        if not row:
            continue
        label = row[0].strip()
        if label in ("Public Transportation", "Ownership Costs", "Operating Costs"):
            section = label
            continue
        if section == "Public Transportation" and label == "National":
            public_transit = money(row[1], "transportation public transit")
        elif section == "Ownership Costs" and label == "National":
            ownership = {
                "one_car": money(row[1], "ownership one car"),
                "two_cars": money(row[2], "ownership two cars"),
            }
        elif section == "Operating Costs" and len(row) == 3 and label != "One Car":
            costs = {
                "one_car": money(row[1], f"operating {label}"),
                "two_cars": money(row[2], f"operating {label}"),
            }
            if label.endswith("Region"):
                region_costs[label.removesuffix("Region").strip().lower()] = costs
            else:
                msa_costs[label] = costs
    if public_transit is None or not ownership or not region_costs:
        raise SystemExit("transportation: table shape changed — review the XLSX")

    # The MSA county definitions live only on the UST's per-region HTML pages;
    # they are curated by hand into --msa-file. Shape:
    #   { "<msa name as the spreadsheet spells it>":
    #       {"state": "FL", "counties": ["Broward", ...]} , ... }
    msa_defs = json.loads(Path(args.msa_file).read_text(encoding="utf-8"))
    operating_msas: dict[str, dict[str, object]] = {}
    for name, definition in msa_defs.items():
        if name not in msa_costs:
            raise SystemExit(f"msa-file names {name!r}, not in the spreadsheet")
        if definition["state"] not in LAUNCH_STATES:
            raise SystemExit(f"msa-file {name!r} is outside the launch states")
        operating_msas[name] = {
            "state": definition["state"],
            "counties": definition["counties"],
            **msa_costs[name],
        }

    dataset = {
        "kind": "irs-local-standards",
        "verification": "primary",
        "sources": [
            source_entry(
                "UST IRS housing and utilities standards by county (XLSX)",
                housing_url,
                args.accessed,
            ),
            source_entry(
                "UST IRS transportation standards (XLSX)", transport_url, args.accessed
            ),
            source_entry(
                "UST South region transportation page (MSA county definitions)",
                f"https://www.justice.gov/ust/eo/bapcpa/{args.period}"
                "/bci_data/IRS_Trans_Exp_Stds_SO.htm",
                args.accessed,
            ),
        ],
        "states": list(LAUNCH_STATES),
        "housing_utilities": {code: housing[code] for code in sorted(housing)},
        "transportation": {
            "public_transportation_national": public_transit,
            "ownership_costs": ownership,
            "operating_costs_region": dict.fromkeys(LAUNCH_STATES, "south"),
            "operating_costs": {"south": region_costs["south"]},
            "operating_costs_msa": {
                name: operating_msas[name] for name in sorted(operating_msas)
            },
        },
        "notes": (
            "IRS Local Standards as the UST republishes them, scoped to the "
            "launch states (ADR 0017): housing/utilities insurance-and-"
            "operating (non_mortgage) and mortgage/rent allowances per county "
            "for household sizes 1,2,3,4,5+ (122A-2 lines 8-9), and the "
            "transportation tables (lines 11-14) — national public transit, "
            "national per-vehicle ownership, and operating costs by Census "
            "region with MSA overrides whose county lists the UST publishes "
            "only on its region HTML pages. All three launch states are in "
            "the South region. Converted mechanically by "
            "scripts/ingest-ust-data.py."
        ),
    }
    manifest = manifest_for(
        "ust/irs-local-standards",
        args.effective,
        args.sequence,
        housing_url,
        housing_sha,
        f"IRS Local Standards (housing/utilities by county + transportation) "
        f"as republished by the UST for cases filed on or after "
        f"{args.effective}, scoped to the launch states FL/GA/TX (ADR 0017). "
        f"Transportation XLSX: {transport_url} (sha256 {transport_sha}). "
        "Converted mechanically; spot-check the diff against the UST's HTML "
        "tables before merging.",
    )
    write_release(
        args.out_root,
        "ust/irs-local-standards",
        args.effective,
        args.sequence,
        manifest,
        dataset,
    )


def ingest_ch13_multipliers(args: argparse.Namespace) -> None:
    url = BASE.format(period=args.period, name="ch13_exp_mult")
    data, sha = fetch(url)
    rows = read_sheet_rows(data)

    multipliers: dict[str, str] = {}
    source_note = ""
    for row in rows:
        cells = [c for c in row if c]
        if len(cells) == 1 and cells[0].startswith("Effective as of"):
            source_note = cells[0]
        if len(cells) != 2 or cells[0] in ("JUDICIAL DISTRICT",):
            continue
        district, raw = cells
        try:
            fraction = Decimal(raw)
        except InvalidOperation:
            continue
        if not Decimal("0") <= fraction < Decimal("1"):
            raise SystemExit(f"ch13 multiplier {district}: {raw!r} is not a fraction")
        # The XLSX stores binary-float noise (9.2999...E-2); the published
        # figures are tenths of a percent, so quantize to 4 places and strip.
        multipliers[district] = str(fraction.quantize(Decimal("0.0001")).normalize())
    if len(multipliers) < 50:
        raise SystemExit(
            f"ch13 multipliers: only {len(multipliers)} districts parsed — "
            "the table shape changed; review the XLSX"
        )

    dataset = {
        "kind": "ch13-admin-multipliers",
        "verification": "primary",
        "sources": [
            source_entry(
                "UST Chapter 13 administrative expense multipliers (XLSX)",
                url,
                args.accessed,
            )
        ],
        "multipliers": {name: multipliers[name] for name in sorted(multipliers)},
        "notes": (
            "Schedule of actual administrative expenses of administering a "
            "Chapter 13 plan, per judicial district, as fractions "
            "(§ 707(b)(2)(A)(ii)(III); B122A-2 line 36 multiplies the "
            "projected plan payment by this figure). District names are the "
            f"UST's own ('Middle Florida'). {source_note}. Converted "
            "mechanically by scripts/ingest-ust-data.py."
        ),
    }
    manifest = manifest_for(
        "ust/ch13-admin-multipliers",
        args.effective,
        args.sequence,
        url,
        sha,
        f"Chapter 13 administrative expense multipliers as published by the "
        f"UST for cases filed on or after {args.effective}. Converted "
        "mechanically; spot-check the diff against the UST's HTML table "
        "before merging.",
    )
    write_release(
        args.out_root,
        "ust/ch13-admin-multipliers",
        args.effective,
        args.sequence,
        manifest,
        dataset,
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for name, handler in (
        ("medians", ingest_medians),
        ("national", ingest_national),
        ("local", ingest_local),
        ("ch13-multipliers", ingest_ch13_multipliers),
    ):
        p = sub.add_parser(name)
        p.add_argument("--period", required=True, help="UST period, YYYYMMDD")
        p.add_argument("--effective", required=True, help="effective date, YYYY-MM-DD")
        p.add_argument("--sequence", type=int, default=1)
        p.add_argument(
            "--accessed", default=date.today().isoformat(), help="access date"
        )
        p.add_argument("--out-root", type=Path, default=DEFAULT_OUT_ROOT)
        if name == "local":
            p.add_argument(
                "--msa-file",
                required=True,
                help="curated MSA county definitions JSON (see module docstring)",
            )
        p.set_defaults(handler=handler)
    args = parser.parse_args()
    if not re.fullmatch(r"\d{8}", args.period):
        raise SystemExit("--period must be YYYYMMDD")
    date.fromisoformat(args.effective)  # validates
    args.handler(args)


if __name__ == "__main__":
    sys.exit(main())
