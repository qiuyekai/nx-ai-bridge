"""Reference model used by the demo command, the tests and (re-implemented in C#) the NX self-check.

An L-bracket: 80 x 40 x 10 base, 10 x 40 x 50 upright, one through hole in
each leg, one chamfered and one blended edge.  Volumes after each step are
known analytically, which makes the model a good end-to-end check.
"""

from __future__ import annotations

import math

from .client import BridgeClient

BASE = (80.0, 40.0, 10.0)
UPRIGHT = (10.0, 40.0, 50.0)
HOLE_BASE_D = 10.0
HOLE_UPRIGHT_D = 8.0
CHAMFER = 2.0
BLEND = 3.0

VOLUME_SOLID = BASE[0] * BASE[1] * BASE[2] + UPRIGHT[0] * UPRIGHT[1] * UPRIGHT[2]  # 52000
VOLUME_HOLES = VOLUME_SOLID - math.pi * (HOLE_BASE_D / 2) ** 2 * BASE[2] - math.pi * (HOLE_UPRIGHT_D / 2) ** 2 * UPRIGHT[0]
VOLUME_CHAMFER = VOLUME_HOLES - 0.5 * CHAMFER**2 * BASE[1]
VOLUME_FINAL = VOLUME_CHAMFER - (1 - math.pi / 4) * BLEND**2 * UPRIGHT[1]
BBOX_FINAL = [0.0, 0.0, 0.0, 80.0, 40.0, 60.0]


def bracket_steps(part_path: str) -> list[tuple[str, dict, float | None]]:
    """(tool, params, expected volume of the bracket after the step or None)."""
    return [
        ("new_part", {"path": part_path}, None),
        ("create_block", {"length": BASE[0], "width": BASE[1], "height": BASE[2]}, BASE[0] * BASE[1] * BASE[2]),
        (
            "create_block",
            {"length": UPRIGHT[0], "width": UPRIGHT[1], "height": UPRIGHT[2], "origin": [0, 0, BASE[2]], "boolean": "unite"},
            VOLUME_SOLID,
        ),
        ("create_hole", {"position": [55, 20, 10], "direction": [0, 0, -1], "diameter": HOLE_BASE_D, "through": True}, None),
        ("create_hole", {"position": [10, 20, 40], "direction": [-1, 0, 0], "diameter": HOLE_UPRIGHT_D, "through": True}, VOLUME_HOLES),
        # far top edge of the base, parallel to Y
        ("chamfer", {"distance": CHAMFER, "edges": [{"near": [80, 20, 10]}]}, VOLUME_CHAMFER),
        # outer top edge of the upright, parallel to Y
        ("edge_blend", {"radius": BLEND, "edges": [{"near": [0, 20, 60]}]}, VOLUME_FINAL),
        ("get_model_info", {}, VOLUME_FINAL),
        ("save_part", {}, None),
    ]


def run_bracket(client: BridgeClient, part_path: str, stop_on_error: bool = True) -> dict:
    """Build the bracket; returns a report with per-step results and volume checks."""
    steps = []
    ok = True
    for tool, params, expected in bracket_steps(part_path):
        resp = client.call(tool, params)
        step = {"tool": tool, "params": params, "ok": resp["ok"]}
        if resp["ok"]:
            result = resp["result"]
            volume = result.get("volume", result.get("total_volume"))
            step["volume"] = volume
            if expected is not None:
                step["expected_volume"] = round(expected, 6)
                step["volume_ok"] = volume is not None and abs(volume - expected) <= max(1e-3, expected * 1e-4)
            if tool == "get_model_info":
                step["bbox"] = result.get("bbox")
                step["bbox_ok"] = result.get("bbox") is not None and all(
                    abs(a - b) < 1e-3 for a, b in zip(result["bbox"], BBOX_FINAL)
                )
        else:
            step["error"] = resp["error"]
            ok = False
        steps.append(step)
        if not resp["ok"] and stop_on_error:
            break
    checks_ok = all(s.get("volume_ok", True) and s.get("bbox_ok", True) for s in steps)
    return {"ok": ok and checks_ok, "all_steps_ran": ok, "checks_ok": checks_ok, "part": part_path, "steps": steps}
