#!/usr/bin/env python3
"""Freshness tripwire for the UST means-testing datasets (issue #99).

The regulatory source register's demand: a release the authority has made
effective that we have not ingested means the product is computing on stale
data — an incident, not a surprise. This script is the automated half of
that promise (effective-dating.md, "The refresh process", step 1): it asks
justice.gov what the current means-testing period is, downloads that
period's spreadsheets, and compares their SHA-256 digests against what the
committed registry ingested. Any artifact the registry has never seen means
a human must run scripts/ingest-ust-data.py and review the diff.

It also watches the § 104(b) horizon: when today is within the warning
window of the `code/dollar-amounts` series' recorded next adjustment and no
release covering it has been ingested, that is the same incident about to
happen.

Run by .github/workflows/regulatory-refresh.yml on a weekly cron (the
failure opens/updates a GitHub issue so the alert reaches a human, not a
log), and runnable locally at any time:

    python3 scripts/check-ust-data.py

Exit codes: 0 current · 1 stale (new data to ingest) · 2 could not verify
(fetch/parse failure — treat as an alert too; silence is the only green).

Stdlib only, like every check in this repo that CI runs on a bare python3.
"""

from __future__ import annotations

import hashlib
import json
import re
import sys
import urllib.request
from datetime import date, timedelta
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
REGISTRY = REPO_ROOT / "services/api/src/insolvia_api/regulatory"

MEANS_TESTING_URL = "https://www.justice.gov/ust/means-testing"
DOC_BASE = "https://www.justice.gov/ust/eo/bapcpa/{period}/bci_data/docs/{name}.xlsx"

# Every spreadsheet a period publishes that some committed release ingests,
# mapped to the series directory whose committed digests must include it.
PERIOD_FILES = {
    "median_income": "ust/census-median-family-income",
    "national_expense_standards": "ust/irs-national-standards",
    "national_oop_healthcare": "ust/irs-national-standards",
    "housing_util_standards_FIPS": "ust/irs-local-standards",
    "transportation_standards": "ust/irs-local-standards",
    "ch13_exp_mult": "ust/ch13-admin-multipliers",
}

# How far ahead of a recorded § 104 next_adjustment the alarm trips. Two
# months is enough to ingest a Federal Register notice published ~Feb 1 for
# an Apr 1 effective date.
SECTION_104_WARNING = timedelta(days=60)

_SHA_RE = re.compile(r"\b[0-9a-f]{64}\b")
_PERIOD_RE = re.compile(r'value="/ust/means-testing/(\d{8})"')


def fetch(url: str) -> bytes:
    # justice.gov answers 403 to urllib's default User-Agent.
    request = urllib.request.Request(url, headers={"User-Agent": "curl/8"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read()


def latest_period() -> str:
    """The newest period in the means-testing page's case-filed dropdown."""
    page = fetch(MEANS_TESTING_URL).decode("utf-8", errors="replace")
    periods = _PERIOD_RE.findall(page)
    if not periods:
        raise RuntimeError(
            "no /ust/means-testing/YYYYMMDD periods found on the page — the "
            "page layout changed; update this script's parser"
        )
    return max(periods)


def committed_digests(series: str) -> set[str]:
    """Every SHA-256 a series' committed releases record — the manifests'
    source.sha256 plus any digest their notes mention (the multi-file
    releases record secondary files' digests in notes)."""
    series_dir = REGISTRY / series
    if not series_dir.is_dir():
        raise RuntimeError(f"registry has no series directory {series}")
    digests: set[str] = set()
    for manifest_path in sorted(series_dir.glob("*/manifest.json")):
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        source = manifest.get("source") or {}
        sha = source.get("sha256")
        if isinstance(sha, str):
            digests.add(sha.lower())
        digests.update(
            found.lower() for found in _SHA_RE.findall(manifest.get("notes", ""))
        )
    if not digests:
        raise RuntimeError(f"{series}: no committed digests found")
    return digests


def check_period_files(period: str) -> list[str]:
    problems: list[str] = []
    digests_by_series = {
        series: committed_digests(series) for series in set(PERIOD_FILES.values())
    }
    for name, series in PERIOD_FILES.items():
        url = DOC_BASE.format(period=period, name=name)
        digest = hashlib.sha256(fetch(url)).hexdigest()
        if digest in digests_by_series[series]:
            print(f"  current: {name}.xlsx ({period}) already ingested by {series}")
        else:
            problems.append(
                f"{series} has never ingested {url} (sha256 {digest}) — the "
                f"UST's {period} period carries data the registry lacks. Run "
                "scripts/ingest-ust-data.py and review the diff; the notice "
                "on https://www.justice.gov/ust/means-testing states the "
                "effective date."
            )
    return problems


def check_section_104_horizon(today: date) -> list[str]:
    """Warn when the recorded next § 104 adjustment is near and no release
    covering it exists yet."""
    series_dir = REGISTRY / "code/dollar-amounts"
    adjustments: set[date] = set()
    effectives: set[date] = set()
    for release_dir in sorted(p for p in series_dir.iterdir() if p.is_dir()):
        effectives.add(date.fromisoformat(release_dir.name.split("+")[0]))
        payload = json.loads((release_dir / "amounts.json").read_text(encoding="utf-8"))
        for amount in payload.get("amounts", []):
            next_adjustment = amount.get("next_adjustment")
            if isinstance(next_adjustment, str):
                adjustments.add(date.fromisoformat(next_adjustment))
    problems: list[str] = []
    for adjustment in sorted(adjustments):
        if adjustment <= today + SECTION_104_WARNING and not any(
            effective >= adjustment for effective in effectives
        ):
            problems.append(
                f"code/dollar-amounts records a § 104(b) adjustment effective "
                f"{adjustment.isoformat()} and no release covers it — ingest "
                "the new Federal Register notice (published ~February of the "
                "adjustment year) as the next release of the series."
            )
        else:
            print(f"  current: § 104 horizon {adjustment.isoformat()} covered or far")
    return problems


def main() -> int:
    try:
        period = latest_period()
        print(f"latest UST means-testing period: {period}")
        problems = check_period_files(period)
        problems.extend(check_section_104_horizon(date.today()))
    except Exception as error:
        print(f"could not verify freshness: {error}", file=sys.stderr)
        return 2
    if problems:
        print(f"\n{len(problems)} dataset(s) stale:", file=sys.stderr)
        for problem in problems:
            print(f"  {problem}", file=sys.stderr)
        return 1
    print("OK — the registry matches the UST's current period")
    return 0


if __name__ == "__main__":
    sys.exit(main())
