import math
import os

from conftest import ok

from nx_ai_bridge.demo import BBOX_FINAL, VOLUME_FINAL, run_bracket


def test_bracket_volumes(mock_client, part):
    report = run_bracket(mock_client, part)
    assert report["ok"], report
    assert os.path.isfile(part)
    info = ok(mock_client.call("get_model_info"))
    assert info["body_count"] == 1
    assert math.isclose(info["total_volume"], VOLUME_FINAL, rel_tol=1e-6)
    assert info["bbox"] == BBOX_FINAL
    assert [f["type"] for f in info["features"]] == ["BLOCK", "BLOCK", "SIMPLE HOLE", "SIMPLE HOLE", "CHAMFER", "EDGE BLEND"]


def test_no_work_part_error(mock_client):
    resp = mock_client.call("create_block", {"length": 1, "width": 1, "height": 1})
    assert not resp["ok"]
    assert resp["error"]["code"] == "NO_WORK_PART"
    assert "new_part" in resp["error"]["hint"]


def test_nx_error_is_structured_and_rolled_back(mock_client, part):
    ok(mock_client.call("new_part", {"path": part}))
    ok(mock_client.call("create_block", {"length": 10, "width": 10, "height": 10}))
    far = mock_client.call("create_cylinder", {"diameter": 2, "height": 5, "origin": [100, 100, 100], "boolean": "subtract"})
    assert not far["ok"]
    assert far["error"]["code"] == "NX_ERROR"
    assert far["error"]["nx_error"]["type"] == "NXOpen.NXException"
    assert "outside target" in far["error"]["nx_error"]["message"]
    info = ok(mock_client.call("get_model_info"))
    assert info["body_count"] == 1 and len(info["features"]) == 1  # failed call left nothing behind


def test_primitives(mock_client, part):
    ok(mock_client.call("new_part", {"path": part}))
    cyl = ok(mock_client.call("create_cylinder", {"diameter": 10, "height": 20, "direction": [1, 0, 0]}))
    assert math.isclose(cyl["volume"], math.pi * 25 * 20, rel_tol=1e-6)
    assert cyl["bbox"] == [0, -5, -5, 20, 5, 5]
    cone = ok(mock_client.call("create_cone", {"base_diameter": 10, "height": 12, "origin": [50, 0, 0]}))
    assert math.isclose(cone["volume"], math.pi * 25 * 12 / 3, rel_tol=1e-6)
    sph = ok(mock_client.call("create_sphere", {"diameter": 6, "center": [0, 50, 0]}))
    assert math.isclose(sph["volume"], math.pi * 36, rel_tol=1e-6)
    assert ok(mock_client.call("get_model_info"))["body_count"] == 3


def test_sketch_extrude_and_revolve(mock_client, part):
    ok(mock_client.call("new_part", {"path": part}))
    sk = ok(mock_client.call("create_sketch", {"plane": "XY"}))
    assert sk["normal"] == [0, 0, 1]
    ok(mock_client.call("sketch_rectangle", {"sketch": sk["sketch"], "corner1": [0, 0], "corner2": [30, 20]}))
    ok(mock_client.call("sketch_circle", {"sketch": sk["sketch"], "center": [15, 10], "radius": 5}))
    ext = ok(mock_client.call("extrude", {"sketch": sk["sketch"], "distance": 10}))
    assert math.isclose(ext["volume"], (600 - math.pi * 25) * 10, rel_tol=1e-6)
    assert ext["bbox"] == [0, 0, 0, 30, 20, 10]

    sk2 = ok(mock_client.call("create_sketch", {"plane": "XZ", "origin": [0, 100, 0]}))
    ok(mock_client.call("sketch_circle", {"sketch": sk2["sketch"], "center": [20, 0], "radius": 3}))
    rev = ok(mock_client.call("revolve", {"sketch": sk2["sketch"], "axis_point": [0, 100, 0], "axis_direction": [0, 0, 1]}))
    assert math.isclose(rev["volume"], math.pi * 9 * 2 * math.pi * 20, rel_tol=1e-6)  # torus


def test_open_sketch_cannot_be_extruded(mock_client, part):
    ok(mock_client.call("new_part", {"path": part}))
    sk = ok(mock_client.call("create_sketch", {}))["sketch"]
    ok(mock_client.call("sketch_line", {"sketch": sk, "start": [0, 0], "end": [10, 0]}))
    ok(mock_client.call("sketch_line", {"sketch": sk, "start": [10, 0], "end": [10, 10]}))
    resp = mock_client.call("extrude", {"sketch": sk, "distance": 5})
    assert not resp["ok"] and "not closed" in resp["error"]["message"]


def test_booleans_shell_undo_delete(mock_client, part):
    ok(mock_client.call("new_part", {"path": part}))
    a = ok(mock_client.call("create_block", {"length": 20, "width": 20, "height": 20}))["body"]
    b = ok(mock_client.call("create_block", {"length": 10, "width": 10, "height": 10, "origin": [15, 15, 15]}))["body"]
    sub = ok(mock_client.call("boolean_subtract", {"target": a, "tools": [b]}))
    assert math.isclose(sub["volume"], 8000 - 125)
    assert sub["consumed"] == [b]
    assert ok(mock_client.call("undo"))["undone"] == 1
    assert ok(mock_client.call("get_model_info"))["body_count"] == 2
    uni = ok(mock_client.call("boolean_unite", {"target": "BLOCK(1)", "tools": ["BLOCK(2)"]}))
    assert math.isclose(uni["volume"], 8000 + 1000 - 125)
    ok(mock_client.call("delete_feature", {"feature": uni["feature"]}))
    assert ok(mock_client.call("get_model_info"))["body_count"] == 2
    ok(mock_client.call("delete_feature", {"feature": "BLOCK(2)"}))
    shell = ok(mock_client.call("shell", {"thickness": 2, "remove_faces": [{"normal": [0, 0, 1]}]}))
    assert math.isclose(shell["volume"], 8000 - 16 * 16 * 18)
    missing = mock_client.call("delete_feature", {"feature": "NOPE(9)"})
    assert missing["error"]["code"] == "NOT_FOUND"


def test_expressions(mock_client, part):
    ok(mock_client.call("new_part", {"path": part}))
    blk = ok(mock_client.call("create_block", {"length": 10, "width": 10, "height": 10}))
    exprs = ok(mock_client.call("list_expressions"))["expressions"]
    assert [e["description"] for e in exprs] == ["length", "width", "height"]
    assert blk["expressions"] == [e["name"] for e in exprs]
    res = ok(mock_client.call("set_expression", {"name": exprs[2]["name"], "value": 25}))
    assert res["model_updated"]
    info = ok(mock_client.call("get_model_info"))
    assert math.isclose(info["total_volume"], 2500) and info["bbox"][5] == 25
    ok(mock_client.call("set_expression", {"name": exprs[0]["name"], "formula": f"{exprs[1]['name']}*2"}))
    assert math.isclose(ok(mock_client.call("get_model_info"))["total_volume"], 20 * 10 * 25)


def test_edges_and_faces(mock_client, part):
    ok(mock_client.call("new_part", {"path": part}))
    ok(mock_client.call("create_block", {"length": 10, "width": 20, "height": 30}))
    edges = ok(mock_client.call("list_edges"))
    assert edges["count"] == 12 and edges["edges"][0]["id"].startswith("E:B1:")
    faces = ok(mock_client.call("list_faces"))
    assert faces["count"] == 6
    res = ok(mock_client.call("edge_blend", {"radius": 1, "edges": [edges["edges"][0]["id"]]}))
    assert res["edge_count"] == 1
    too_big = mock_client.call("chamfer", {"distance": 50, "all_edges_of_body": "B1"})
    assert not too_big["ok"] and "smaller" in too_big["error"]["hint"]


def test_exports_and_screenshot(mock_client, part, tmp_path, home):
    ok(mock_client.call("new_part", {"path": part}))
    ok(mock_client.call("create_block", {"length": 10, "width": 20, "height": 30}))
    step = ok(mock_client.call("export_step", {"path": str(tmp_path / "m.stp")}))
    assert open(step["path"]).read().startswith("ISO-10303-21;")
    stl = ok(mock_client.call("export_stl", {"path": str(tmp_path / "m.stl")}))
    assert stl["triangles"] == 12 and stl["bytes"] == 84 + 12 * 50
    shot = ok(mock_client.call("screenshot", {"view": "front"}))
    assert shot["path"].startswith(str(home)) and open(shot["path"], "rb").read(8) == b"\x89PNG\r\n\x1a\n"
    bad = mock_client.call("export_stl", {"path": str(tmp_path / "m.txt")})
    assert bad["error"]["code"] == "INVALID_PARAMS"


def test_save_open_roundtrip(mock_client, part, tmp_path):
    ok(mock_client.call("new_part", {"path": part}))
    ok(mock_client.call("create_sphere", {"diameter": 10}))
    assert ok(mock_client.call("nx_status"))["modified"] is True
    ok(mock_client.call("save_part"))
    other = str(tmp_path / "copy.prt")
    ok(mock_client.call("save_part_as", {"path": other}))
    ok(mock_client.call("close_part"))
    assert ok(mock_client.call("nx_status"))["work_part"] is None
    ok(mock_client.call("open_part", {"path": part}))
    assert ok(mock_client.call("get_model_info"))["body_count"] == 1
    exists = mock_client.call("new_part", {"path": other})
    assert exists["error"]["code"] == "NX_ERROR" and "already exists" in exists["error"]["message"]


def test_batch_tool_on_live_transport(mock_client, part):
    resp = mock_client.call(
        "nx_batch",
        {"commands": [
            {"tool": "new_part", "params": {"path": part}},
            {"tool": "create_block", "params": {"length": 1, "width": 1, "height": 1}},
            {"tool": "create_hole", "params": {"position": [50, 50, 50], "diameter": 1}},
            {"tool": "save_part"},
        ]},
    )
    assert not resp["ok"]
    assert resp["result"]["completed"] == 2 and resp["result"]["total"] == 4
    assert "Step 3" in resp["error"]["message"]
    bad = mock_client.call("nx_batch", {"commands": [{"tool": "create_block", "params": {}}]})
    assert bad["error"]["code"] == "INVALID_PARAMS" and "commands[0]" in bad["error"]["message"]
