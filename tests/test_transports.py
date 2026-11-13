import json
import time

import pytest
from conftest import ok

from nx_ai_bridge.client import BridgeClient
from nx_ai_bridge.config import Config, load_config
from nx_ai_bridge.mock import MockReceiver, MockServer
from nx_ai_bridge.transport import (
    BatchTransport,
    BridgeError,
    FileTransport,
    TcpTransport,
    install_receiver,
    make_request,
    select_transport,
)


@pytest.fixture
def server(home):
    srv = MockServer(home, port=0).start()
    yield srv
    srv.stop()


def test_tcp_roundtrip(home, server, part):
    client = BridgeClient(Config(home=home, transport="tcp", port=server.port))
    assert ok(client.call("nx_status"))["mode"] == "mock"
    ok(client.call("new_part", {"path": part}))
    res = client.call("create_block", {"length": 3, "width": 4, "height": 5})
    assert ok(res)["volume"] == 60 and res["meta"]["transport"] == "tcp"
    # non-ASCII survives the wire
    resp = client.call("open_part", {"path": "C:/零件/支架.prt"})
    assert "支架" in resp["error"]["message"]


def test_file_roundtrip(home, server, part):
    client = BridgeClient(Config(home=home, transport="file"))
    ok(client.call("new_part", {"path": part}))
    res = client.call("create_sphere", {"diameter": 2})
    assert res["ok"] and res["meta"]["transport"] == "file"
    assert not list((home / "ipc" / "requests").glob("*")) and not list((home / "ipc" / "responses").glob("*"))


def test_auto_prefers_tcp_port_from_heartbeat(home, server):
    cfg = load_config(home)
    transport = select_transport(cfg)
    assert isinstance(transport, TcpTransport) and transport.port == server.port
    assert ok(BridgeClient(cfg).call("nx_status"))["connected"]


def test_auto_falls_back_to_file_when_receiver_has_no_socket(home):
    srv = MockServer(home, tcp=False).start()
    try:
        cfg = load_config(home)
        assert isinstance(select_transport(cfg), FileTransport)
        assert ok(BridgeClient(cfg).call("nx_status"))["connected"]
    finally:
        srv.stop()


def test_receiver_not_running_gives_actionable_hint(home):
    for mode in ("auto", "tcp", "file"):
        resp = BridgeClient(Config(home=home, transport=mode, port=1)).call("nx_status")
        assert not resp["ok"]
        assert resp["error"]["code"] == "RECEIVER_NOT_RUNNING", mode
        assert "Alt+F8" in resp["error"]["hint"] and "NxAiBridgeReceiver.cs" in resp["error"]["hint"]


def test_stale_heartbeat_is_ignored(home):
    (home / "receiver.json").write_text(json.dumps({"heartbeat": time.time() - 3600, "tcp_port": 0}))
    resp = BridgeClient(Config(home=home, transport="file")).call("nx_status", timeout=1)
    assert resp["error"]["code"] == "RECEIVER_NOT_RUNNING"


def test_file_timeout(home):
    (home / "receiver.json").write_text(json.dumps({"heartbeat": time.time(), "tcp_port": 0}))
    resp = BridgeClient(Config(home=home, transport="file")).call("nx_status", timeout=0.3)
    assert resp["error"]["code"] == "TIMEOUT"
    assert not list((home / "ipc" / "requests").glob("*.json"))


def test_install_receiver(home):
    dst = install_receiver(Config(home=home))
    assert (dst / "NxAiBridgeReceiver.cs").is_file() and (dst / "handlers").is_dir() and (dst / "kernel").is_dir()


class FakeNx:
    """Stands in for run_journal.exe: every job is a fresh NX session."""

    def __init__(self):
        self.jobs = []

    def __call__(self, job_dir, timeout):
        job = json.loads((job_dir / "requests.json").read_text(encoding="utf-8"))
        self.jobs.append([r["tool"] for r in job["requests"]])
        receiver = MockReceiver()
        out = []
        for req in job["requests"]:
            resp = receiver.handle(req)
            out.append(resp)
            if job["stop_on_error"] and not resp["ok"]:
                break
        (job_dir / "responses.json").write_text(json.dumps(out), encoding="utf-8")


def test_batch_mode_keeps_work_part_between_sessions(home, part):
    nx = FakeNx()
    cfg = Config(home=home, transport="batch")
    client = BridgeClient(cfg, transport=BatchTransport(cfg, runner=nx))
    ok(client.call("new_part", {"path": part}))
    assert ok(client.call("create_block", {"length": 2, "width": 2, "height": 2}))["volume"] == 8
    assert ok(client.call("get_model_info"))["total_volume"] == 8
    assert nx.jobs == [
        ["new_part", "save_part"],
        ["open_part", "create_block", "save_part"],
        ["open_part", "get_model_info"],
    ]
    batch = client.call("nx_batch", {"commands": [
        {"tool": "create_cylinder", "params": {"diameter": 1, "height": 1, "origin": [10, 0, 0]}},
        {"tool": "list_features"},
    ]})
    assert batch["ok"] and batch["result"]["results"][1]["result"]["count"] == 2
    assert nx.jobs[-1] == ["open_part", "create_cylinder", "list_features", "save_part"]
    ok(client.call("close_part"))
    assert client.call("create_block", {"length": 1, "width": 1, "height": 1})["error"]["code"] == "NO_WORK_PART"


def test_batch_reports_missing_nx(home):
    cfg = Config(home=home, transport="batch")
    install_receiver(cfg)
    resp = BridgeClient(cfg).call("nx_status")
    assert resp["error"]["code"] == "NX_NOT_FOUND" and "UGII_BASE_DIR" in resp["error"]["hint"]


def test_batch_reports_silent_failure(home):
    cfg = Config(home=home, transport="batch")
    transport = BatchTransport(cfg, runner=lambda job, timeout: None)
    with pytest.raises(BridgeError) as exc:
        transport.send(make_request("nx_status", {}), 5)
    assert exc.value.code == "BATCH_FAILED"
