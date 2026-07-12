#!/usr/bin/env python3
"""A deliberately tiny MCP stdio server for adapter tests.

Speaks newline-delimited JSON-RPC 2.0 with no SDK, so the tests exercise a real
protocol conversation: initialize, tools/list (two tools with annotations), and
tools/call (one working, one always-erroring).
"""

import json
import sys

TOOLS = [
    {
        "name": "add",
        "description": "Adds two integers.",
        "inputSchema": {
            "type": "object",
            "properties": {"a": {"type": "integer"}, "b": {"type": "integer"}},
            "required": ["a", "b"],
        },
        "annotations": {"readOnlyHint": True},
    },
    {
        "name": "explode",
        "description": "Always fails.",
        "inputSchema": {"type": "object"},
        "annotations": {"destructiveHint": True},
    },
]


def send(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def result(msg_id, payload):
    send({"jsonrpc": "2.0", "id": msg_id, "result": payload})


for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    message = json.loads(line)
    method = message.get("method")
    msg_id = message.get("id")

    if method == "initialize":
        result(
            msg_id,
            {
                "protocolVersion": message["params"]["protocolVersion"],
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "orkis-test-server", "version": "1.0.0"},
            },
        )
    elif method == "tools/list":
        result(msg_id, {"tools": TOOLS})
    elif method == "tools/call":
        name = message["params"]["name"]
        args = message["params"].get("arguments") or {}
        if name == "add":
            result(
                msg_id,
                {
                    "content": [{"type": "text", "text": str(int(args["a"]) + int(args["b"]))}],
                    "isError": False,
                },
            )
        else:
            result(
                msg_id,
                {"content": [{"type": "text", "text": "kaboom"}], "isError": True},
            )
    elif msg_id is not None:
        send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32601, "message": "method not found"}})
