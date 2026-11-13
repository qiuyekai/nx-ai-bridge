"""High-level client: validate parameters, send to the receiver, normalise errors."""

from __future__ import annotations

import time
from typing import Any

from .config import Config, load_config
from .tools import TOOLS_BY_NAME, ParamError, validate_params
from .transport import BridgeError, Transport, make_request, select_transport


def _error(code: str, message: str, **extra: Any) -> dict:
    err = {"code": code, "message": message}
    err.update({k: v for k, v in extra.items() if v is not None})
    return {"ok": False, "error": err}


class BridgeClient:
    """Calls tools by name.  Never raises for tool failures - always returns the envelope."""

    def __init__(self, cfg: Config | None = None, transport: Transport | None = None):
        self.cfg = cfg or load_config()
        self._transport = transport

    @property
    def transport(self) -> Transport:
        # "auto" is re-evaluated on every call so that starting/stopping the
        # receiver in NX is picked up without restarting the MCP server.
        if self._transport is not None:
            return self._transport
        if self.cfg.transport in ("mock", "batch"):
            self._transport = select_transport(self.cfg)
            return self._transport
        return select_transport(self.cfg)

    def call(self, tool: str, params: dict | None = None, timeout: float | None = None) -> dict:
        timeout = timeout or self.cfg.timeout
        try:
            clean = validate_params(tool, params)
        except ParamError as exc:
            return _error("INVALID_PARAMS", str(exc))
        if tool == "nx_batch":
            return self._batch(clean, timeout)
        if tool == "screenshot" and not clean.get("path"):
            shots = self.cfg.home / "screenshots"
            shots.mkdir(parents=True, exist_ok=True)
            clean["path"] = str(shots / f"shot-{time.strftime('%Y%m%d-%H%M%S')}-{int(time.time() * 1000) % 1000:03d}.png")
        try:
            transport = self.transport
            resp = transport.send(make_request(tool, clean), timeout)
        except BridgeError as exc:
            return {"ok": False, "error": exc.to_error()}
        return self._normalise(resp, transport.name)

    def _batch(self, params: dict, timeout: float) -> dict:
        requests = []
        for i, cmd in enumerate(params["commands"]):
            name = cmd.get("tool") if isinstance(cmd, dict) else None
            if not name or name == "nx_batch" or name not in TOOLS_BY_NAME:
                return _error("INVALID_PARAMS", f"nx_batch: commands[{i}] needs a valid 'tool' name, got {cmd!r}")
            try:
                clean = validate_params(name, cmd.get("params") or {})
            except ParamError as exc:
                return _error("INVALID_PARAMS", f"nx_batch: commands[{i}]: {exc}")
            requests.append(make_request(name, clean))
        try:
            transport = self.transport
            responses = transport.send_many(requests, timeout, stop_on_error=params.get("stop_on_error", True))
        except BridgeError as exc:
            return {"ok": False, "error": exc.to_error()}
        results = []
        for req, resp in zip(requests, responses):
            item = self._normalise(resp, transport.name)
            item["tool"] = req["tool"]
            results.append(item)
        all_ok = len(results) == len(requests) and all(r["ok"] for r in results)
        out: dict[str, Any] = {
            "ok": all_ok,
            "result": {"completed": sum(1 for r in results if r["ok"]), "total": len(requests), "results": results},
        }
        if not all_ok:
            failed = next((r for r in results if not r["ok"]), None)
            out["error"] = {
                "code": "BATCH_STEP_FAILED",
                "message": f"Step {results.index(failed) + 1 if failed else len(results) + 1} of {len(requests)} failed"
                + (f" ({failed['tool']}): {failed['error'].get('message')}" if failed else "."),
            }
        return out

    @staticmethod
    def _normalise(resp: Any, transport_name: str) -> dict:
        if not isinstance(resp, dict) or "ok" not in resp:
            return _error("TRANSPORT", f"Malformed response from receiver: {str(resp)[:300]}")
        out: dict[str, Any] = {"ok": bool(resp["ok"])}
        if out["ok"]:
            out["result"] = resp.get("result") if resp.get("result") is not None else {}
        else:
            err = resp.get("error")
            out["error"] = err if isinstance(err, dict) else {"code": "UNKNOWN", "message": str(err)}
        meta = dict(resp.get("meta") or {})
        meta["transport"] = transport_name
        out["meta"] = meta
        return out
