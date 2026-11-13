"""Mock NX receiver.

Speaks exactly the same protocol as the real receiver that runs inside NX, so
the whole chain (AI -> MCP/CLI -> transport -> receiver) can be tested on any
machine without NX.

The geometry is deliberately simple: every body is tracked as a bounding box
plus a volume.  Volumes are exact for blocks, cylinders, cones, spheres,
holes in blocks, block shells and straight-edge blends/chamfers, and
approximate otherwise (results then carry ``"approximate": true``).
"""

from __future__ import annotations

import copy
import json
import math
import os
import re
import socket
import struct
import threading
import time
import zlib
from pathlib import Path
from typing import Any, Callable

from . import PROTOCOL_VERSION, __version__

Vec = list[float]


class NxError(Exception):
    """Simulates an exception raised by NX Open."""

    def __init__(self, message: str, code: str = "NX_ERROR", nx_code: int = 0, hint: str | None = None):
        super().__init__(message)
        self.code = code
        self.nx_code = nx_code
        self.hint = hint


# ------------------------------------------------------------------ vector helpers
def _sub(a, b):
    return [a[i] - b[i] for i in range(3)]


def _add(a, b):
    return [a[i] + b[i] for i in range(3)]


def _mul(a, s):
    return [a[i] * s for i in range(3)]


def _dot(a, b):
    return sum(a[i] * b[i] for i in range(3))


def _cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def _norm(a):
    n = math.sqrt(_dot(a, a))
    if n < 1e-12:
        raise NxError("Direction vector has zero length.", "INVALID_PARAMS")
    return [x / n for x in a]


def _bbox_of_points(pts: list[Vec]) -> list[float]:
    return [min(p[i] for p in pts) for i in range(3)] + [max(p[i] for p in pts) for i in range(3)]


def _bbox_union(a, b):
    return [min(a[i], b[i]) for i in range(3)] + [max(a[i + 3], b[i + 3]) for i in range(3)]


def _bbox_inter(a, b):
    lo = [max(a[i], b[i]) for i in range(3)]
    hi = [min(a[i + 3], b[i + 3]) for i in range(3)]
    if any(hi[i] - lo[i] <= 1e-9 for i in range(3)):
        return None
    return lo + hi


def _bbox_vol(b):
    return (b[3] - b[0]) * (b[4] - b[1]) * (b[5] - b[2])


def _disc_bbox(center: Vec, axis: Vec, radius: float) -> list[float]:
    ext = [radius * math.sqrt(max(0.0, 1 - axis[i] ** 2)) for i in range(3)]
    return [center[i] - ext[i] for i in range(3)] + [center[i] + ext[i] for i in range(3)]


def _ray_box_length(origin: Vec, direction: Vec, box: list[float]) -> float:
    """Length of the part of the ray (t >= 0) that lies inside the box."""
    t0, t1 = 0.0, float("inf")
    for i in range(3):
        if abs(direction[i]) < 1e-12:
            if origin[i] < box[i] - 1e-9 or origin[i] > box[i + 3] + 1e-9:
                return 0.0
            continue
        a = (box[i] - origin[i]) / direction[i]
        b = (box[i + 3] - origin[i]) / direction[i]
        t0, t1 = max(t0, min(a, b)), min(t1, max(a, b))
    return max(0.0, t1 - t0)


PLANES = {
    "XY": ([1.0, 0.0, 0.0], [0.0, 1.0, 0.0]),
    "XZ": ([1.0, 0.0, 0.0], [0.0, 0.0, 1.0]),
    "YZ": ([0.0, 1.0, 0.0], [0.0, 0.0, 1.0]),
}


class MockReceiver:
    """State machine that imitates the NX receiver."""

    def __init__(self, home: Path | None = None):
        self.home = Path(home) if home else None
        self.part: dict | None = None
        self.undo_stack: list[dict] = []
        self.lock = threading.Lock()
        self.calls = 0

    # ------------------------------------------------------------- dispatch
    def handle(self, request: dict) -> dict:
        t0 = time.perf_counter()
        rid = request.get("id") if isinstance(request, dict) else None
        with self.lock:
            self.calls += 1
            try:
                tool = request["tool"]
                params = request.get("params") or {}
                fn: Callable | None = getattr(self, "t_" + tool, None)
                if fn is None:
                    raise NxError(f"Unknown tool '{tool}'.", "UNKNOWN_TOOL")
                snapshot = copy.deepcopy(self.part) if tool in MODIFYING else None
                try:
                    result = fn(params)
                except Exception:
                    if snapshot is not None:
                        self.part = snapshot  # roll back, like UndoToMark in NX
                    raise
                if snapshot is not None and tool not in ("undo", "new_part", "open_part", "close_part", "save_part", "save_part_as"):
                    self.undo_stack.append(snapshot)
                    if self.part is not None:
                        self.part["modified"] = True
                resp: dict[str, Any] = {"id": rid, "ok": True, "result": result}
            except NxError as exc:
                err: dict[str, Any] = {"code": exc.code, "message": str(exc)}
                if exc.code == "NX_ERROR":
                    err["nx_error"] = {"type": "NXOpen.NXException", "message": str(exc), "error_code": exc.nx_code}
                if exc.hint:
                    err["hint"] = exc.hint
                resp = {"id": rid, "ok": False, "error": err}
            except (KeyError, TypeError, ValueError, IndexError) as exc:
                resp = {"id": rid, "ok": False, "error": {"code": "INVALID_PARAMS", "message": f"{type(exc).__name__}: {exc}"}}
        resp["meta"] = {"elapsed_ms": round((time.perf_counter() - t0) * 1000, 2), "receiver": "mock", "variant": "mock"}
        return resp

    # -------------------------------------------------------------- helpers
    def _work(self) -> dict:
        if self.part is None:
            raise NxError(
                "There is no work part.", "NO_WORK_PART", hint="Call new_part or open_part first."
            )
        return self.part

    def _new_feature(self, ftype: str, body: str | None, **extra) -> dict:
        part = self._work()
        part["timestamp"] += 1
        feat = {"name": f"{ftype}({part['timestamp']})", "type": ftype, "timestamp": part["timestamp"], "body": body}
        feat.update(extra)
        part["features"].append(feat)
        return feat

    def _add_expr(self, feature: dict, param: str, value: float) -> str:
        part = self._work()
        name = f"p{part['next_expr']}"
        part["next_expr"] += 1
        part["expressions"][name] = {"value": float(value), "formula": _fmt(value), "feature": feature["name"], "param": param, "units": "mm"}
        return name

    def _new_body(self, volume: float, bbox: list[float], kind: str, approximate: bool = False) -> str:
        part = self._work()
        part["next_body"] += 1
        name = f"B{part['next_body']}"
        part["bodies"][name] = {"volume": volume, "bbox": bbox, "kind": kind, "approximate": approximate}
        return name

    def _body(self, ref: str | None) -> str:
        part = self._work()
        bodies = part["bodies"]
        if not ref:
            if len(bodies) == 1:
                return next(iter(bodies))
            if not bodies:
                raise NxError("The work part has no solid body.", "NOT_FOUND")
            raise NxError(
                f"The part has {len(bodies)} bodies; specify which one. Bodies: {sorted(bodies)}", "INVALID_PARAMS"
            )
        if ref in bodies:
            return ref
        for feat in part["features"]:
            if feat["name"].upper() == ref.upper() and feat.get("body") in bodies:
                return feat["body"]
        raise NxError(f"Body '{ref}' not found. Bodies: {sorted(bodies)}", "NOT_FOUND", hint="Use get_model_info to list bodies.")

    def _finish_solid(self, ftype: str, volume: float, bbox: list[float], p: dict, kind: str, approximate=False, exprs=None) -> dict:
        """Create the body for a new solid feature and apply the optional boolean."""
        part = self._work()
        boolean = p.get("boolean", "none")
        if boolean == "none":
            body = self._new_body(volume, bbox, kind, approximate)
            feat = self._new_feature(ftype, body, base_volume=volume, creates_body=True, params=dict(p))
        else:
            target = self._body(p.get("target"))
            tb = part["bodies"][target]
            before = copy.deepcopy(tb)
            self._apply_boolean(boolean, tb, {"volume": volume, "bbox": bbox, "kind": kind, "approximate": approximate})
            body = target
            feat = self._new_feature(ftype, body, base_volume=volume, prev=before, params=dict(p))
        for key, val in (exprs or {}).items():
            self._add_expr(feat, key, val)
        return self._feature_result(feat)

    @staticmethod
    def _apply_boolean(op: str, target: dict, tool: dict) -> None:
        inter = _bbox_inter(target["bbox"], tool["bbox"])
        if inter is None:
            a, b = target["bbox"], tool["bbox"]
            touching = all(min(a[i + 3], b[i + 3]) - max(a[i], b[i]) >= -1e-9 for i in range(3))
            if op == "unite" and touching:  # bodies sharing a face can be united
                target["volume"] += tool["volume"]
                target["bbox"] = _bbox_union(a, b)
                exact = target["kind"] == "block" and tool["kind"] == "block"
                target["kind"] = "solid"
                target["approximate"] = bool(target.get("approximate")) or not exact or tool.get("approximate", False)
                return
            if op == "unite":
                raise NxError("Unite failed: the tool body does not touch the target body.", nx_code=670030)
            raise NxError("Tool body completely outside target body.", nx_code=670030)
        fill = min(target["volume"] / max(_bbox_vol(target["bbox"]), 1e-12), tool["volume"] / max(_bbox_vol(tool["bbox"]), 1e-12), 1.0)
        overlap = _bbox_vol(inter) * fill
        exact = target["kind"] == "block" and tool["kind"] == "block"
        if op == "unite":
            target["volume"] = target["volume"] + tool["volume"] - overlap
            target["bbox"] = _bbox_union(target["bbox"], tool["bbox"])
        elif op == "subtract":
            target["volume"] = target["volume"] - overlap
            if target["volume"] <= 1e-9:
                raise NxError("Subtract would remove the whole target body.", nx_code=670031)
        elif op == "intersect":
            target["volume"] = overlap
            target["bbox"] = inter
        else:
            raise NxError(f"Unknown boolean '{op}'.", "INVALID_PARAMS")
        target["kind"] = "solid"
        target["approximate"] = bool(target.get("approximate")) or not exact or tool.get("approximate", False)

    def _feature_result(self, feat: dict) -> dict:
        part = self._work()
        out = {"feature": feat["name"], "type": feat["type"], "bodies": [feat["body"]] if feat.get("body") else []}
        body = part["bodies"].get(feat.get("body") or "")
        if body:
            out["body"] = feat["body"]
            out["volume"] = round(body["volume"], 6)
            out["bbox"] = [round(v, 6) for v in body["bbox"]]
            if body.get("approximate"):
                out["approximate"] = True
        exprs = [n for n, e in part["expressions"].items() if e["feature"] == feat["name"]]
        if exprs:
            out["expressions"] = exprs
        return out

    # ------------------------------------------------------------- basics
    def t_nx_status(self, p):
        return {
            "connected": True,
            "mode": "mock",
            "nx_version": "MOCK (no NX installed)",
            "receiver_version": __version__,
            "protocol": PROTOCOL_VERSION,
            "work_part": self.part["path"] if self.part else None,
            "modified": bool(self.part and self.part["modified"]),
            "units": "mm",
        }

    def t_get_model_info(self, p):
        part = self._work()
        bodies = []
        for name, b in part["bodies"].items():
            bb = b["bbox"]
            item = {
                "name": name,
                "volume": round(b["volume"], 6),
                "bbox": [round(v, 6) for v in bb],
                "size": [round(bb[i + 3] - bb[i], 6) for i in range(3)],
            }
            if b.get("approximate"):
                item["approximate"] = True
            bodies.append(item)
        out = {"part": part["path"], "units": "mm", "modified": part["modified"], "body_count": len(bodies), "bodies": bodies}
        if bodies:
            box = bodies[0]["bbox"]
            for b in bodies[1:]:
                box = _bbox_union(box, b["bbox"])
            out["bbox"] = box
            out["total_volume"] = round(sum(b["volume"] for b in bodies), 6)
        if p.get("include_features", True):
            out["features"] = self.t_list_features({})["features"]
        return out

    def t_run_journal(self, p):
        code = p["code"]
        if not re.search(r"\bMain\b", code):
            raise NxError(
                "Compile error: no Main method found in the journal.",
                "COMPILE_ERROR",
                hint="The code must contain 'Public Sub Main' (VB) or 'public static void Main' (C#).",
            )
        return {"executed": False, "mock": True, "note": "The mock receiver does not execute NX Open code.", "stdout": "", "return": None}

    def t_nx_api_lookup(self, p):
        return {"mock": True, "results": [], "note": "API lookup needs a real NX session."}

    # --------------------------------------------------------------- parts
    def _blank(self, path: str) -> dict:
        return {
            "path": path, "modified": False, "timestamp": 0, "next_body": 0, "next_expr": 0, "next_sketch": 0,
            "features": [], "bodies": {}, "sketches": {}, "expressions": {},
        }

    def t_new_part(self, p):
        path = p["path"]
        if not path.lower().endswith(".prt"):
            raise NxError("Part file name must end with .prt", "INVALID_PARAMS")
        if os.path.exists(path):
            raise NxError(f"File already exists: {path}", nx_code=641058, hint="Use open_part, or choose another file name.")
        if not os.path.isdir(os.path.dirname(path) or "."):
            raise NxError(f"Folder does not exist: {os.path.dirname(path)}", nx_code=641002)
        self.part = self._blank(path)
        self.undo_stack.clear()
        return {"path": path, "units": "mm"}

    def t_open_part(self, p):
        path = p["path"]
        if not os.path.isfile(path):
            raise NxError(f"File not found: {path}", nx_code=641001)
        try:
            data = json.loads(Path(path).read_text(encoding="utf-8"))
            assert data.get("mock_nx_part") is True
        except Exception as exc:
            raise NxError("Not a mock part file (the mock receiver cannot read real NX parts).", nx_code=641003) from exc
        self.part = data["part"]
        self.part["path"] = path
        self.part["modified"] = False
        self.undo_stack.clear()
        return {"path": path, "units": "mm", "feature_count": len(self.part["features"])}

    def _write(self, path: str):
        part = self._work()
        if not os.path.isdir(os.path.dirname(path) or "."):
            raise NxError(f"Folder does not exist: {os.path.dirname(path)}", nx_code=641002)
        part["path"] = path
        part["modified"] = False
        Path(path).write_text(json.dumps({"mock_nx_part": True, "part": part}), encoding="utf-8")

    def t_save_part(self, p):
        self._write(self._work()["path"])
        return {"path": self.part["path"], "saved": True}

    def t_save_part_as(self, p):
        if os.path.exists(p["path"]):
            raise NxError(f"File already exists: {p['path']}", nx_code=641058)
        self._write(p["path"])
        return {"path": p["path"], "saved": True}

    def t_close_part(self, p):
        part = self._work()
        if p.get("save"):
            self._write(part["path"])
        path = part["path"]
        self.part = None
        self.undo_stack.clear()
        return {"closed": path}

    # ---------------------------------------------------------- primitives
    def t_create_block(self, p):
        o = p.get("origin", [0, 0, 0])
        l, w, h = p["length"], p["width"], p["height"]
        bbox = [o[0], o[1], o[2], o[0] + l, o[1] + w, o[2] + h]
        return self._finish_solid("BLOCK", l * w * h, bbox, p, "block", exprs={"length": l, "width": w, "height": h})

    def t_create_cylinder(self, p):
        o = p.get("origin", [0, 0, 0])
        d = _norm(p.get("direction", [0, 0, 1]))
        r, h = p["diameter"] / 2, p["height"]
        bbox = _bbox_union(_disc_bbox(o, d, r), _disc_bbox(_add(o, _mul(d, h)), d, r))
        return self._finish_solid("CYLINDER", math.pi * r * r * h, bbox, p, "cylinder", exprs={"diameter": p["diameter"], "height": h})

    def t_create_cone(self, p):
        o = p.get("origin", [0, 0, 0])
        d = _norm(p.get("direction", [0, 0, 1]))
        D, t, h = p["base_diameter"], p.get("top_diameter", 0), p["height"]
        if abs(D - t) < 1e-9:
            raise NxError("Base and top diameter of a cone must differ.", nx_code=670100)
        vol = math.pi * h / 12 * (D * D + D * t + t * t)
        bbox = _bbox_union(_disc_bbox(o, d, D / 2), _disc_bbox(_add(o, _mul(d, h)), d, t / 2))
        return self._finish_solid("CONE", vol, bbox, p, "cone", exprs={"base_diameter": D, "top_diameter": t, "height": h})

    def t_create_sphere(self, p):
        c = p.get("center", [0, 0, 0])
        r = p["diameter"] / 2
        bbox = [c[i] - r for i in range(3)] + [c[i] + r for i in range(3)]
        return self._finish_solid("SPHERE", math.pi * p["diameter"] ** 3 / 6, bbox, p, "sphere", exprs={"diameter": p["diameter"]})

    # -------------------------------------------------------------- sketch
    def t_create_sketch(self, p):
        part = self._work()
        origin = [float(v) for v in p.get("origin", [0, 0, 0])]
        if p.get("face") is not None:
            face = self._resolve_faces([p["face"]], None)[0]
            n = face["normal"]
            x = _norm(_cross([0, 0, 1], n)) if abs(n[2]) < 0.99 else [1.0, 0.0, 0.0]
            y = _cross(n, x)
            origin = face["point"]
        elif p.get("x_axis") and p.get("normal"):
            n = _norm(p["normal"])
            x = _norm(p["x_axis"])
            y = _cross(n, x)
        else:
            x, y = PLANES[p.get("plane", "XY")]
            n = _cross(x, y)
        part["next_sketch"] += 1
        name = (p.get("name") or f"SK{part['next_sketch']}").upper()
        if not re.fullmatch(r"[A-Z0-9_]{1,24}", name):
            raise NxError("Sketch name may only contain letters, digits and underscore (max 24).", "INVALID_PARAMS")
        if name in part["sketches"]:
            raise NxError(f"Sketch '{name}' already exists.", "INVALID_PARAMS")
        feat = self._new_feature("SKETCH", None, sketch=name)
        part["sketches"][name] = {"origin": origin, "x_axis": x, "y_axis": y, "normal": n, "curves": [], "feature": feat["name"]}
        return {"sketch": name, "feature": feat["name"], "origin": origin, "x_axis": x, "y_axis": y, "normal": n}

    def _sketch(self, name: str) -> dict:
        part = self._work()
        sk = part["sketches"].get(str(name).upper())
        if sk is None:
            raise NxError(f"Sketch '{name}' not found. Sketches: {sorted(part['sketches'])}", "NOT_FOUND")
        return sk

    def _add_curve(self, p, curve):
        sk = self._sketch(p["sketch"])
        sk["curves"].append(curve)
        return {"sketch": p["sketch"].upper(), "curve_count": len(sk["curves"]), "added": curve["type"]}

    def t_sketch_line(self, p):
        if p["start"] == p["end"]:
            raise NxError("Line has zero length.", nx_code=1105013)
        return self._add_curve(p, {"type": "line", "start": p["start"], "end": p["end"]})

    def t_sketch_circle(self, p):
        return self._add_curve(p, {"type": "circle", "center": p["center"], "radius": p["radius"]})

    def t_sketch_rectangle(self, p):
        (x1, y1), (x2, y2) = p["corner1"], p["corner2"]
        if x1 == x2 or y1 == y2:
            raise NxError("Rectangle has zero width or height.", "INVALID_PARAMS")
        pts = [[x1, y1], [x2, y1], [x2, y2], [x1, y2]]
        for i in range(4):
            out = self._add_curve(p, {"type": "line", "start": pts[i], "end": pts[(i + 1) % 4]})
        out["added"] = "rectangle"
        return out

    def t_sketch_arc(self, p):
        return self._add_curve(p, {"type": "arc", "center": p["center"], "radius": p["radius"], "start_angle": p["start_angle"], "end_angle": p["end_angle"]})

    @staticmethod
    def _profile(sk: dict):
        """Return (area, centroid2d, bbox2d, approximate) of the sketch profile."""
        curves = sk["curves"]
        if not curves:
            raise NxError("The sketch is empty.", nx_code=1105014, hint="Add curves with sketch_line / sketch_circle / ... first.")
        lines = [c for c in curves if c["type"] == "line"]
        circles = [c for c in curves if c["type"] == "circle"]
        arcs = [c for c in curves if c["type"] == "arc"]
        pts2: list[list[float]] = []
        for c in lines:
            pts2 += [c["start"], c["end"]]
        for c in circles + arcs:
            cx, cy, r = c["center"][0], c["center"][1], c["radius"]
            pts2 += [[cx - r, cy - r], [cx + r, cy + r]]
        bbox2 = [min(q[0] for q in pts2), min(q[1] for q in pts2), max(q[0] for q in pts2), max(q[1] for q in pts2)]
        approximate = bool(arcs)
        area = 0.0
        cen = [(bbox2[0] + bbox2[2]) / 2, (bbox2[1] + bbox2[3]) / 2]
        if lines:
            # chain the lines into one loop
            remaining = [(tuple(c["start"]), tuple(c["end"])) for c in lines]
            loop = [remaining[0][0], remaining[0][1]]
            remaining.pop(0)
            while remaining:
                for i, (a, b) in enumerate(remaining):
                    if _close2(a, loop[-1]):
                        loop.append(b)
                    elif _close2(b, loop[-1]):
                        loop.append(a)
                    else:
                        continue
                    remaining.pop(i)
                    break
                else:
                    break
            closed = _close2(loop[0], loop[-1]) and not remaining
            if not closed and not arcs:
                raise NxError(
                    "The section is not closed; a solid cannot be created from open curves.", nx_code=1105015,
                    hint="Make sure consecutive lines share end points and the last one returns to the start.",
                )
            s = cx = cy = 0.0
            for i in range(len(loop) - 1):
                (x0, y0), (x1, y1) = loop[i], loop[i + 1]
                cr = x0 * y1 - x1 * y0
                s += cr
                cx += (x0 + x1) * cr
                cy += (y0 + y1) * cr
            if abs(s) > 1e-12:
                area = abs(s) / 2
                cen = [cx / (3 * s), cy / (3 * s)]
            else:
                approximate = True
                area = (bbox2[2] - bbox2[0]) * (bbox2[3] - bbox2[1])
            area -= sum(math.pi * c["radius"] ** 2 for c in circles)  # circles inside = holes
        elif circles:
            big = max(circles, key=lambda c: c["radius"])
            area = math.pi * big["radius"] ** 2 - sum(math.pi * c["radius"] ** 2 for c in circles if c is not big)
            cen = list(big["center"])
        else:
            area = (bbox2[2] - bbox2[0]) * (bbox2[3] - bbox2[1]) * 0.7
        if area <= 1e-9:
            raise NxError("The section encloses no area.", nx_code=1105016)
        return area, cen, bbox2, approximate

    @staticmethod
    def _to3d(sk: dict, q) -> Vec:
        return _add(sk["origin"], _add(_mul(sk["x_axis"], q[0]), _mul(sk["y_axis"], q[1])))

    def t_extrude(self, p):
        sk = self._sketch(p["sketch"])
        area, _cen, b2, approx = self._profile(sk)
        start, end = p.get("start", 0), p["distance"]
        if abs(end - start) < 1e-9:
            raise NxError("Extrude start and end limits are equal.", nx_code=670200)
        d = _norm(p["direction"]) if p.get("direction") else sk["normal"]
        cos = abs(_dot(d, sk["normal"]))
        if cos < 1e-6:
            raise NxError("Extrude direction lies in the sketch plane.", nx_code=670201)
        corners = [self._to3d(sk, q) for q in ([b2[0], b2[1]], [b2[2], b2[1]], [b2[2], b2[3]], [b2[0], b2[3]])]
        pts = [_add(c, _mul(d, t)) for c in corners for t in (start, end)]
        kind = "block" if not approx and len(sk["curves"]) == 4 and all(c["type"] == "line" for c in sk["curves"]) and abs(area - (b2[2] - b2[0]) * (b2[3] - b2[1])) < 1e-9 and _axis_aligned(sk, d) else "solid"
        return self._finish_solid("EXTRUDE", area * abs(end - start) * cos, _bbox_of_points(pts), p, kind, approx, exprs={"start": start, "end": end})

    def t_revolve(self, p):
        sk = self._sketch(p["sketch"])
        area, cen, b2, _approx = self._profile(sk)
        axis = _norm(p["axis_direction"])
        ap = p.get("axis_point", [0, 0, 0])
        sweep = abs(p.get("angle", 360) - p.get("start_angle", 0))
        if sweep < 1e-9 or sweep > 360 + 1e-9:
            raise NxError("Revolve angle must be between 0 and 360 degrees.", nx_code=670210)
        c3 = self._to3d(sk, cen)
        rel = _sub(c3, ap)
        radial = _sub(rel, _mul(axis, _dot(rel, axis)))
        rc = math.sqrt(_dot(radial, radial))
        if rc < 1e-9:
            raise NxError("The section is centred on the revolve axis; it would self-intersect.", nx_code=670211)
        vol = area * 2 * math.pi * rc * sweep / 360
        corners = [self._to3d(sk, q) for q in ([b2[0], b2[1]], [b2[2], b2[1]], [b2[2], b2[3]], [b2[0], b2[3]])]
        rmax = max(math.sqrt(max(0.0, _dot(_sub(c, ap), _sub(c, ap)) - _dot(_sub(c, ap), axis) ** 2)) for c in corners)
        along = [_dot(_sub(c, ap), axis) for c in corners]
        lo, hi = _add(ap, _mul(axis, min(along))), _add(ap, _mul(axis, max(along)))
        bbox = _bbox_union(_disc_bbox(lo, axis, rmax), _disc_bbox(hi, axis, rmax))
        return self._finish_solid("REVOLVE", vol, bbox, p, "solid", True, exprs={"angle": p.get("angle", 360)})

    # ------------------------------------------------------------ features
    def t_create_hole(self, p):
        part = self._work()
        name = self._body(p.get("body"))
        body = part["bodies"][name]
        d = _norm(p.get("direction", [0, 0, -1]))
        r = p["diameter"] / 2
        inside = _ray_box_length(p["position"], d, body["bbox"])
        if inside <= 1e-9:
            raise NxError(
                "Tool body completely outside target body.", nx_code=670030,
                hint="Check position/direction: the direction must point INTO the material from the entry face.",
            )
        through = p.get("through", False) or p.get("depth") is None
        depth = inside if through else min(p["depth"], inside)
        before = copy.deepcopy(body)
        body["volume"] -= math.pi * r * r * depth
        if body["volume"] <= 0:
            raise NxError("The hole would remove the whole body.", nx_code=670031)
        if body["kind"] != "block":
            body["approximate"] = True
        body["kind"] = "solid" if body["kind"] != "block" else "block_with_holes"
        feat = self._new_feature("SIMPLE HOLE", name, prev=before, params=dict(p))
        self._add_expr(feat, "diameter", p["diameter"])
        self._add_expr(feat, "depth", depth)
        out = self._feature_result(feat)
        out["depth"] = round(depth, 6)
        out["through"] = bool(through)
        return out

    def _edges_of(self, name: str) -> list[dict]:
        b = self._work()["bodies"][name]["bbox"]
        lo, hi = b[:3], b[3:]
        edges = []
        for axis in range(3):
            o1, o2 = [a for a in range(3) if a != axis]
            for s1 in (lo, hi):
                for s2 in (lo, hi):
                    a = [0.0, 0.0, 0.0]
                    a[axis], a[o1], a[o2] = lo[axis], s1[o1], s2[o2]
                    e = list(a)
                    e[axis] = hi[axis]
                    edges.append({
                        "id": f"E:{name}:{len(edges)}", "body": name, "type": "linear",
                        "length": round(hi[axis] - lo[axis], 6), "start": a, "end": e,
                        "midpoint": [(a[i] + e[i]) / 2 for i in range(3)],
                    })
        return edges

    def _faces_of(self, name: str) -> list[dict]:
        b = self._work()["bodies"][name]["bbox"]
        c = [(b[i] + b[i + 3]) / 2 for i in range(3)]
        faces = []
        for axis in range(3):
            for sign, idx in ((-1.0, axis), (1.0, axis + 3)):
                pt = list(c)
                pt[axis] = b[idx]
                n = [0.0, 0.0, 0.0]
                n[axis] = sign
                fb = list(b)
                fb[axis] = fb[axis + 3] = b[idx]
                faces.append({"id": f"F:{name}:{len(faces)}", "body": name, "type": "planar", "point": pt, "normal": n, "bbox": fb})
        return faces

    def _resolve_edges(self, p) -> list[dict]:
        if p.get("all_edges_of_body"):
            return self._edges_of(self._body(p["all_edges_of_body"]))
        sels = p.get("edges") or []
        if not sels:
            raise NxError("Give `edges` or `all_edges_of_body`.", "INVALID_PARAMS", hint="Call list_edges to see the edges of a body.")
        out = []
        for sel in sels:
            if isinstance(sel, str):
                m = re.fullmatch(r"E:(.+):(\d+)", sel)
                if not m:
                    raise NxError(f"Bad edge id '{sel}' (expected 'E:<body>:<index>').", "INVALID_PARAMS")
                edges = self._edges_of(self._body(m.group(1)))
                if int(m.group(2)) >= len(edges):
                    raise NxError(f"Edge '{sel}' does not exist; the body has {len(edges)} edges.", "NOT_FOUND")
                out.append(edges[int(m.group(2))])
            else:
                near = sel["near"]
                names = [self._body(sel["body"])] if sel.get("body") else list(self._work()["bodies"])
                cands = [e for n in names for e in self._edges_of(n)]
                if not cands:
                    raise NxError("No edges found.", "NOT_FOUND")
                out.append(min(cands, key=lambda e: _dot(_sub(e["midpoint"], near), _sub(e["midpoint"], near))))
        return out

    def _resolve_faces(self, sels, default_body) -> list[dict]:
        out = []
        for sel in sels:
            if isinstance(sel, str):
                m = re.fullmatch(r"F:(.+):(\d+)", sel)
                if not m:
                    raise NxError(f"Bad face id '{sel}' (expected 'F:<body>:<index>').", "INVALID_PARAMS")
                faces = self._faces_of(self._body(m.group(1)))
                if int(m.group(2)) >= len(faces):
                    raise NxError(f"Face '{sel}' does not exist.", "NOT_FOUND")
                out.append(faces[int(m.group(2))])
                continue
            names = [self._body(sel.get("body") or default_body)] if (sel.get("body") or default_body) else list(self._work()["bodies"])
            cands = [f for n in names for f in self._faces_of(n)]
            if not cands:
                raise NxError("No faces found.", "NOT_FOUND")
            if "near" in sel:
                out.append(min(cands, key=lambda f: _dot(_sub(f["point"], sel["near"]), _sub(f["point"], sel["near"]))))
            elif "normal" in sel:
                n = _norm(sel["normal"])
                good = [f for f in cands if _dot(f["normal"], n) > 0.99]
                if not good:
                    raise NxError(f"No planar face with normal {sel['normal']}.", "NOT_FOUND")
                out.append(max(good, key=lambda f: _dot(f["point"], n)))
            else:
                raise NxError(f"Face selector needs 'near' or 'normal': {sel!r}", "INVALID_PARAMS")
        return out

    def _edge_feature(self, p, ftype: str, size: float, factor: float, label: str) -> dict:
        part = self._work()
        edges = self._resolve_edges(p)
        names = {e["body"] for e in edges}
        if len(names) != 1:
            raise NxError("All edges must belong to the same body.", "INVALID_PARAMS")
        name = names.pop()
        body = part["bodies"][name]
        dims = [body["bbox"][i + 3] - body["bbox"][i] for i in range(3)]
        if size * 2 > min(dims) + 1e-9 and len(edges) > 1 or size > min(dims) + 1e-9:
            raise NxError(f"{label} is too large for the body; the feature cannot be built.", nx_code=670300, hint="Use a smaller value.")
        before = copy.deepcopy(body)
        seen = set()
        for e in edges:
            if e["id"] not in seen:
                seen.add(e["id"])
                body["volume"] -= factor * size * size * e["length"]
        if body["kind"] != "block":
            body["approximate"] = True
        feat = self._new_feature(ftype, name, prev=before, params=dict(p))
        self._add_expr(feat, label.lower().replace(" ", "_"), size)
        out = self._feature_result(feat)
        out["edge_count"] = len(seen)
        return out

    def t_edge_blend(self, p):
        return self._edge_feature(p, "EDGE BLEND", p["radius"], 1 - math.pi / 4, "Blend radius")

    def t_chamfer(self, p):
        return self._edge_feature(p, "CHAMFER", p["distance"], 0.5, "Chamfer distance")

    def t_shell(self, p):
        part = self._work()
        name = self._body(p.get("body"))
        body = part["bodies"][name]
        t = p["thickness"]
        faces = self._resolve_faces(p.get("remove_faces") or [], name)
        b = body["bbox"]
        inner = []
        for axis in range(3):
            size = b[axis + 3] - b[axis]
            open_sides = sum(1 for f in faces if abs(f["normal"][axis]) > 0.99)
            inner.append(size - t * (2 - min(open_sides, 2)))
        if any(v <= 1e-9 for v in inner):
            raise NxError("Shell thickness is too large for the body.", nx_code=670400, hint="Use a smaller thickness.")
        before = copy.deepcopy(body)
        cavity = inner[0] * inner[1] * inner[2] * min(1.0, body["volume"] / max(_bbox_vol(b), 1e-12))
        body["volume"] -= cavity
        if body["kind"] != "block":
            body["approximate"] = True
        feat = self._new_feature("SHELL", name, prev=before, params=dict(p))
        self._add_expr(feat, "thickness", t)
        out = self._feature_result(feat)
        out["removed_faces"] = [f["id"] for f in faces]
        return out

    # ------------------------------------------------------------ booleans
    def _boolean(self, op: str, ftype: str, p) -> dict:
        part = self._work()
        target = self._body(p["target"])
        tools = [self._body(t) for t in p["tools"]]
        if not tools:
            raise NxError("`tools` must contain at least one body.", "INVALID_PARAMS")
        if target in tools:
            raise NxError("The target body cannot also be a tool body.", "INVALID_PARAMS")
        before = copy.deepcopy(part["bodies"][target])
        for t in tools:
            self._apply_boolean(op, part["bodies"][target], part["bodies"][t])
        consumed = {t: part["bodies"].pop(t) for t in tools}
        feat = self._new_feature(ftype, target, prev=before, consumed=consumed, params=dict(p))
        out = self._feature_result(feat)
        out["consumed"] = tools
        return out

    def t_boolean_unite(self, p):
        return self._boolean("unite", "UNITE", p)

    def t_boolean_subtract(self, p):
        return self._boolean("subtract", "SUBTRACT", p)

    def t_boolean_intersect(self, p):
        return self._boolean("intersect", "INTERSECT", p)

    # ---------------------------------------------------------- management
    def t_list_features(self, p):
        part = self._work()
        feats = []
        for f in part["features"]:
            item = {"name": f["name"], "type": f["type"], "timestamp": f["timestamp"], "suppressed": False}
            if f.get("body"):
                item["body"] = f["body"]
            exprs = [n for n, e in part["expressions"].items() if e["feature"] == f["name"]]
            if exprs:
                item["expressions"] = exprs
            feats.append(item)
        return {"count": len(feats), "features": feats}

    def t_delete_feature(self, p):
        part = self._work()
        wanted = p["feature"].upper()
        feat = next((f for f in part["features"] if f["name"].upper() == wanted), None)
        if feat is None:
            raise NxError(
                f"Feature '{p['feature']}' not found. Features: {[f['name'] for f in part['features']]}", "NOT_FOUND"
            )
        deleted = [feat["name"]]
        if feat["type"] == "SKETCH":
            part["sketches"].pop(feat["sketch"], None)
        elif feat.get("creates_body"):
            body = feat["body"]
            deleted += [f["name"] for f in part["features"] if f.get("body") == body and f is not feat]
            part["bodies"].pop(body, None)
        elif feat.get("prev") and feat.get("body") in part["bodies"]:
            later = [f for f in part["features"] if f.get("body") == feat["body"] and f["timestamp"] > feat["timestamp"]]
            if later:
                body = part["bodies"][feat["body"]]
                body["volume"] += feat["prev"]["volume"] - self._volume_after(feat)
                body["approximate"] = True
            else:
                part["bodies"][feat["body"]] = feat["prev"]
            for name, b in (feat.get("consumed") or {}).items():
                part["bodies"][name] = b
        part["features"] = [f for f in part["features"] if f["name"] not in deleted]
        for name in [n for n, e in part["expressions"].items() if e["feature"] in deleted]:
            del part["expressions"][name]
        return {"deleted": deleted}

    def _volume_after(self, feat: dict) -> float:
        """Volume of the feature's body right after the feature was applied (best effort)."""
        part = self._work()
        later = [f for f in part["features"] if f.get("body") == feat["body"] and f["timestamp"] > feat["timestamp"] and f.get("prev")]
        return later[0]["prev"]["volume"] if later else part["bodies"][feat["body"]]["volume"]

    def t_list_expressions(self, p):
        part = self._work()
        items = [
            {"name": n, "value": e["value"], "formula": e["formula"], "units": e["units"], "feature": e["feature"], "description": e["param"]}
            for n, e in part["expressions"].items()
        ]
        return {"count": len(items), "expressions": items}

    def t_set_expression(self, p):
        part = self._work()
        expr = part["expressions"].get(p["name"])
        if expr is None:
            raise NxError(f"Expression '{p['name']}' not found. Expressions: {list(part['expressions'])}", "NOT_FOUND")
        if p.get("value") is None and not p.get("formula"):
            raise NxError("Give `value` or `formula`.", "INVALID_PARAMS")
        if p.get("value") is not None:
            value, formula = float(p["value"]), _fmt(p["value"])
        else:
            formula = p["formula"]
            try:
                env = {n: e["value"] for n, e in part["expressions"].items()}
                value = float(eval(formula, {"__builtins__": {}}, env))  # noqa: S307 - mock only, names restricted
            except Exception as exc:
                raise NxError(f"Syntax error in expression: {formula}", nx_code=1050029) from exc
        if value <= 0 and expr["param"] not in ("start", "end", "top_diameter"):
            raise NxError(f"Update failed: {expr['param']} must be positive.", nx_code=670500)
        old = expr["value"]
        expr["value"], expr["formula"] = value, formula
        feat = next(f for f in part["features"] if f["name"] == expr["feature"])
        updated = False
        if feat.get("creates_body") and feat["type"] in ("BLOCK", "CYLINDER", "SPHERE", "CONE") and feat["body"] in part["bodies"]:
            params = dict(feat["params"])
            params[expr["param"]] = value
            old_base = feat["base_volume"]
            scratch = MockReceiver()
            scratch.part = self._blank("scratch.prt")
            params["boolean"] = "none"
            res = getattr(scratch, "t_create_" + feat["type"].lower())(params)
            body = part["bodies"][feat["body"]]
            only = not any(f.get("body") == feat["body"] and f is not feat for f in part["features"])
            body["volume"] += res["volume"] - old_base
            if only:
                body["bbox"] = res["bbox"]
            else:
                body["approximate"] = True
            feat["base_volume"], feat["params"] = res["volume"], {**feat["params"], expr["param"]: value}
            updated = True
        return {"name": p["name"], "old_value": old, "value": value, "formula": formula, "model_updated": updated, **({} if updated else {"approximate": True})}

    def t_undo(self, p):
        steps = int(p.get("steps", 1))
        if steps < 1:
            raise NxError("steps must be >= 1", "INVALID_PARAMS")
        if not self.undo_stack:
            raise NxError("Nothing to undo.", "NOTHING_TO_UNDO")
        done = 0
        while done < steps and self.undo_stack:
            self.part = self.undo_stack.pop()
            done += 1
        if self.part is not None:
            self.part["modified"] = True
        return {"undone": done}

    def t_list_edges(self, p):
        name = self._body(p.get("body"))
        edges = self._edges_of(name)
        return {"body": name, "count": len(edges), "edges": edges, "approximate": self._work()["bodies"][name]["kind"] != "block"}

    def t_list_faces(self, p):
        name = self._body(p.get("body"))
        faces = self._faces_of(name)
        return {"body": name, "count": len(faces), "faces": faces, "approximate": self._work()["bodies"][name]["kind"] != "block"}

    # -------------------------------------------------------------- export
    @staticmethod
    def _check_out(path: str, exts: tuple[str, ...]):
        if not path.lower().endswith(exts):
            raise NxError(f"Output file must end with one of {exts}.", "INVALID_PARAMS")
        if not os.path.isdir(os.path.dirname(path) or "."):
            raise NxError(f"Folder does not exist: {os.path.dirname(path)}", nx_code=641002)

    def t_export_step(self, p):
        part = self._work()
        self._check_out(p["path"], (".stp", ".step"))
        lines = ["ISO-10303-21;", "HEADER;", "FILE_DESCRIPTION(('nx-ai-bridge MOCK export - not real geometry'),'2;1');",
                 f"FILE_NAME('{os.path.basename(p['path'])}','',(''),(''),'nx-ai-bridge mock','','');",
                 "FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));", "ENDSEC;", "DATA;"]
        for i, (name, b) in enumerate(part["bodies"].items(), 1):
            lines.append(f"#{i}=PRODUCT('{name}','bbox {b['bbox']}','',());")
        lines += ["ENDSEC;", "END-ISO-10303-21;"]
        Path(p["path"]).write_text("\n".join(lines), encoding="utf-8")
        return {"path": p["path"], "bytes": os.path.getsize(p["path"]), "body_count": len(part["bodies"]), "mock": True}

    def t_export_stl(self, p):
        part = self._work()
        self._check_out(p["path"], (".stl",))
        names = [self._body(n) for n in p["bodies"]] if p.get("bodies") else list(part["bodies"])
        if not names:
            raise NxError("The work part has no solid body to export.", "NOT_FOUND")
        tris = []
        for name in names:
            b = part["bodies"][name]["bbox"]
            v = [[b[3 * ((i >> k) & 1) + k] for k in range(3)] for i in range(8)]
            for a, c, d, e in ((0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)):
                tris += [(v[a], v[c], v[d]), (v[a], v[d], v[e])]
        with open(p["path"], "wb") as fh:
            fh.write(b"nx-ai-bridge mock STL (bounding boxes only)".ljust(80, b" "))
            fh.write(struct.pack("<I", len(tris)))
            for t in tris:
                n = _cross(_sub(t[1], t[0]), _sub(t[2], t[0]))
                ln = math.sqrt(_dot(n, n)) or 1.0
                fh.write(struct.pack("<12fH", *[x / ln for x in n], *t[0], *t[1], *t[2], 0))
        return {"path": p["path"], "bytes": os.path.getsize(p["path"]), "triangles": len(tris), "body_count": len(names), "mock": True}

    def t_screenshot(self, p):
        part = self._work()
        path = p.get("path")
        if not path:
            raise NxError("screenshot needs a path.", "INVALID_PARAMS")
        self._check_out(path, (".png",))
        w, h = 480, 360
        img = bytearray(b"\xf4" * (w * h))
        view = p.get("view", "isometric")
        boxes = [b["bbox"] for b in part["bodies"].values()]

        def project(pt):
            x, y, z = pt
            if view == "top":
                return x, y
            if view == "bottom":
                return x, -y
            if view == "front":
                return x, z
            if view == "back":
                return -x, z
            if view == "right":
                return y, z
            if view == "left":
                return -y, z
            return (x - y) * 0.866, (x + y) * 0.5 + z

        segs = []
        for b in boxes:
            v = [[b[3 * ((i >> k) & 1) + k] for k in range(3)] for i in range(8)]
            for i in range(8):
                for k in range(3):
                    j = i | (1 << k)
                    if j != i:
                        segs.append((project(v[i]), project(v[j])))
        if segs:
            xs = [q[0] for s in segs for q in s]
            ys = [q[1] for s in segs for q in s]
            sx = max(max(xs) - min(xs), 1e-9)
            sy = max(max(ys) - min(ys), 1e-9)
            scale = min((w - 40) / sx, (h - 40) / sy)
            ox = (w - sx * scale) / 2 - min(xs) * scale
            oy = (h - sy * scale) / 2 - min(ys) * scale
            for (x0, y0), (x1, y1) in segs:
                steps = int(max(abs(x1 - x0), abs(y1 - y0)) * scale) + 1
                for i in range(steps + 1):
                    t = i / steps
                    px = int(ox + (x0 + (x1 - x0) * t) * scale)
                    py = h - 1 - int(oy + (y0 + (y1 - y0) * t) * scale)
                    if 0 <= px < w and 0 <= py < h:
                        img[py * w + px] = 0x20
        raw = b"".join(b"\x00" + bytes(img[r * w:(r + 1) * w]) for r in range(h))

        def chunk(tag: bytes, data: bytes) -> bytes:
            return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

        png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 0, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")
        Path(path).write_bytes(png)
        return {"path": path, "width": w, "height": h, "view": view, "mock": True, "note": "Mock image: wireframe of bounding boxes."}


MODIFYING = {
    "new_part", "open_part", "save_part", "save_part_as", "close_part", "create_block", "create_cylinder", "create_cone",
    "create_sphere", "create_sketch", "sketch_line", "sketch_circle", "sketch_rectangle", "sketch_arc", "extrude",
    "revolve", "create_hole", "edge_blend", "chamfer", "shell", "boolean_unite", "boolean_subtract",
    "boolean_intersect", "delete_feature", "set_expression", "undo",
}


def _fmt(v: float) -> str:
    return repr(float(v)).rstrip("0").rstrip(".") if "." in repr(float(v)) else repr(float(v))


def _close2(a, b, tol: float = 1e-6) -> bool:
    return abs(a[0] - b[0]) < tol and abs(a[1] - b[1]) < tol


def _axis_aligned(sk: dict, d: Vec) -> bool:
    return all(max(abs(c) for c in v) > 0.999999 for v in (sk["x_axis"], sk["y_axis"], d))


# ---------------------------------------------------------------------------
# stand-alone server: serves the mock over TCP *and* the file transport, and
# writes the same heartbeat file as the real receiver.
# ---------------------------------------------------------------------------
class MockServer:
    def __init__(self, home: Path, port: int = 0, tcp: bool = True, poll: float = 0.02):
        self.home = Path(home)
        self.receiver = MockReceiver(home=self.home)
        self.stop_event = threading.Event()
        self.poll = poll
        self.port = 0
        self._threads: list[threading.Thread] = []
        self._sock: socket.socket | None = None
        if tcp:
            self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            self._sock.bind(("127.0.0.1", port))  # loopback only
            self._sock.listen(8)
            self._sock.settimeout(0.2)
            self.port = self._sock.getsockname()[1]

    def start(self) -> "MockServer":
        (self.home / "ipc" / "requests").mkdir(parents=True, exist_ok=True)
        (self.home / "ipc" / "responses").mkdir(parents=True, exist_ok=True)
        self._write_state()
        targets = [self._file_loop, self._heartbeat_loop] + ([self._tcp_loop] if self._sock else [])
        for target in targets:
            th = threading.Thread(target=target, daemon=True)
            th.start()
            self._threads.append(th)
        return self

    def stop(self) -> None:
        self.stop_event.set()
        for th in self._threads:
            th.join(timeout=2)
        if self._sock:
            self._sock.close()
        try:
            (self.home / "receiver.json").unlink()
        except OSError:
            pass

    def _write_state(self) -> None:
        state = {
            "pid": os.getpid(), "mode": "mock", "tcp_port": self.port, "heartbeat": time.time(),
            "nx_version": "MOCK", "receiver_version": __version__, "protocol": PROTOCOL_VERSION,
        }
        tmp = self.home / "receiver.json.tmp"
        tmp.write_text(json.dumps(state), encoding="utf-8")
        os.replace(tmp, self.home / "receiver.json")

    def _heartbeat_loop(self) -> None:
        while not self.stop_event.wait(1.0):
            self._write_state()

    def _tcp_loop(self) -> None:
        assert self._sock is not None
        while not self.stop_event.is_set():
            try:
                conn, _addr = self._sock.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            with conn:
                conn.settimeout(5)
                buf = bytearray()
                try:
                    while not buf.endswith(b"\n"):
                        chunk = conn.recv(65536)
                        if not chunk:
                            break
                        buf.extend(chunk)
                    resp = self._answer(buf.decode("utf-8"))
                    conn.sendall((json.dumps(resp, ensure_ascii=False) + "\n").encode("utf-8"))
                except OSError:
                    pass

    def _file_loop(self) -> None:
        req_dir = self.home / "ipc" / "requests"
        resp_dir = self.home / "ipc" / "responses"
        while not self.stop_event.wait(self.poll):
            for req_file in sorted(req_dir.glob("*.json")):
                try:
                    text = req_file.read_text(encoding="utf-8")
                    req_file.unlink()
                except OSError:
                    continue
                resp = self._answer(text)
                rid = resp.get("id") or req_file.stem
                tmp = resp_dir / f"{rid}.json.tmp"
                tmp.write_text(json.dumps(resp, ensure_ascii=False), encoding="utf-8")
                os.replace(tmp, resp_dir / f"{rid}.json")

    def _answer(self, text: str) -> dict:
        try:
            request = json.loads(text)
        except ValueError as exc:
            return {"id": None, "ok": False, "error": {"code": "BAD_REQUEST", "message": f"Invalid JSON: {exc}"}}
        return self.receiver.handle(request)
