import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

SRC = str(Path(__file__).resolve().parents[1] / "src")


def run_cli(home, *args, transport="mock"):
    env = dict(os.environ, NX_AI_BRIDGE_HOME=str(home), NX_AI_BRIDGE_TRANSPORT=transport, PYTHONPATH=SRC)
    return subprocess.run([sys.executable, "-m", "nx_ai_bridge", *args], capture_output=True, text=True, env=env, encoding="utf-8")


def test_cli_tools_listing(home):
    out = run_cli(home, "tools")
    assert out.returncode == 0 and "create_block" in out.stdout and "[sketch]" in out.stdout
    one = json.loads(run_cli(home, "tools", "create_block").stdout)
    assert one["input_schema"]["required"] == ["length", "width", "height"]
    assert len(json.loads(run_cli(home, "tools", "--json").stdout)) > 30


def test_cli_call_success_and_failure_exit_codes(home):
    good = run_cli(home, "call", "nx_status")
    assert good.returncode == 0 and json.loads(good.stdout)["result"]["mode"] == "mock"
    bad = run_cli(home, "call", "create_block", "length=10", "width=5")
    assert bad.returncode == 1 and json.loads(bad.stdout)["error"]["code"] == "INVALID_PARAMS"
    no_part = run_cli(home, "call", "create_block", '{"length": 10, "width": 5, "height": 2}', "origin=[1,2,3]")
    assert no_part.returncode == 1 and json.loads(no_part.stdout)["error"]["code"] == "NO_WORK_PART"


def test_cli_without_receiver_explains_what_to_do(home):
    out = run_cli(home, "status", transport="auto")
    assert out.returncode == 1
    assert "Alt+F8" in json.loads(out.stdout)["error"]["hint"]


def test_cli_doctor_and_install(home):
    assert json.loads(run_cli(home, "install-receiver").stdout)["ok"]
    info = json.loads(run_cli(home, "doctor", transport="auto").stdout)
    assert info["receiver_installed"] and not info["receiver_running"] and info["home"] == str(home)
    snippets = json.loads(run_cli(home, "mcp-config").stdout)
    assert "claude mcp add" in snippets["claude_code"]


class McpProcess:
    """Minimal MCP client speaking newline-delimited JSON-RPC over stdio (no SDK needed)."""

    def __init__(self, home):
        env = dict(os.environ, NX_AI_BRIDGE_HOME=str(home), NX_AI_BRIDGE_TRANSPORT="mock", PYTHONPATH=SRC)
        self.proc = subprocess.Popen(
            [sys.executable, "-m", "nx_ai_bridge", "serve"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, env=env, encoding="utf-8",
        )
        self.next_id = 0

    def notify(self, method, params=None):
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method, "params": params or {}}) + "\n")
        self.proc.stdin.flush()

    def request(self, method, params=None):
        self.next_id += 1
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params or {}}) + "\n")
        self.proc.stdin.flush()
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("server exited: " + self.proc.stderr.read())
            msg = json.loads(line)
            if msg.get("id") == self.next_id:
                assert "error" not in msg, msg
                return msg["result"]

    def close(self):
        self.proc.stdin.close()
        try:
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.kill()


@pytest.fixture
def mcp(home):
    pytest.importorskip("mcp")
    proc = McpProcess(home)
    init = proc.request("initialize", {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "test", "version": "0"}})
    assert init["serverInfo"]["name"] == "nx-ai-bridge"
    proc.notify("notifications/initialized")
    yield proc
    proc.close()


def test_mcp_end_to_end(mcp, tmp_path):
    tools = mcp.request("tools/list")["tools"]
    names = {t["name"] for t in tools}
    assert {"nx_status", "create_block", "screenshot", "run_journal", "nx_batch"} <= names
    block = next(t for t in tools if t["name"] == "create_block")
    assert block["inputSchema"]["properties"]["length"]["type"] == "number" and "mm" in block["description"]

    def call(name, **arguments):
        return mcp.request("tools/call", {"name": name, "arguments": arguments})

    res = call("new_part", path=str(tmp_path / "a.prt"))
    assert not res.get("isError")
    res = call("create_block", length=10, width=10, height=10)
    assert json.loads(res["content"][0]["text"])["result"]["volume"] == 1000

    shot = call("screenshot")
    kinds = [c["type"] for c in shot["content"]]
    assert kinds == ["text", "image"] and shot["content"][1]["mimeType"] == "image/png"

    bad = call("create_block", length=-1, width=10, height=10)
    assert bad["isError"] is True
    assert json.loads(bad["content"][0]["text"])["error"]["code"] == "INVALID_PARAMS"
