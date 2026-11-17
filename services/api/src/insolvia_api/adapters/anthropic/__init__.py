"""Anthropic API implementations of core ports (issue #97, ADR 0019).

WORKER IMAGE ONLY: the `anthropic` SDK is installed by
requirements-worker.txt, per ADR 0015's rule that pipeline weight never
lands in the API image. Nothing under api/ or the api_lambda entrypoint may
import this package — the layering test enforces the layer, and the API
image's missing dependency would make such an import loud anyway.
"""
