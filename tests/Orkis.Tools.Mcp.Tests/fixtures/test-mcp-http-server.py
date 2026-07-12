#!/usr/bin/env python3
"""A deliberately tiny MCP Streamable HTTP server for adapter tests.

Plain http.server, no SDK: every JSON-RPC message arrives as a POST and gets a
plain application/json response (the spec allows JSON instead of SSE), so the
tests exercise a real remote-transport conversation. Usage: pass the port as argv[1].
"""

import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOOLS = [
    {
        "name": "multiply",
        "description": "Multiplies two integers.",
        "inputSchema": {
            "type": "object",
            "properties": {"a": {"type": "integer"}, "b": {"type": "integer"}},
            "required": ["a", "b"],
        },
        "annotations": {"readOnlyHint": True},
    }
]


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def _json(self, status, payload, headers=None):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(body)

    def _empty(self, status):
        self.send_response(status)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_GET(self):  # noqa: N802 — no server-initiated stream offered.
        self._empty(405)

    def do_DELETE(self):  # noqa: N802 — session teardown.
        self._empty(200)

    def _read_body(self):
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            chunks = []
            while True:
                size = int(self.rfile.readline().strip(), 16)
                if size == 0:
                    self.rfile.readline()
                    return b"".join(chunks)
                chunks.append(self.rfile.read(size))
                self.rfile.readline()
        return self.rfile.read(int(self.headers.get("Content-Length", "0")))

    def do_POST(self):  # noqa: N802
        message = json.loads(self._read_body())
        method = message.get("method")
        msg_id = message.get("id")

        if msg_id is None:  # Notification.
            self._empty(202)
            return

        if method == "initialize":
            self._json(
                200,
                {
                    "jsonrpc": "2.0",
                    "id": msg_id,
                    "result": {
                        "protocolVersion": message["params"]["protocolVersion"],
                        "capabilities": {"tools": {}},
                        "serverInfo": {"name": "orkis-test-http-server", "version": "1.0.0"},
                    },
                },
                headers={"Mcp-Session-Id": "orkis-test-session"},
            )
        elif method == "tools/list":
            self._json(200, {"jsonrpc": "2.0", "id": msg_id, "result": {"tools": TOOLS}})
        elif method == "tools/call":
            args = message["params"].get("arguments") or {}
            self._json(
                200,
                {
                    "jsonrpc": "2.0",
                    "id": msg_id,
                    "result": {
                        "content": [{"type": "text", "text": str(int(args["a"]) * int(args["b"]))}],
                        "isError": False,
                    },
                },
            )
        else:
            self._json(
                200,
                {"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32601, "message": "method not found"}},
            )


ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
