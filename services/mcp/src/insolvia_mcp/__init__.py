"""Insolvia's MCP service (ADR 0016, issues #261/#262).

The remote MCP server of docs/reference/mcp-surface.md: eight tools over the
shared case domain (`insolvia_core`), speaking Streamable HTTP on Lambda.
An MCP client is a client (ADR 0001) — this service brokers every read, an
agent write lands as a candidate awaiting human review (ADR 0013), and every
call resolves the caller's firm permissions from the store (ADR 0009).
"""
