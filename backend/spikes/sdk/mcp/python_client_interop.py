"""Drive the C# spike server with the Python MCP SDK 2.1.1 *client* (#515).

The Python server is what harnesses talk to today; this checks the other
direction of the same wire: a real 2026-07-28 client (server/discover probe,
per-request _meta, resultType handling) against the C# server.

  cd backend/spikes/sdk/mcp && dotnet run > run.log &      # prints SPIKE_TOKEN=...
  python python_client_interop.py run.log      # needs services/mcp's venv (mcp==2.1.1)
"""
import asyncio
import re
import sys

import httpx2 as httpx
from mcp.client import Client
from mcp.client._transport import Transport  # noqa: F401
from mcp.client.streamable_http import streamable_http_client

URL = "http://127.0.0.1:8788/mcp"


async def main(mode: str = "auto") -> None:
    token = re.search(r"SPIKE_TOKEN=(\S+)", open(sys.argv[1]).read()).group(1)
    http = httpx.AsyncClient(headers={"Authorization": f"Bearer {token}"})
    async with Client(streamable_http_client(URL, http_client=http), mode=mode) as client:
        print("negotiated:", getattr(client, "protocol_version", None))
        tools = await client.list_tools()
        print("tools:", [t.name for t in tools.tools])
        who = await client.call_tool("whoami", {})
        print("whoami structured:", who.structured_content)
        cases = await client.call_tool("list_cases", {"status": "intake"})
        print("list_cases structured:", cases.structured_content)
        bad = await client.call_tool("list_cases", {"status": "bogus"})
        print("refusal isError:", bad.is_error, bad.structured_content)


for m in ("auto", "legacy"):  # legacy = initialize handshake, what older harnesses send
    print(f"--- mode={m}")
    try:
        asyncio.run(main(m))
    except Exception as exc:  # report, don't crash: the failure IS the finding
        print("FAILED:", type(exc).__name__, str(exc)[:300])
