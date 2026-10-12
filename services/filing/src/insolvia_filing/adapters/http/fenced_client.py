"""The only HTTP the filing worker has: `http.client`, behind the host fence.

THE FENCE IS CHECKED BEFORE A CONNECTION OBJECT EXISTS. `send` asks
`HostFence.check` for the target first; a refused host raises
`HostNotAllowedError` and no socket is created, no DNS lookup is made, and
nothing about the request leaves the process (tests/unit/test_fence.py
patches `socket.socket` and `socket.create_connection` to fail the test if
either is reached). Redirects are NEVER followed here — a 3xx comes back to
the driver, which sends the next hop through `send` again, so every hop is
fenced.

One client per filing run: it holds that session's cookies and nothing
else, and is dropped with the run. It never logs a request — not the URL's
query, not a body, not a header.
"""

from __future__ import annotations

import http.client
import ssl
from collections.abc import Callable
from http.cookies import SimpleCookie

from ...core.fence import HostFence, Target
from ...core.ports import (
    HttpRequest,
    HttpResponse,
    HttpTimeoutError,
    HttpTransportError,
)

ConnectionFactory = Callable[[Target, float], http.client.HTTPConnection]


def _connect(target: Target, timeout: float) -> http.client.HTTPConnection:
    if target.scheme == "https":
        return http.client.HTTPSConnection(
            target.host,
            target.port,
            timeout=timeout,
            context=ssl.create_default_context(),
        )
    return http.client.HTTPConnection(target.host, target.port, timeout=timeout)


class FencedHttpClient:
    def __init__(
        self,
        fence: HostFence,
        *,
        timeout: float,
        connect: ConnectionFactory = _connect,
    ) -> None:
        self._fence = fence
        self._timeout = timeout
        self._connect = connect
        self._cookies: dict[str, str] = {}

    def send(self, request: HttpRequest) -> HttpResponse:
        # FIRST, before anything that could touch the network.
        target = self._fence.check(request.url)
        headers = dict(request.headers)
        if self._cookies:
            headers["Cookie"] = "; ".join(f"{k}={v}" for k, v in self._cookies.items())
        connection = self._connect(target, self._timeout)
        try:
            connection.request(
                request.method, target.path, body=request.body, headers=headers
            )
            response = connection.getresponse()
            body = response.read()
            response_headers = dict(response.getheaders())
            for key, value in response.getheaders():
                if key.lower() == "set-cookie":
                    cookie: SimpleCookie = SimpleCookie()
                    cookie.load(value)
                    for name, morsel in cookie.items():
                        self._cookies[name] = morsel.value
            return HttpResponse(
                status=response.status,
                headers=response_headers,
                body=body,
                url=request.url,
            )
        except TimeoutError as error:
            raise HttpTimeoutError("the court did not answer in time") from error
        except (OSError, http.client.HTTPException) as error:
            raise HttpTransportError(type(error).__name__) from error
        finally:
            connection.close()
