"""Command line interface - the same tools as the MCP server, for AIs that can only run commands.

Examples::

    nx-ai-bridge serve                       # MCP server on stdio (default when no command is given)
    nx-ai-bridge tools                       # list tools
    nx-ai-bridge tools create_block          # show one tool with its parameters
    nx-ai-bridge call nx_status
    nx-ai-bridge call create_block length=100 width=50 height=20
    nx-ai-bridge call create_block '{"length": 100, "width": 50, "height": 20, "origin": [0, 0, 10]}'
    nx-ai-bridge doctor                      # show configuration and connection state
    nx-ai-bridge mock                        # run a fake NX receiver (for trying things without NX)
    nx-ai-bridge demo --part C:\\temp\\bracket.prt
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path

from . import __version__
from .config import load_config, packaged_receiver_dir
from .tools import GROUPS, TOOLS, TOOLS_BY_NAME


def _print_json(data) -> None:
    sys.stdout.write(json.dumps(data, ensure_ascii=False, indent=2) + "\n")


def _parse_value(text: str):
    try:
        return json.loads(text)
    except ValueError:
        return text


def _parse_call_args(items: list[str]) -> dict:
    params: dict = {}
    for item in items:
        stripped = item.strip()
        if stripped.startswith("{"):
            params.update(json.loads(stripped))
        elif stripped.startswith("@"):
            params.update(json.loads(Path(stripped[1:]).read_text(encoding="utf-8-sig")))
        elif "=" in item:
            key, _, value = item.partition("=")
            params[key.strip().lstrip("-").replace("-", "_")] = _parse_value(value)
        else:
            raise SystemExit(f"Cannot parse argument {item!r}: use key=value, a JSON object, or @file.json")
    return params


def cmd_serve(args) -> int:
    try:
        from .server import run_stdio
    except ImportError as exc:  # pragma: no cover
        sys.stderr.write(f"The MCP SDK is not installed ({exc}). Install with: pip install mcp\n")
        return 2
    run_stdio()
    return 0


def cmd_tools(args) -> int:
    if args.name:
        tool = TOOLS_BY_NAME.get(args.name)
        if tool is None:
            sys.stderr.write(f"Unknown tool {args.name!r}\n")
            return 2
        _print_json({"name": tool.name, "group": tool.group, "description": tool.description, "input_schema": tool.input_schema})
        return 0
    if args.json:
        _print_json([{"name": t.name, "group": t.group, "description": t.description, "input_schema": t.input_schema} for t in TOOLS])
        return 0
    for group in GROUPS:
        print(f"[{group}]")
        for t in TOOLS:
            if t.group == group:
                req = ", ".join(t.required)
                opt = ", ".join(k for k in t.properties if k not in t.required)
                sig = req + (f" [{opt}]" if opt else "")
                print(f"  {t.name:<18} {sig}")
    print("\nAll lengths in mm, angles in degrees. Details: nx-ai-bridge tools <name>")
    return 0


def cmd_call(args) -> int:
    from .client import BridgeClient

    try:
        params = _parse_call_args(args.params)
    except ValueError as exc:
        _print_json({"ok": False, "error": {"code": "INVALID_PARAMS", "message": f"Bad JSON on the command line: {exc}"}})
        return 2
    resp = BridgeClient().call(args.tool, params, timeout=args.timeout)
    _print_json(resp)
    return 0 if resp.get("ok") else 1


def cmd_status(args) -> int:
    args.tool, args.params, args.timeout = "nx_status", [], None
    return cmd_call(args)


def cmd_doctor(args) -> int:
    from .transport import read_receiver_state, receiver_is_fresh, select_transport

    cfg = load_config()
    state = read_receiver_state(cfg)
    info = {
        "version": __version__,
        "python": sys.version.split()[0],
        "platform": sys.platform,
        "home": str(cfg.home),
        "transport_setting": cfg.transport,
        "transport_selected": select_transport(cfg).name,
        "port": cfg.port,
        "nx_base_dir": cfg.nx_base_dir,
        "run_journal_exe": cfg.run_journal_exe,
        "run_journal_exists": bool(cfg.run_journal_exe and os.path.isfile(cfg.run_journal_exe)),
        "receiver_installed": (cfg.receiver_dir / "NxAiBridgeReceiver.cs").is_file(),
        "receiver_journal": str(cfg.receiver_dir / "NxAiBridgeReceiver.cs"),
        "receiver_running": receiver_is_fresh(state),
        "receiver_state": state,
    }
    try:
        import mcp  # noqa: F401

        info["mcp_sdk"] = True
    except ImportError:
        info["mcp_sdk"] = False
    _print_json(info)
    return 0


def cmd_install_receiver(args) -> int:
    from .transport import BridgeError, install_receiver

    cfg = load_config()
    try:
        dst = install_receiver(cfg)
    except BridgeError as exc:
        _print_json({"ok": False, "error": exc.to_error()})
        return 1
    _print_json({"ok": True, "result": {"receiver_dir": str(dst), "journal": str(dst / "NxAiBridgeReceiver.cs"),
                                         "selfcheck_journal": str(dst / "NxAiBridgeSelfCheck.cs")}})
    return 0


def cmd_mock(args) -> int:
    from .mock import MockServer

    cfg = load_config()
    cfg.home.mkdir(parents=True, exist_ok=True)
    server = MockServer(cfg.home, port=cfg.port if args.port is None else args.port, tcp=not args.no_tcp).start()
    sys.stderr.write(
        f"Mock NX receiver running (home={cfg.home}, tcp_port={server.port or 'off'}, file transport on). Ctrl+C to stop.\n"
    )
    try:
        while True:
            time.sleep(0.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.stop()
    return 0


def cmd_demo(args) -> int:
    from .client import BridgeClient
    from .demo import run_bracket

    report = run_bracket(BridgeClient(), args.part)
    _print_json(report)
    return 0 if report["ok"] else 1


def mcp_config_snippets(command: list[str] | None = None) -> dict:
    """Configuration snippets for common AI tools."""
    command = command or ["uvx", "--from", "git+https://github.com/qiuyekai/nx-ai-bridge", "nx-ai-bridge", "serve"]
    exe, rest = command[0], command[1:]
    entry = {"command": exe, "args": rest}
    return {
        "claude_code": "claude mcp add nx -- " + " ".join(command),
        "claude_desktop / cursor / windsurf (mcpServers JSON)": {"mcpServers": {"nx": entry}},
        "vscode (.vscode/mcp.json)": {"servers": {"nx": {"type": "stdio", **entry}}},
        "codex (~/.codex/config.toml)": "[mcp_servers.nx]\ncommand = " + json.dumps(exe) + "\nargs = " + json.dumps(rest),
        "gemini_cli (~/.gemini/settings.json)": {"mcpServers": {"nx": entry}},
        "cli_only": "nx-ai-bridge tools   |   nx-ai-bridge call <tool> key=value ...",
    }


def cmd_mcp_config(args) -> int:
    command = args.command.split() if args.command else None
    _print_json(mcp_config_snippets(command))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="nx-ai-bridge", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=f"nx-ai-bridge {__version__} (alpha)")
    sub = parser.add_subparsers(dest="cmd")

    sub.add_parser("serve", help="run the MCP server on stdio").set_defaults(func=cmd_serve)

    p = sub.add_parser("tools", help="list tools or describe one")
    p.add_argument("name", nargs="?")
    p.add_argument("--json", action="store_true", help="machine readable list with JSON schemas")
    p.set_defaults(func=cmd_tools)

    p = sub.add_parser("call", help="call a tool; prints the JSON envelope; exit code 1 on failure")
    p.add_argument("tool")
    p.add_argument("params", nargs="*", help="key=value pairs, a JSON object, or @file.json")
    p.add_argument("--timeout", type=float, default=None)
    p.set_defaults(func=cmd_call)

    sub.add_parser("status", help="shortcut for: call nx_status").set_defaults(func=cmd_status)
    sub.add_parser("doctor", help="show configuration, NX detection and receiver state").set_defaults(func=cmd_doctor)
    sub.add_parser("install-receiver", help="copy the NX journals to <home>/nx_receiver").set_defaults(func=cmd_install_receiver)

    p = sub.add_parser("mock", help="run a mock NX receiver (TCP + file transport)")
    p.add_argument("--port", type=int, default=None, help="TCP port (0 = pick a free one)")
    p.add_argument("--no-tcp", action="store_true", help="file transport only")
    p.set_defaults(func=cmd_mock)

    p = sub.add_parser("demo", help="build the reference bracket and check volumes")
    p.add_argument("--part", required=True, help="absolute path of the .prt file to create")
    p.set_defaults(func=cmd_demo)

    p = sub.add_parser("mcp-config", help="print MCP configuration snippets for AI tools")
    p.add_argument("--command", help="command line that starts the server (default: uvx from GitHub)")
    p.set_defaults(func=cmd_mcp_config)
    return parser


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):  # Windows consoles default to a legacy code page
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
    parser = build_parser()
    args = parser.parse_args(argv)
    if not getattr(args, "func", None):
        return cmd_serve(args)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
