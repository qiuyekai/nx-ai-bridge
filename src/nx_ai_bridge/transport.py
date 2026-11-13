"""Transports between the bridge and the NX receiver.

Wire format (identical for every transport)::

    request  = {"id": "<uuid>", "tool": "<name>", "params": {...}, "protocol": 1}
    response = {"id": "<uuid>", "ok": true,  "result": {...}, "meta": {...}}
             | {"id": "<uuid>", "ok": false, "error": {"code", "message", ...}, "meta": {...}}

* TCP   : connect to 127.0.0.1:<port>, send one request as a single UTF-8 line
          terminated by "\\n", read one response line, close.
* File  : write ``<home>/ipc/requests/<id>.json`` (atomically: ``.tmp`` then
          rename); the receiver answers with ``<home>/ipc/responses/<id>.json``.
* Batch : write ``<job>/requests.json`` (a JSON array of requests), start
          ``run_journal.exe NxAiBridgeReceiver.cs -args --batch <job>`` and read
          ``<job>/responses.json`` (a JSON array of responses).
"""

from __future__ import annotations

import json
import os
import socket
import subprocess
import time
import uuid
from pathlib import Path
from typing import Any

from . import PROTOCOL_VERSION
from .config import Config, packaged_receiver_dir

START_RECEIVER_HINT = (
    "The NX receiver is not running. Ask the user to do this ONE manual step in NX: "
    "press Alt+F8 (menu: Tools > Journal > Play...), browse to "
    "{journal} and click Run. A small 'NX AI Bridge' window appears and stays open "
    "while the receiver is listening. Then call nx_status again. "
    "(Alternative without the NX window: set NX_AI_BRIDGE_TRANSPORT=batch.)"
)


class BridgeError(Exception):
    """An error produced by the bridge itself (not by NX)."""

    def __init__(self, code: str, message: str, hint: str | None = None, details: Any = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.hint = hint
        self.details = details

    def to_error(self) -> dict:
        err: dict[str, Any] = {"code": self.code, "message": self.message}
        if self.hint:
            err["hint"] = self.hint
        if self.details is not None:
            err["details"] = self.details
        return err


def make_request(tool: str, params: dict | None) -> dict:
    return {"id": uuid.uuid4().hex, "tool": tool, "params": params or {}, "protocol": PROTOCOL_VERSION}


def _receiver_hint(cfg: Config) -> str:
    journal = cfg.receiver_dir / "NxAiBridgeReceiver.cs"
    if not journal.is_file():
        # e.g. started through `uvx` without install.ps1: put the journals where the hint says they are
        try:
            install_receiver(cfg)
        except (BridgeError, OSError):
            pass
    return START_RECEIVER_HINT.format(journal=journal)


def read_receiver_state(cfg: Config) -> dict | None:
    """Heartbeat file of a live receiver, or None when missing/unreadable."""
    try:
        return json.loads(cfg.receiver_state_file.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None


def receiver_is_fresh(state: dict | None, max_age: float = 20.0) -> bool:
    if not state:
        return False
    try:
        return (time.time() - float(state.get("heartbeat", 0))) < max_age
    except (TypeError, ValueError):
        return False


class Transport:
    name = "?"

    def send(self, request: dict, timeout: float) -> dict:  # pragma: no cover - interface
        raise NotImplementedError

    def send_many(self, requests: list[dict], timeout: float, stop_on_error: bool = True) -> list[dict]:
        out = []
        for req in requests:
            resp = self.send(req, timeout)
            out.append(resp)
            if stop_on_error and not resp.get("ok"):
                break
        return out


class TcpTransport(Transport):
    name = "tcp"

    def __init__(self, cfg: Config, port: int | None = None):
        self.cfg = cfg
        self.port = port or cfg.port

    def send(self, request: dict, timeout: float) -> dict:
        try:
            sock = socket.create_connection((self.cfg.host, self.port), timeout=min(timeout, 5.0))
        except OSError as exc:
            raise BridgeError(
                "RECEIVER_NOT_RUNNING",
                f"Cannot connect to the NX receiver on {self.cfg.host}:{self.port} ({exc}).",
                hint=_receiver_hint(self.cfg),
            ) from exc
        with sock:
            sock.settimeout(timeout)
            try:
                sock.sendall((json.dumps(request, ensure_ascii=False) + "\n").encode("utf-8"))
                buf = bytearray()
                while not buf.endswith(b"\n"):
                    chunk = sock.recv(65536)
                    if not chunk:
                        break
                    buf.extend(chunk)
            except socket.timeout as exc:
                raise BridgeError(
                    "TIMEOUT",
                    f"NX did not answer within {timeout:.0f} s. The operation may still be running in NX.",
                    hint="Check the NX window for a dialog that is waiting for input, or raise NX_AI_BRIDGE_TIMEOUT.",
                ) from exc
            except OSError as exc:
                raise BridgeError("TRANSPORT", f"TCP connection to the receiver failed: {exc}") from exc
        if not buf.strip():
            raise BridgeError("TRANSPORT", "The receiver closed the connection without answering.")
        try:
            return json.loads(buf.decode("utf-8"))
        except ValueError as exc:
            raise BridgeError("TRANSPORT", f"Receiver sent invalid JSON: {buf[:200]!r}") from exc


class FileTransport(Transport):
    name = "file"

    def __init__(self, cfg: Config, require_live: bool = True):
        self.cfg = cfg
        self.require_live = require_live
        self.req_dir = cfg.ipc_dir / "requests"
        self.resp_dir = cfg.ipc_dir / "responses"

    def send(self, request: dict, timeout: float) -> dict:
        if self.require_live and not receiver_is_fresh(read_receiver_state(self.cfg)):
            raise BridgeError(
                "RECEIVER_NOT_RUNNING",
                f"No live NX receiver found (no fresh heartbeat in {self.cfg.receiver_state_file}).",
                hint=_receiver_hint(self.cfg),
            )
        self.req_dir.mkdir(parents=True, exist_ok=True)
        self.resp_dir.mkdir(parents=True, exist_ok=True)
        rid = request["id"]
        req_file = self.req_dir / f"{rid}.json"
        tmp = self.req_dir / f"{rid}.json.tmp"
        tmp.write_text(json.dumps(request, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp, req_file)
        resp_file = self.resp_dir / f"{rid}.json"
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if resp_file.exists():
                try:
                    text = resp_file.read_text(encoding="utf-8-sig")
                    data = json.loads(text)
                except (OSError, ValueError):
                    time.sleep(0.03)  # receiver is still writing
                    continue
                try:
                    resp_file.unlink()
                except OSError:
                    pass
                return data
            time.sleep(0.03)
        try:
            req_file.unlink()
        except OSError:
            pass
        raise BridgeError(
            "TIMEOUT",
            f"NX did not answer within {timeout:.0f} s (file transport).",
            hint=_receiver_hint(self.cfg),
        )


class BatchTransport(Transport):
    """Runs requests through ``run_journal.exe`` without the NX user interface.

    Every NX session is short-lived, so the bridge remembers the work part in
    ``<home>/batch_state.json`` and wraps single calls as
    ``open_part -> tool -> save_part``.
    """

    name = "batch"

    def __init__(self, cfg: Config, runner=None):
        self.cfg = cfg
        self._runner = runner or self._run_nx
        self.state_file = cfg.home / "batch_state.json"

    # ---- persistent work-part state
    def _work_part(self) -> str | None:
        try:
            return json.loads(self.state_file.read_text(encoding="utf-8")).get("work_part")
        except (OSError, ValueError):
            return None

    def _set_work_part(self, path: str | None) -> None:
        self.cfg.home.mkdir(parents=True, exist_ok=True)
        self.state_file.write_text(json.dumps({"work_part": path}), encoding="utf-8")

    # ---- running NX
    def _journal_path(self) -> Path:
        installed = self.cfg.receiver_dir / "NxAiBridgeReceiver.cs"
        if installed.is_file():
            return installed
        raise BridgeError(
            "NOT_INSTALLED",
            f"Receiver journals not found in {self.cfg.receiver_dir}.",
            hint="Run `nx-ai-bridge install-receiver` (or install.ps1) first.",
        )

    def _run_nx(self, job_dir: Path, timeout: float) -> None:
        exe = self.cfg.run_journal_exe
        if not exe or not Path(exe).is_file():
            raise BridgeError(
                "NX_NOT_FOUND",
                "run_journal.exe was not found.",
                hint="Set NX_AI_BRIDGE_NX_DIR (or UGII_BASE_DIR) to the NX installation folder, "
                "or NX_AI_BRIDGE_RUN_JOURNAL to the full path of run_journal.exe.",
            )
        env = dict(os.environ)
        env["NX_AI_BRIDGE_HOME"] = str(self.cfg.home)
        if self.cfg.nx_base_dir:  # run_journal.exe needs the NX environment
            env.setdefault("UGII_BASE_DIR", self.cfg.nx_base_dir)
            env.setdefault("UGII_ROOT_DIR", str(Path(self.cfg.nx_base_dir) / "UGII") + os.sep)
        cmd = [exe, str(self._journal_path()), "-args", "--batch", str(job_dir)]
        try:
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, env=env, errors="replace")
        except subprocess.TimeoutExpired as exc:
            raise BridgeError("TIMEOUT", f"run_journal.exe did not finish within {timeout:.0f} s.") from exc
        (job_dir / "run_journal.log").write_text(
            f"exit code: {proc.returncode}\n--- stdout ---\n{proc.stdout}\n--- stderr ---\n{proc.stderr}\n",
            encoding="utf-8",
        )

    def _execute(self, requests: list[dict], timeout: float, stop_on_error: bool) -> list[dict]:
        job_dir = self.cfg.home / "batch" / f"{time.strftime('%Y%m%d-%H%M%S')}-{uuid.uuid4().hex[:6]}"
        job_dir.mkdir(parents=True, exist_ok=True)
        (job_dir / "requests.json").write_text(
            json.dumps({"stop_on_error": stop_on_error, "requests": requests}, ensure_ascii=False), encoding="utf-8"
        )
        self._runner(job_dir, timeout)
        resp_file = job_dir / "responses.json"
        if not resp_file.is_file():
            log = ""
            try:
                log = (job_dir / "run_journal.log").read_text(encoding="utf-8")[-3000:]
            except OSError:
                pass
            raise BridgeError(
                "BATCH_FAILED",
                "run_journal.exe finished but the receiver wrote no responses.",
                hint="See the log; common causes: no NX licence available, or the journal failed to compile "
                f"(check {self.cfg.home / 'logs'}).",
                details={"job_dir": str(job_dir), "log_tail": log},
            )
        return json.loads(resp_file.read_text(encoding="utf-8-sig"))

    def send_many(self, requests: list[dict], timeout: float, stop_on_error: bool = True) -> list[dict]:
        from .tools import TOOLS_BY_NAME  # local import: avoid cycle

        if not requests:
            return []
        work = self._work_part()
        tools = [r["tool"] for r in requests]
        prefix: list[dict] = []
        suffix: list[dict] = []
        opens_own_part = tools[0] in ("new_part", "open_part")
        if work and not opens_own_part and tools != ["nx_status"]:
            prefix.append(make_request("open_part", {"path": work}))
        modifies = any(not TOOLS_BY_NAME[t].readonly for t in tools if t in TOOLS_BY_NAME)
        last_part_tool = next((t for t in reversed(tools) if t in ("new_part", "open_part", "save_part", "save_part_as", "close_part")), None)
        if modifies and last_part_tool not in ("save_part", "save_part_as", "close_part") and (work or opens_own_part):
            suffix.append(make_request("save_part", {}))
        responses = self._execute(prefix + requests + suffix, timeout, stop_on_error)
        by_id = {r.get("id"): r for r in responses}
        # a failing implicit open is reported for the first user request
        if prefix and not by_id.get(prefix[0]["id"], {}).get("ok", False):
            failed = dict(by_id.get(prefix[0]["id"]) or {"ok": False, "error": {"code": "BATCH_FAILED", "message": "implicit open_part did not run"}})
            failed["id"] = requests[0]["id"]
            return [failed]
        out = [by_id[r["id"]] for r in requests if r["id"] in by_id]
        # track the work part for the next session
        for req, resp in zip(requests, out):
            if not resp.get("ok"):
                continue
            if req["tool"] in ("new_part", "open_part", "save_part_as"):
                self._set_work_part((resp.get("result") or {}).get("path") or req["params"].get("path"))
            elif req["tool"] == "close_part":
                self._set_work_part(None)
        if suffix:
            save = by_id.get(suffix[0]["id"])
            if out and save is not None and not save.get("ok") and out[-1].get("ok"):
                out[-1].setdefault("meta", {})["warning"] = "implicit save_part failed: " + str(save.get("error"))
        return out

    def send(self, request: dict, timeout: float) -> dict:
        out = self.send_many([request], timeout)
        if not out:
            raise BridgeError("BATCH_FAILED", "The batch session produced no response for the request.")
        return out[0]


class MockTransport(Transport):
    """In-process mock receiver - no NX, no sockets.  For demos and tests."""

    name = "mock"

    def __init__(self, cfg: Config):
        from .mock import MockReceiver

        self.receiver = MockReceiver(home=cfg.home)

    def send(self, request: dict, timeout: float) -> dict:
        return self.receiver.handle(request)


def select_transport(cfg: Config) -> Transport:
    """Pick the transport for ``cfg.transport`` ("auto" inspects the heartbeat file)."""
    mode = cfg.transport
    if mode == "mock":
        return MockTransport(cfg)
    if mode == "batch":
        return BatchTransport(cfg)
    if mode == "tcp":
        return TcpTransport(cfg)
    if mode == "file":
        return FileTransport(cfg)
    state = read_receiver_state(cfg)
    if receiver_is_fresh(state):
        port = int(state.get("tcp_port") or 0)
        if port > 0:
            return TcpTransport(cfg, port=port)
        return FileTransport(cfg)
    # no heartbeat: a receiver may still be listening on the default port
    return TcpTransport(cfg)


def install_receiver(cfg: Config) -> Path:
    """Copy the receiver journals to ``<home>/nx_receiver`` (the fixed location the journals expect)."""
    import shutil

    src = packaged_receiver_dir()
    if src is None:
        raise BridgeError("NOT_INSTALLED", "The nx_receiver folder is missing from this installation.")
    dst = cfg.receiver_dir
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(src, dst)
    return dst
