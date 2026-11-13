"""Configuration: environment variables override ``<home>/config.json``.

Home directory (shared with the NX receiver, which uses the same rule):
``$NX_AI_BRIDGE_HOME`` or ``~/.nx-ai-bridge``  (``%USERPROFILE%\\.nx-ai-bridge``).

Layout::

    <home>/config.json        written by install.ps1 (nx_base_dir, run_journal_exe, port, ...)
    <home>/receiver.json      heartbeat written by the running NX receiver
    <home>/ipc/requests/      file transport: bridge -> receiver
    <home>/ipc/responses/     file transport: receiver -> bridge
    <home>/nx_receiver/       installed copy of the receiver journals
    <home>/batch/             batch-mode job folders
    <home>/reports/           self-check reports
    <home>/screenshots/       default screenshot folder
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path

DEFAULT_PORT = 47110
DEFAULT_TIMEOUT = 120.0
TRANSPORTS = ("auto", "tcp", "file", "batch", "mock")


def home_dir() -> Path:
    env = os.environ.get("NX_AI_BRIDGE_HOME")
    return Path(env).expanduser() if env else Path.home() / ".nx-ai-bridge"


@dataclass
class Config:
    home: Path
    transport: str = "auto"
    host: str = "127.0.0.1"  # never anything but loopback
    port: int = DEFAULT_PORT
    timeout: float = DEFAULT_TIMEOUT
    run_journal_exe: str | None = None
    nx_base_dir: str | None = None

    @property
    def ipc_dir(self) -> Path:
        return self.home / "ipc"

    @property
    def receiver_state_file(self) -> Path:
        return self.home / "receiver.json"

    @property
    def receiver_dir(self) -> Path:
        return self.home / "nx_receiver"


def load_config(home: Path | None = None) -> Config:
    home = home or home_dir()
    data: dict = {}
    cfg_file = home / "config.json"
    if cfg_file.is_file():
        try:
            data = json.loads(cfg_file.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            data = {}
    env = os.environ
    transport = env.get("NX_AI_BRIDGE_TRANSPORT", data.get("transport", "auto")).lower()
    if transport not in TRANSPORTS:
        raise ValueError(f"NX_AI_BRIDGE_TRANSPORT must be one of {TRANSPORTS}, got {transport!r}")
    nx_base = env.get("NX_AI_BRIDGE_NX_DIR") or data.get("nx_base_dir") or env.get("UGII_BASE_DIR")
    run_journal = env.get("NX_AI_BRIDGE_RUN_JOURNAL") or data.get("run_journal_exe")
    if not run_journal and nx_base:
        for sub in ("NXBIN", "UGII"):
            cand = Path(nx_base) / sub / "run_journal.exe"
            if cand.is_file():
                run_journal = str(cand)
                break
    return Config(
        home=home,
        transport=transport,
        port=int(env.get("NX_AI_BRIDGE_PORT", data.get("port", DEFAULT_PORT))),
        timeout=float(env.get("NX_AI_BRIDGE_TIMEOUT", data.get("timeout", DEFAULT_TIMEOUT))),
        run_journal_exe=run_journal,
        nx_base_dir=nx_base,
    )


def packaged_receiver_dir() -> Path | None:
    """Location of the receiver journals shipped with this package / checkout."""
    here = Path(__file__).resolve().parent
    for cand in (here / "_nx_receiver", here.parent.parent / "nx_receiver"):
        if (cand / "NxAiBridgeReceiver.cs").is_file():
            return cand
    return None
