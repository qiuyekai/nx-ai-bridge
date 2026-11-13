import re
from pathlib import Path

import pytest

from nx_ai_bridge.mock import MODIFYING, MockReceiver
from nx_ai_bridge.tools import GROUPS, TOOLS, TOOLS_BY_NAME, ParamError, validate_params

REPO = Path(__file__).resolve().parents[1]


def test_names_unique_and_grouped():
    assert len(TOOLS) == len(TOOLS_BY_NAME)
    for t in TOOLS:
        assert re.fullmatch(r"[a-z][a-z0-9_]*", t.name)
        assert t.group in GROUPS
        assert len(t.description) > 20
        assert set(t.required) <= set(t.properties)
        for key, schema in t.properties.items():
            assert "type" in schema and schema.get("description"), f"{t.name}.{key}"


def test_plan_coverage():
    """Every tool promised in the project plan exists."""
    wanted = """nx_status get_model_info run_journal new_part open_part save_part save_part_as
    create_block create_cylinder create_cone create_sphere create_sketch sketch_line sketch_circle
    sketch_rectangle sketch_arc extrude revolve create_hole edge_blend chamfer shell boolean_unite
    boolean_subtract boolean_intersect list_features delete_feature set_expression undo export_step
    export_stl screenshot""".split()
    assert not [w for w in wanted if w not in TOOLS_BY_NAME]


def test_units_documented():
    for name in ("create_block", "create_cylinder", "extrude", "create_hole", "edge_blend", "get_model_info"):
        assert "mm" in TOOLS_BY_NAME[name].description


def test_mock_implements_every_receiver_tool():
    for t in TOOLS:
        if not t.bridge_only:
            assert hasattr(MockReceiver, "t_" + t.name), t.name
            assert (t.name in MODIFYING) == (not t.readonly) or t.name in ("run_journal",), t.name


def test_nx_receiver_implements_every_tool():
    """Each receiver tool is claimed by a `// tools:` header in nx_receiver/handlers/*.cs."""
    claimed = set()
    for f in (REPO / "nx_receiver" / "handlers").glob("*.cs"):
        m = re.search(r"^//\s*tools:\s*(.+)$", f.read_text(encoding="utf-8"), re.M)
        assert m, f"{f.name} has no '// tools:' header"
        claimed |= {n.strip() for n in m.group(1).split(",")}
    expected = {t.name for t in TOOLS if not t.bridge_only}
    assert expected - claimed == set(), f"tools without NX handler: {sorted(expected - claimed)}"
    assert claimed - expected == set(), f"handlers for unknown tools: {sorted(claimed - expected)}"


def test_defaults_applied():
    p = validate_params("create_block", {"length": 1, "width": 2, "height": 3})
    assert p["origin"] == [0, 0, 0] and p["boolean"] == "none"


@pytest.mark.parametrize(
    "tool,params,fragment",
    [
        ("create_block", {"length": 1, "width": 2}, "missing required parameter 'height'"),
        ("create_block", {"length": 1, "width": 2, "height": 3, "colour": "red"}, "unknown parameter"),
        ("create_block", {"length": "1", "width": 2, "height": 3}, "must be of type number"),
        ("create_block", {"length": 0, "width": 2, "height": 3}, "must be > 0"),
        ("create_block", {"length": 1, "width": 2, "height": 3, "origin": [0, 0]}, "must have 3 numbers"),
        ("create_block", {"length": 1, "width": 2, "height": 3, "boolean": "glue"}, "must be one of"),
        ("create_cone", {"base_diameter": 5, "height": 3, "top_diameter": -1}, "must be >= 0"),
        ("no_such_tool", {}, "Unknown tool"),
    ],
)
def test_validation_errors(tool, params, fragment):
    with pytest.raises(ParamError) as exc:
        validate_params(tool, params)
    assert fragment in str(exc.value)


def test_cone_zero_top_allowed():
    assert validate_params("create_cone", {"base_diameter": 5, "height": 3, "top_diameter": 0})["top_diameter"] == 0


def test_receiver_sources_are_ascii_and_csharp5():
    """NX 10 compiles journals with the .NET Framework C# 5 compiler and guesses file encodings."""
    banned = [r'(?<![\w})\]"])\$"', r"\?\.", r"\bnameof\(", r"=>\s*[^;{]*;\s*$", r"\bout var\b", r"\bswitch\s*\{"]
    for f in sorted((REPO / "nx_receiver").rglob("*")):
        if f.suffix not in (".cs", ".vb"):
            continue
        text = f.read_bytes()
        assert all(b < 128 for b in text), f"{f.name} contains non-ASCII bytes (use \\uXXXX escapes)"
        if f.suffix == ".cs":
            for line_no, line in enumerate(text.decode("ascii").splitlines(), 1):
                code = line.split("//")[0]
                for pattern in banned:
                    assert not re.search(pattern, code), f"{f.name}:{line_no} uses C# 6+ syntax ({pattern}): {line.strip()}"


def test_selfcheck_loaders_are_in_sync():
    import subprocess
    import sys

    result = subprocess.run([sys.executable, str(REPO / "scripts" / "sync_loaders.py"), "--check"], capture_output=True, text=True)
    assert result.returncode == 0, result.stdout
