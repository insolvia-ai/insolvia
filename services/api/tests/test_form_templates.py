"""Checks over the form template registry and its loader (issue #93).

Three layers:

- the loader is exercised against malformed releases built in tmp_path, so a
  bad manifest, a template whose bytes are not the official PDF's, or a spec
  that no longer covers the dump is a loud ValueError;
- the committed registry is loaded for real and swept with consistency
  checks — every series resolves, every template hashes to its recorded
  official sha256, resolution and pinning follow effective-dating.md;
- the registry's spec/acroform copies are diffed against `forms/` — the
  curation workspace holds exactly the current revision, so while the two
  describe the same revision they must be byte-identical, and bumping one
  without the other fails here rather than shipping a template the checker
  never saw.
"""

import dataclasses
import hashlib
import json
from datetime import date
from pathlib import Path

import pytest
from insolvia_api.core.form_templates import (
    FormRelease,
    form_registry,
    form_releases,
    form_revisions_as_of,
    form_series_ids,
    get_form,
    latest_form,
    load_form_registry,
    pick_form_release,
    resolve_form,
)

REPO_ROOT = Path(__file__).resolve().parents[3]
REGISTRY_ROOT = (
    Path(__file__).resolve().parents[1] / "src" / "insolvia_api" / "regulatory"
)

ALL_RELEASES: list[FormRelease] = [
    release for series in form_registry().values() for release in series
]


def _release_params() -> list[object]:
    return [pytest.param(release, id=release.release_id) for release in ALL_RELEASES]


# --- the committed registry loads, whole -------------------------------------


def test_the_registry_holds_the_expected_series() -> None:
    assert form_series_ids() == ("form/b101", "form/b106i")


@pytest.mark.parametrize("release", _release_params())
def test_every_template_is_the_official_pdf(release: FormRelease) -> None:
    assert hashlib.sha256(release.template_pdf).hexdigest() == release.source_sha256
    assert release.source_url.startswith("https://www.uscourts.gov/")
    assert release.template_pdf.startswith(b"%PDF-")


@pytest.mark.parametrize("release", _release_params())
def test_every_fillable_widget_is_claimed_exactly_once(
    release: FormRelease,
) -> None:
    fillable = {n for n, w in release.widgets.items() if w.kind != "pushbutton"}
    claimed = [n for f in release.fields for n in f.pdf_names]
    assert sorted(claimed) == sorted(fillable)


@pytest.mark.parametrize("release", _release_params())
def test_radio_options_are_the_pdf_export_states(release: FormRelease) -> None:
    for field in release.fields:
        if field.type != "radio":
            continue
        states = {s for n in field.pdf_names for s in release.widgets[n].states}
        assert {o.value for o in field.options} == states, field.id


def test_widget_metadata_survives_loading() -> None:
    b101 = latest_form("form/b101")
    district = b101.widgets["Bankruptcy District Information"]
    assert district.kind == "choice"
    assert "Middle District of Florida" in district.choice_options
    # The PDF's own character caps — the fill engine refuses what the box
    # would silently truncate.
    assert b101.widgets["Debtor1.SSNum"].max_len == 4
    assert b101.widgets["Debtor1.State"].max_len == 2


def test_field_lookup_by_id() -> None:
    release = latest_form("form/b101")
    assert release.field("caption.district").type == "district"
    with pytest.raises(KeyError):
        release.field("no_such_field")


# --- the registry copies do not drift from forms/ ----------------------------


@pytest.mark.parametrize("release", _release_params())
def test_latest_release_matches_the_forms_workspace(
    release: FormRelease,
) -> None:
    """forms/ holds exactly the current revision of every form; the latest
    committed release must be byte-identical to it. A new revision therefore
    lands as forms/ update + new release directory in one PR, and a forms/
    edit that skips the registry (or vice versa) fails here."""
    if release is not latest_form(release.series_id):
        pytest.skip("only the latest release mirrors the workspace")
    release_dir = REGISTRY_ROOT / "form" / release.form / release.pin
    for copied, workspace in (
        ("spec.json", REPO_ROOT / "forms" / "specs" / f"{release.form}.json"),
        (
            "acroform.json",
            REPO_ROOT / "forms" / "acroform" / f"{release.form}.json",
        ),
    ):
        assert (release_dir / copied).read_bytes() == workspace.read_bytes(), (
            f"{release.release_id}/{copied} differs from {workspace} — "
            "ingest a new release or re-copy the current one"
        )


# --- resolution (effective-dating.md) ----------------------------------------


def test_resolve_picks_the_release_in_force() -> None:
    release = resolve_form("form/b101", date(2026, 9, 1))
    assert release.release_id == "form/b101@2024-06-22"
    assert release.revision == "06/24"


def test_resolution_refuses_dates_before_the_series_begins() -> None:
    with pytest.raises(LookupError, match="refusing"):
        resolve_form("form/b101", date(2020, 1, 1))


def test_get_returns_a_pinned_release_forever() -> None:
    release = get_form("form/b106i", "form/b106i@2015-12-01")
    assert release.effective_date == date(2015, 12, 1)
    with pytest.raises(KeyError):
        get_form("form/b106i", "form/b106i@1999-01-01")


def test_unknown_series_is_a_key_error() -> None:
    with pytest.raises(KeyError):
        form_releases("form/b999")


def test_corrections_win_ties_by_sequence() -> None:
    base = latest_form("form/b101")
    correction = dataclasses.replace(base, sequence=2)
    picked = pick_form_release((base, correction), date(2026, 1, 1))
    assert picked.sequence == 2
    assert picked.release_id == "form/b101@2024-06-22+2"
    assert picked.pin == "2024-06-22+2"


def test_form_revisions_as_of_is_the_case_pin_map() -> None:
    pins = form_revisions_as_of(date(2026, 9, 1))
    assert pins == {
        "form/b101": "2024-06-22",
        "form/b106i": "2015-12-01",
    }
    # And every pin round-trips through get_form, which is what makes a
    # filed case reproducible forever.
    for series_id, pin in pins.items():
        assert get_form(series_id, f"{series_id}@{pin}").pin == pin


# --- the loader refuses malformed releases -----------------------------------


TEMPLATE = b"%PDF-1.7 fake template bytes"
TEMPLATE_SHA = hashlib.sha256(TEMPLATE).hexdigest()


def _write_release(root: Path, **overrides: object) -> Path:
    """A minimal internally consistent release; each test corrupts one facet."""
    release = root / "form" / "b900" / "2026-01-01"
    release.mkdir(parents=True)
    manifest = {
        "series_id": "form/b900",
        "effective_date": "2026-01-01",
        "sequence": 1,
        "source": {
            "url": "https://www.uscourts.gov/example.pdf",
            "published": None,
            "sha256": TEMPLATE_SHA,
        },
        "notes": "a test release",
    }
    acroform = {
        "form": "b900",
        "revision": "01/26",
        "effective_date": "2026-01-01",
        "source": {"pdf_sha256": TEMPLATE_SHA},
        "fields": [
            {"name": "Debtor name", "kind": "text", "pages": [1]},
            {
                "name": "Chapter",
                "kind": "radio",
                "pages": [1],
                "states": ["7", "13"],
            },
            {"name": "Button.Print", "kind": "pushbutton", "pages": [1]},
        ],
    }
    spec = {
        "form": "b900",
        "official_number": "900",
        "title": "Test form",
        "revision": "01/26",
        "effective_date": "2026-01-01",
        "parts": [],
        "fields": [
            {
                "id": "debtor_name",
                "label": "Name",
                "type": "text",
                "maps_to": {"entity": "debtor", "attribute": "name"},
                "pdf": {"names": ["Debtor name"]},
            },
            {
                "id": "chapter",
                "label": "Chapter",
                "type": "radio",
                "options": [{"value": "7"}, {"value": "13"}],
                "maps_to": {"entity": "case", "attribute": "chapter"},
                "pdf": {"names": ["Chapter"]},
            },
        ],
    }
    template = TEMPLATE
    for key, value in overrides.items():
        if key == "template":
            assert isinstance(value, bytes)
            template = value
        elif key == "manifest":
            assert isinstance(value, dict)
            manifest = {**manifest, **value}
        elif key == "acroform":
            assert isinstance(value, dict)
            acroform = {**acroform, **value}
        elif key == "spec":
            assert isinstance(value, dict)
            spec = {**spec, **value}
        else:  # pragma: no cover - a typo in the test itself
            raise AssertionError(key)
    (release / "manifest.json").write_text(json.dumps(manifest))
    (release / "acroform.json").write_text(json.dumps(acroform))
    (release / "spec.json").write_text(json.dumps(spec))
    (release / "template.pdf").write_bytes(template)
    return release


def test_a_wellformed_release_loads(tmp_path: Path) -> None:
    _write_release(tmp_path)
    registry = load_form_registry(tmp_path)
    (release,) = registry["form/b900"]
    assert release.release_id == "form/b900@2026-01-01"
    assert release.field("chapter").pdf_names == ("Chapter",)


@pytest.mark.parametrize(
    ("overrides", "problem"),
    [
        pytest.param(
            {"template": b"%PDF-1.7 tampered"},
            "not the official PDF",
            id="template-bytes-differ-from-recorded-sha",
        ),
        pytest.param(
            {"manifest": {"series_id": "form/other"}},
            "series_id",
            id="manifest-series-mismatch",
        ),
        pytest.param(
            {"manifest": {"effective_date": "2026-02-02"}},
            "disagrees with the path",
            id="manifest-date-mismatch",
        ),
        pytest.param(
            {"manifest": {"notes": " "}},
            "notes missing",
            id="manifest-notes-empty",
        ),
        pytest.param(
            {"acroform": {"source": {"pdf_sha256": "0" * 64}}},
            "disagrees with the manifest",
            id="acroform-hash-mismatch",
        ),
        pytest.param(
            {"spec": {"revision": "02/26"}},
            "revision disagrees",
            id="spec-revision-mismatch",
        ),
        pytest.param(
            {"spec": {"effective_date": "2026-02-02"}},
            "disagrees with the path",
            id="spec-date-mismatch",
        ),
        pytest.param(
            {"spec": {"form": "b901"}},
            "disagrees with the series id",
            id="spec-form-mismatch",
        ),
        pytest.param(
            {
                "spec": {
                    "fields": [
                        {
                            "id": "debtor_name",
                            "label": "Name",
                            "type": "text",
                            "maps_to": {},
                            "pdf": {"names": ["Debtor name"]},
                        }
                    ]
                }
            },
            "not claimed by any spec field",
            id="uncovered-widget",
        ),
        pytest.param(
            {
                "spec": {
                    "fields": [
                        {
                            "id": "debtor_name",
                            "label": "Name",
                            "type": "text",
                            "maps_to": {},
                            "pdf": {"names": ["Debtor name", "Chapter"]},
                        }
                    ]
                }
            },
            "claims widget kinds",
            id="text-field-claiming-a-button",
        ),
        pytest.param(
            {
                "spec": {
                    "fields": [
                        {
                            "id": "debtor_name",
                            "label": "Name",
                            "type": "text",
                            "maps_to": {},
                            "pdf": {"names": ["No such box"]},
                        }
                    ]
                }
            },
            "unknown PDF field",
            id="claim-resolves-nowhere",
        ),
    ],
)
def test_malformed_releases_are_refused(
    tmp_path: Path, overrides: dict[str, object], problem: str
) -> None:
    _write_release(tmp_path, **overrides)
    with pytest.raises(ValueError, match=problem):
        load_form_registry(tmp_path)


def test_radio_options_must_match_the_pdf_states(tmp_path: Path) -> None:
    release = _write_release(tmp_path)
    spec = json.loads((release / "spec.json").read_text())
    spec["fields"][1]["options"] = [{"value": "7"}, {"value": "11"}]
    (release / "spec.json").write_text(json.dumps(spec))
    with pytest.raises(ValueError, match="export states"):
        load_form_registry(tmp_path)


def test_a_release_missing_its_template_is_refused(tmp_path: Path) -> None:
    release = _write_release(tmp_path)
    (release / "template.pdf").unlink()
    with pytest.raises(ValueError, match=r"template\.pdf missing"):
        load_form_registry(tmp_path)
