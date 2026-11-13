import os
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from nx_ai_bridge.client import BridgeClient  # noqa: E402
from nx_ai_bridge.config import Config  # noqa: E402


@pytest.fixture
def home(tmp_path, monkeypatch):
    h = tmp_path / "home"
    h.mkdir()
    monkeypatch.setenv("NX_AI_BRIDGE_HOME", str(h))
    for var in ("NX_AI_BRIDGE_TRANSPORT", "NX_AI_BRIDGE_PORT", "NX_AI_BRIDGE_NX_DIR", "NX_AI_BRIDGE_RUN_JOURNAL", "UGII_BASE_DIR"):
        monkeypatch.delenv(var, raising=False)
    return h


@pytest.fixture
def mock_client(home):
    return BridgeClient(Config(home=home, transport="mock"))


@pytest.fixture
def part(tmp_path):
    return str(tmp_path / "model.prt")


def ok(resp):
    assert resp["ok"], resp.get("error")
    return resp["result"]
