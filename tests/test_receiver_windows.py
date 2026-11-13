"""End-to-end tests of the real C# receiver kernel (not the mock).

They need the receiver compiled against the NX Open stub, which only happens in the Windows CI
job (see .github/workflows/ci.yml); it sets NX_AI_BRIDGE_TEST_RECEIVER to the loader .exe.
Everywhere else these tests are skipped.
"""

import json
import os
import subprocess
import time
from pathlib import Path

import pytest

from nx_ai_bridge.client import BridgeClient
from nx_ai_bridge.config import Config
from nx_ai_bridge.transport import (
    FileTransport,
    TcpTransport,
    install_receiver,
    make_request,
    read_receiver_state,
    receiver_is_fresh,
)

EXE = os.environ.get("NX_AI_BRIDGE_TEST_RECEIVER")
SELFCHECK_EXE = os.environ.get("NX_AI_BRIDGE_TEST_SELFCHECK")
FAKE = Path(__file__).resolve().parent / "fake_handlers"
pytestmark = [pytest.mark.skipif(not EXE, reason="needs the compiled receiver (Windows CI only)"), pytest.mark.timeout(180)]


def start(home, *extra):
    install_receiver(Config(home=home))
    env = dict(os.environ, NX_AI_BRIDGE_HOME=str(home))
    proc = subprocess.Popen([EXE, "--console", "--port", "0", "--timeout", "150", *extra], env=env,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    deadline = time.time() + 60
    while time.time() < deadline:
        if receiver_is_fresh(read_receiver_state(Config(home=home))):
            return proc
        if proc.poll() is not None:
            break
        time.sleep(0.1)
    out = proc.stdout.read() if proc.poll() is not None else ""
    log = home / "logs" / "loader-error.txt"
    raise AssertionError(f"receiver did not start: {out}\n{log.read_text() if log.exists() else ''}")


def stop(home, proc):
    try:
        TcpTransport(Config(home=home), port=read_receiver_state(Config(home=home))["tcp_port"]).send(make_request("__shutdown", {}), 10)
    except Exception:
        (home / "stop").write_text("stop")
    try:
        proc.wait(timeout=20)
    except subprocess.TimeoutExpired:
        proc.kill()


@pytest.fixture
def fake(home):
    proc = start(home, "--handlers", str(FAKE))
    yield Config(home=home)
    stop(home, proc)


@pytest.fixture
def real(home):
    proc = start(home)
    yield Config(home=home)
    stop(home, proc)


def send(cfg, tool, params=None, transport="tcp"):
    state = read_receiver_state(cfg)
    t = TcpTransport(cfg, port=state["tcp_port"]) if transport == "tcp" else FileTransport(cfg)
    return t.send(make_request(tool, params or {}), 60)


@pytest.mark.parametrize("transport", ["tcp", "file"])
def test_roundtrip_and_json_fidelity(fake, transport):
    params = {"number": 2.5, "text": "支架 \"quoted\" \\ back\nline", "nested": {"list": [1, 2.5, True, None, "x"]}, "big": 123456789012}
    resp = send(fake, "echo", params, transport)
    assert resp["ok"], resp
    assert resp["result"]["params"] == params
    assert resp["result"]["number"] == 2.5 and resp["result"]["list"] == [1, 2.25, 3]
    assert resp["result"]["mode"] == "console" and resp["meta"]["variant"] == "echo.cs"


def test_heartbeat_state(fake):
    state = read_receiver_state(fake)
    assert state["tcp_port"] > 0 and state["protocol"] == 1 and state["mode"] == "console"
    assert BridgeClient(fake).call("nx_status")["ok"]  # "auto" finds the port through the heartbeat


def test_error_mapping(fake):
    user = send(fake, "user_error")
    assert user["error"]["code"] == "INVALID_PARAMS" and user["error"]["hint"] == "use a positive number"
    nx = send(fake, "nx_error")
    assert nx["error"]["code"] == "NX_ERROR"
    assert nx["error"]["nx_error"] == {"type": "NXOpen.NXException", "message": "Tool body completely outside target body.", "error_code": 670030}
    crash = send(fake, "crash")
    assert crash["error"]["code"] == "INTERNAL_ERROR" and "boom" in crash["error"]["message"]
    api = send(fake, "api_mismatch")
    assert api["error"]["code"] == "API_MISMATCH" and "NoSuchMethod" in api["error"]["message"]
    unknown = send(fake, "does_not_exist")
    assert unknown["error"]["code"] == "UNKNOWN_TOOL"


def test_variant_fallback(fake):
    runtime = send(fake, "fallback_demo")
    assert runtime["ok"] and runtime["result"]["served_by"] == "variant 2"
    assert runtime["meta"]["variant"] == "fallback2.cs"
    assert runtime["meta"]["attempts"][0]["code"] == "API_MISMATCH" and runtime["meta"]["attempts"][0]["variant"] == "fallback1.cs"

    compile_time = send(fake, "compile_demo")
    assert compile_time["ok"] and compile_time["meta"]["attempts"][0]["code"] == "COMPILE_ERROR"
    assert "ThisMethodDoesNotExist" in json.dumps(compile_time["meta"]["attempts"][0]["details"])

    # a user error must NOT fall through to the next variant
    user = send(fake, "fallback_demo", {"user_error": True})
    assert not user["ok"] and user["error"]["code"] == "NOT_FOUND"

    broken = send(fake, "always_broken")
    assert broken["error"]["code"] == "COMPILE_ERROR" and broken["error"]["details"]["errors"]


def test_late_binding_helper(fake):
    """Dyn is what makes blind-written NX code diagnosable: check its conversions on the real CLR."""
    resp = send(fake, "dyn_demo")
    assert resp["ok"], resp
    r = resp["result"]
    assert r["out_int"] == 4 and r["filled"] == 7.5 and r["ref_string"] == "n!" and r["out_array_last"] == 9
    assert r["size"] == 4 and r["mode"] == "Fancy" and r["text"] == "hello" and r["set_any"] == "Size"
    assert r["pick"] == "kind:Fancy" and r["sum_list"] == 6
    assert r["over_string"] == "string" and r["over_enum"] == "enum" and r["over_int"] == "double"  # exact matches win
    assert r["nulls"] == "nulls" and r["enum_lookup"] == "Plain" and r["try_set_missing"] is False
    assert r["failure"] == "InvalidOperationException:inner failure"
    assert "Size" in r["missing_candidates"]  # the typo "Sise" is answered with the real member


def test_bad_request_is_answered(fake):
    import socket

    port = read_receiver_state(fake)["tcp_port"]
    with socket.create_connection(("127.0.0.1", port), timeout=10) as s:
        s.sendall(b"this is not json\n")
        s.settimeout(30)
        data = s.recv(65536)
    resp = json.loads(data)
    assert resp["ok"] is False and resp["error"]["code"] == "BAD_REQUEST"


def test_real_handlers_against_stub(real):
    """The production handlers, with the NX Open stub standing in for NX."""
    client = BridgeClient(real)
    status = client.call("nx_status")
    assert status["ok"] and status["result"]["work_part"] is None and status["result"]["mode"] == "console"
    no_part = client.call("create_block", {"length": 1, "width": 2, "height": 3})
    assert no_part["error"]["code"] == "NO_WORK_PART" and "new_part" in no_part["error"]["hint"]

    vb = client.call("run_journal", {"code": "Imports System\nModule M\n Function Main(ByVal args() As String) As String\n  Console.WriteLine(\"hi \" & args(0))\n  Return \"vb ok\"\n End Function\nEnd Module\n", "args": ["there"]})
    assert vb["ok"], vb
    assert vb["result"]["return"] == "vb ok" and vb["result"]["stdout"].strip() == "hi there"
    cs = client.call("run_journal", {"language": "csharp", "code": "using NXOpen;\npublic class J { public static string Main() { return Session.GetSession().Parts.Work == null ? \"no part\" : \"part\"; } }"})
    assert cs["ok"] and cs["result"]["return"] == "no part"
    bad = client.call("run_journal", {"language": "csharp", "code": "public class J { public static void Main() { int x = ; } }"})
    assert bad["error"]["code"] == "COMPILE_ERROR" and bad["error"]["details"]["errors"]

    api = client.call("nx_api_lookup", {"type": "NXOpen.Session", "query": "undo"})
    assert api["ok"] and any("SetUndoMark" in m for m in api["result"]["members"])


def test_compile_check_all_handlers(home):
    """Every production source file compiles with the .NET Framework C# 5 compiler."""
    install_receiver(Config(home=home))
    env = dict(os.environ, NX_AI_BRIDGE_HOME=str(home))
    proc = subprocess.run([EXE, "--compile-check"], env=env, capture_output=True, text=True, timeout=170)
    report = json.loads((home / "logs" / "compile-check.json").read_text(encoding="utf-8"))
    broken = {k: v for k, v in report["units"].items() if v != "ok"}
    assert not broken, json.dumps(broken, indent=1)
    assert proc.returncode == 0 and len(report["units"]) >= 20


def test_batch_mode(home, tmp_path):
    install_receiver(Config(home=home))
    job = tmp_path / "job"
    job.mkdir()
    reqs = [make_request("nx_status", {}), make_request("create_block", {"length": 1, "width": 1, "height": 1}), make_request("nx_status", {})]
    (job / "requests.json").write_text(json.dumps({"stop_on_error": True, "requests": reqs}), encoding="utf-8")
    env = dict(os.environ, NX_AI_BRIDGE_HOME=str(home))
    subprocess.run([EXE, "--batch", str(job)], env=env, timeout=170, check=True)
    responses = json.loads((job / "responses.json").read_text(encoding="utf-8"))
    assert [r["ok"] for r in responses] == [True, False]  # stopped at the first error
    assert responses[0]["id"] == reqs[0]["id"] and responses[0]["result"]["mode"] == "batch"


@pytest.mark.skipif(not SELFCHECK_EXE, reason="needs the compiled self-check loader")
def test_selfcheck_writes_report(home):
    install_receiver(Config(home=home))
    env = dict(os.environ, NX_AI_BRIDGE_HOME=str(home))
    subprocess.run([SELFCHECK_EXE, "--no-ui"], env=env, timeout=170, check=True)
    reports = list((home / "reports").glob("nx-ai-bridge-selfcheck-*.json"))
    assert len(reports) == 1
    report = json.loads(reports[0].read_text(encoding="utf-8"))
    assert report["transport"]["sockets"]["ok"] and report["transport"]["tcp_roundtrip"]["ok"] and report["transport"]["file_roundtrip"]["ok"]
    assert all(v == "ok" for v in report["compile"].values()), report["compile"]
    assert report["steps"][0]["tool"] == "nx_status" and report["steps"][0]["ok"]
    assert report["steps"][1]["tool"] == "new_part" and not report["steps"][1]["ok"]  # the stub cannot create parts
    assert "NXOpen.Session" in report["api_dump"] and "finished" in report
