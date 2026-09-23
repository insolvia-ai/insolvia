"""The forms hub (issue 13.2 / #343): per-form grouping of the completeness
gate, per-form metrics, and the single-form preview render.

The reference case from test_form_projections.py is the fixture here too, for
the same reason test_packet_assembly.py uses it: it is the one case proven by
the goldens to project every form cleanly, so it is what a "renders
successfully" test has to build on.
"""

from __future__ import annotations

from dataclasses import replace
from datetime import date

import pytest
from insolvia_api.core.case_summary import summarise
from insolvia_api.core.form_projections.shared import claims_of
from insolvia_api.core.forms_hub import (
    FORM_PROBLEM_SOURCES,
    form_preview_object_key,
    forms_hub,
    group_problems,
    render_form_preview,
    resolve_case_form,
)
from insolvia_api.core.packet_assembly import (
    PacketProblem,
    packet_form_series,
    to_case_file,
)
from insolvia_core.errors import ValidationError
from insolvia_core.expenses import HOUSEHOLD, HouseholdBody

from tests.unit.test_packet_assembly import CASE_ID, TODAY, _entity, reference_case_data

# ── forms_hub: one row per required form ─────────────────────────


def test_every_required_form_gets_a_row_in_filing_order():
    data = reference_case_data()
    summaries = forms_hub(data, as_of=TODAY)
    assert tuple(s.series for s in summaries) == packet_form_series(data)


def test_a_clean_case_reports_every_row_with_no_problems():
    summaries = forms_hub(reference_case_data(), as_of=TODAY)
    assert all(s.problems == () for s in summaries)


def test_every_row_names_its_form_and_title():
    summaries = forms_hub(reference_case_data(), as_of=TODAY)
    by_series = {s.series: s for s in summaries}
    b101 = by_series["form/b101"]
    assert b101.form == "b101"
    assert b101.title != ""
    assert b101.official_number != ""


# ── Metrics: counts for A/B-H, totals for I and J ────────────────


def test_schedule_ab_counts_the_assets():
    data = reference_case_data()
    summaries = {s.series: s for s in forms_hub(data, as_of=TODAY)}
    metric = summaries["form/b106ab"].metric
    assert metric is not None
    assert metric.kind == "count"
    assert metric.value == str(len(data.assets))


def test_schedule_c_counts_the_exemptions():
    data = reference_case_data()
    summaries = {s.series: s for s in forms_hub(data, as_of=TODAY)}
    metric = summaries["form/b106c"].metric
    assert metric is not None
    assert metric.value == str(len(data.exemptions))


def test_schedule_d_counts_only_secured_claims():
    data = reference_case_data()
    case_file = to_case_file(data)
    summaries = {s.series: s for s in forms_hub(data, as_of=TODAY)}
    metric = summaries["form/b106d"].metric
    assert metric is not None
    assert metric.value == str(len(claims_of(case_file, "secured")))
    # Sanity: the reference case actually carries at least one of each class,
    # so this is not vacuously true against every claim.
    assert len(case_file.claims) > len(claims_of(case_file, "secured"))


def test_schedule_ef_counts_priority_and_nonpriority_claims_together():
    data = reference_case_data()
    case_file = to_case_file(data)
    summaries = {s.series: s for s in forms_hub(data, as_of=TODAY)}
    metric = summaries["form/b106ef"].metric
    assert metric is not None
    expected = len(claims_of(case_file, "priority_unsecured", "nonpriority_unsecured"))
    assert metric.value == str(expected)


def test_schedule_g_counts_contracts_and_leases():
    data = reference_case_data()
    summaries = {s.series: s for s in forms_hub(data, as_of=TODAY)}
    metric = summaries["form/b106g"].metric
    assert metric is not None
    assert metric.value == str(len(data.contract_leases))


def test_schedule_h_counts_codebtors_and_household_members():
    data = reference_case_data()
    summaries = {s.series: s for s in forms_hub(data, as_of=TODAY)}
    metric = summaries["form/b106h"].metric
    assert metric is not None
    expected = len(data.codebtors) + len(data.community_household_members)
    assert metric.value == str(expected)


def test_schedule_i_and_j_report_the_summary_dollar_totals():
    data = reference_case_data()
    totals = summarise(data).totals
    summaries = {s.series: s for s in forms_hub(data, as_of=TODAY)}
    income_metric = summaries["form/b106i"].metric
    expense_metric = summaries["form/b106j"].metric
    assert income_metric is not None
    assert income_metric.kind == "total"
    assert income_metric.value == str(totals.monthly_income)
    assert expense_metric is not None
    assert expense_metric.kind == "total"
    assert expense_metric.value == str(totals.monthly_expenses)


@pytest.mark.parametrize(
    "series",
    ["form/b101", "form/b106sum", "form/b106dec", "form/b107", "form/b122a1"],
)
def test_forms_the_issue_gives_no_metric_carry_none(series):
    summaries = {s.series: s for s in forms_hub(reference_case_data(), as_of=TODAY)}
    assert summaries[series].metric is None


# ── group_problems: which form each completeness problem belongs to ─────


def test_a_missing_debtor_1_blocks_every_required_form():
    data = replace(reference_case_data(), debtors=())
    grouped = group_problems(data)
    for series in packet_form_series(data):
        assert any(p.source == "debtors" for p in grouped[series])


def test_a_missing_petition_blocks_only_b101():
    data = replace(reference_case_data(), petitions=())
    grouped = group_problems(data)
    for series, problems in grouped.items():
        has_petition_problem = any(p.source == "petitions" for p in problems)
        assert has_petition_problem == (series == "form/b101")


def test_a_household_problem_reaches_both_j_forms_when_both_file():
    data = reference_case_data()
    # A valid debtor_2_separate household brings B106J-2 into the required
    # set; a THIRD, unresolved household is what supplies the problem this
    # test follows — its source ("households") is not specific to either
    # schedule, so it must reach both.
    separate = _entity(
        HOUSEHOLD,
        HouseholdBody(which_household="debtor_2_separate", separate_household=True),
        "hh-separate",
        9_994,
    )
    unresolved = _entity(
        HOUSEHOLD, HouseholdBody(which_household=None), "hh-bad", 9_995
    )
    with_bad = replace(data, households=(*data.households, separate, unresolved))
    assert "form/b106j2" in packet_form_series(with_bad)
    grouped = group_problems(with_bad)
    assert any(p.item_id == "hh-bad" for p in grouped["form/b106j"])
    assert any(p.item_id == "hh-bad" for p in grouped["form/b106j2"])


def test_group_problems_never_names_a_form_the_case_does_not_file():
    # The reference case keeps one shared household, so B106J-2 is not part
    # of its required set — a household problem must not invent a row for it.
    data = reference_case_data()
    assert "form/b106j2" not in packet_form_series(data)
    grouped = group_problems(data)
    assert "form/b106j2" not in grouped


def test_every_completeness_source_with_a_form_mapping_is_reachable():
    # FORM_PROBLEM_SOURCES only matters for sources completeness_problems can
    # actually emit; this pins that every one of its keys names a real series
    # in the packet's own set, so a typo here cannot silently orphan a source.
    from insolvia_api.core.packet_assembly import PACKET_FORM_SERIES

    for series_list in FORM_PROBLEM_SOURCES.values():
        for series in series_list:
            assert series in PACKET_FORM_SERIES


# ── resolve_case_form: float, then pin (effective-dating.md) ────


def test_a_floating_case_resolves_the_currently_effective_release():
    data = reference_case_data()
    release = resolve_case_form(data.case, "form/b101", as_of=TODAY)
    assert release.series_id == "form/b101"
    assert release.effective_date <= TODAY


def test_a_pinned_case_uses_the_pin_rather_than_as_of():
    data = reference_case_data()
    release = resolve_case_form(data.case, "form/b101", as_of=TODAY)
    pinned_case = replace(data.case, form_revisions={"form/b101": release.pin})
    # A far-future as_of would resolve a different (later) release were the
    # pin not honoured — this proves the pin wins.
    far_future = date(2099, 1, 1)
    resolved = resolve_case_form(pinned_case, "form/b101", as_of=far_future)
    assert resolved.release_id == release.release_id


def test_an_unresolvable_as_of_raises_lookup_error():
    data = reference_case_data()
    with pytest.raises(LookupError):
        resolve_case_form(data.case, "form/b101", as_of=date(1990, 1, 1))


# ── render_form_preview: the packet's gate, one form at a time ──


def test_a_clean_case_renders_bytes_that_start_with_the_pdf_magic():
    data = reference_case_data()
    outcome = render_form_preview(data, "form/b106g", as_of=TODAY)
    assert isinstance(outcome, bytes)
    assert outcome[:5] == b"%PDF-"


def test_a_blocked_case_returns_problems_instead_of_bytes():
    data = replace(reference_case_data(), debtors=())
    outcome = render_form_preview(data, "form/b101", as_of=TODAY)
    assert not isinstance(outcome, bytes)
    assert isinstance(outcome, tuple)
    assert all(isinstance(p, PacketProblem) for p in outcome)
    assert outcome != ()


def test_render_never_emits_bytes_alongside_problems():
    # The packet's own contract, applied to one form: renders, or refuses
    # with the reasons, never both and never a partial form.
    data = replace(reference_case_data(), debtors=())
    outcome = render_form_preview(data, "form/b106g", as_of=TODAY)
    assert isinstance(outcome, tuple)
    # b106g itself has no structural problem of its own here — the refusal
    # is entirely the universal "debtors" one, proving the universal gate
    # actually blocks a form whose own source is otherwise clean.
    assert any(p.source == "debtors" for p in outcome)


def test_an_unresolvable_release_is_reported_as_a_problem_not_raised():
    data = reference_case_data()
    outcome = render_form_preview(data, "form/b101", as_of=date(1990, 1, 1))
    assert isinstance(outcome, tuple)
    assert any(p.source == "form/b101" for p in outcome)


# ── form_preview_object_key ──────────────────────────────────────


def test_the_object_key_is_scoped_under_the_case_and_a_fresh_uuid():
    key = form_preview_object_key(CASE_ID, "22222222-3333-4444-8888-000000000099")
    assert key == f"form-previews/{CASE_ID}/22222222-3333-4444-8888-000000000099.pdf"


def test_the_object_key_refuses_a_non_uuid_input():
    with pytest.raises(ValidationError):
        form_preview_object_key("not-a-uuid", "22222222-3333-4444-8888-000000000099")
