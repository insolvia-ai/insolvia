"""The MCP service's test suite. START HERE.

    tests/
      conftest.py    a real RSA keypair, Cognito-shaped tokens, memory stores
      paths.py       where services/mcp is, counted ONCE
      unit/          pytest over core/, auth, and the JSON-RPC seam   every PR

One tier today. `unit/` is what a bare `pytest` runs (`testpaths` in
pyproject.toml), what `scripts/dev-test.sh` runs, and what the `MCP service`
PR check runs. The deployed service is smoke-tested by `mcp-staging.yml` and
`mcp-prod.yml` after each deploy.

An `integration/` tier, when one earns its place, follows services/api's
shape: its own directory, its own conftest, gated on `INSOLVIA_INTEGRATION=1`.
ADR 0021 owns the tiers.
"""
