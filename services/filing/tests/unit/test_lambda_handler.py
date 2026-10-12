"""The Lambda entrypoint: one record per invocation through `run_filing`, and
the deploy smoke test's exact event answered `dropped` with nothing written."""

from __future__ import annotations

from insolvia_filing.entrypoints import filing_lambda


def test_the_deploy_smoke_event_is_dropped_and_writes_nothing(filing, monkeypatch):
    monkeypatch.setattr(filing_lambda, "_deps", filing.deps())

    response = filing_lambda.handler(
        {"Records": [{"body": "deploy-smoke: not a filing job"}]}, None
    )

    assert response == {"outcomes": ["dropped"]}
    assert filing.filings.items() == []


def test_a_real_job_runs_through_the_worker(filing, monkeypatch):
    monkeypatch.setattr(filing_lambda, "_deps", filing.deps())
    _, body = filing.approve()

    response = filing_lambda.handler({"Records": [{"body": body}]}, None)

    assert response == {"outcomes": ["filed"]}
    assert filing.court.submissions == 1
