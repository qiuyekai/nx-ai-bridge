"""MCP server (stdio).  Exposes every tool from tools.py."""

from __future__ import annotations

import base64
import json
import os
from typing import Any

from . import __version__
from .client import BridgeClient
from .tools import TOOLS

INSTRUCTIONS = """\
nx-ai-bridge drives Siemens NX (UG) modelling. ALPHA software.

Workflow:
1. Call nx_status first. If it fails with RECEIVER_NOT_RUNNING, relay the `hint` to the user: they must start
   the receiver journal inside NX once (Alt+F8). Do not retry in a loop.
2. new_part or open_part, then build the model with primitives / sketches / features / booleans.
3. After each important step call screenshot (to see the result) or get_model_info (bounding box, volume) to verify.
4. save_part when done. Export with export_step / export_stl.

Rules:
- All lengths are millimetres, all angles degrees, points are [x, y, z] in absolute coordinates.
- Refer to bodies by the `body` name returned by the creating tool (e.g. "B1") or by feature name (e.g. "BLOCK(1)").
- On failure read error.message / error.nx_error / error.hint, fix the parameters and try again. Use undo to go back.
- When no tool fits, use nx_api_lookup to explore the NX Open API and run_journal to execute custom NX Open code.
"""


def build_server(client: BridgeClient | None = None):
    """Create the MCP server object (imports the MCP SDK lazily).

    Works with both generations of the Python MCP SDK: 1.x registers handlers
    with decorators, 2.x takes them as constructor arguments.
    """
    import anyio
    import mcp.types as types
    from mcp.server import Server

    client = client or BridgeClient()

    def tool_list() -> list[Any]:
        return [
            types.Tool.model_validate({"name": t.name, "description": t.description, "inputSchema": t.input_schema})
            for t in TOOLS
        ]

    async def run_tool(name: str, arguments: dict[str, Any] | None):
        envelope = await anyio.to_thread.run_sync(client.call, name, arguments or {})
        content: list[Any] = [{"type": "text", "text": json.dumps(envelope, ensure_ascii=False, indent=1)}]
        if name == "screenshot" and envelope.get("ok"):
            path = (envelope.get("result") or {}).get("path")
            if path and os.path.isfile(path) and os.path.getsize(path) < 8_000_000:
                with open(path, "rb") as fh:
                    data = base64.b64encode(fh.read()).decode("ascii")
                content.append({"type": "image", "data": data, "mimeType": "image/png"})
        return types.CallToolResult.model_validate({"content": content, "isError": not envelope.get("ok", False)})

    if hasattr(Server, "call_tool"):  # MCP SDK 1.x
        try:
            server = Server("nx-ai-bridge", version=__version__, instructions=INSTRUCTIONS)
        except TypeError:
            server = Server("nx-ai-bridge")

        @server.list_tools()
        async def _list_tools():
            return tool_list()

        @server.call_tool()
        async def _call_tool(name: str, arguments: dict[str, Any] | None):
            return await run_tool(name, arguments)

        return server

    async def on_list_tools(ctx, params):  # MCP SDK 2.x
        return types.ListToolsResult(tools=tool_list())

    async def on_call_tool(ctx, params):
        return await run_tool(params.name, params.arguments)

    return Server(
        "nx-ai-bridge",
        version=__version__,
        instructions=INSTRUCTIONS,
        on_list_tools=on_list_tools,
        on_call_tool=on_call_tool,
    )


def run_stdio() -> None:
    import anyio
    from mcp.server.stdio import stdio_server

    server = build_server()

    async def _main() -> None:
        async with stdio_server() as (read, write):
            await server.run(read, write, server.create_initialization_options())

    anyio.run(_main)
