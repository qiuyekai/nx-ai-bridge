"""Single source of truth for every tool the bridge exposes.

The MCP server, the CLI, the mock receiver, the docs generator and the tests
all read this registry.  The NX receiver (nx_receiver/handlers/*.cs) implements
the same names.

Conventions shared by all tools
-------------------------------
* All lengths are MILLIMETRES, all angles are DEGREES.
* Points / vectors are JSON arrays ``[x, y, z]`` in the absolute coordinate
  system of the work part.
* Bodies are referenced by the name returned from the tool that created them
  (for example ``"B1"``) or by the name of the feature that created them (for
  example ``"BLOCK(1)"``).
* Every tool returns ``{"ok": true, "result": {...}}`` or
  ``{"ok": false, "error": {"code", "message", "nx_error"?, "hint"?}}``.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

UNITS_NOTE = "Units: millimetres (mm) and degrees."


# --------------------------------------------------------------------------
# tiny schema helpers
# --------------------------------------------------------------------------
def num(desc: str, default: float | None = None, minimum: float | None = None, inclusive: bool = False) -> dict:
    """Number schema. ``minimum=0`` means strictly positive unless ``inclusive``."""
    d: dict[str, Any] = {"type": "number", "description": desc}
    if default is not None:
        d["default"] = default
    if minimum is not None:
        d["minimum" if inclusive else "exclusiveMinimum"] = minimum
    return d


def integer(desc: str, default: int | None = None) -> dict:
    d: dict[str, Any] = {"type": "integer", "description": desc}
    if default is not None:
        d["default"] = default
    return d


def string(desc: str, default: str | None = None, enum: list[str] | None = None) -> dict:
    d: dict[str, Any] = {"type": "string", "description": desc}
    if default is not None:
        d["default"] = default
    if enum:
        d["enum"] = enum
    return d


def boolean(desc: str, default: bool | None = None) -> dict:
    d: dict[str, Any] = {"type": "boolean", "description": desc}
    if default is not None:
        d["default"] = default
    return d


def vec3(desc: str, default: list[float] | None = None) -> dict:
    d: dict[str, Any] = {
        "type": "array",
        "items": {"type": "number"},
        "minItems": 3,
        "maxItems": 3,
        "description": desc + " [x, y, z]",
    }
    if default is not None:
        d["default"] = default
    return d


def vec2(desc: str) -> dict:
    return {
        "type": "array",
        "items": {"type": "number"},
        "minItems": 2,
        "maxItems": 2,
        "description": desc + " [x, y] in sketch coordinates (mm)",
    }


BOOLEAN_PARAM = string(
    "Boolean with an existing body: none = create a new body.",
    default="none",
    enum=["none", "unite", "subtract", "intersect"],
)
TARGET_PARAM = string(
    "Target body for the boolean (body name or feature name). "
    "Required when boolean is not 'none' and the part has more than one body."
)
EDGES_PARAM = {
    "type": "array",
    "description": (
        "Edges to modify. Each item is either an edge id from list_edges "
        '(e.g. "E:B1:3") or an object {"near": [x, y, z], "body": "B1"?} that picks '
        "the edge closest to that point."
    ),
    "items": {"type": ["string", "object"]},
}
FACES_PARAM = {
    "type": "array",
    "description": (
        "Faces. Each item is either a face id from list_faces (e.g. \"F:B1:2\"), an object "
        '{"near": [x, y, z], "body": "B1"?} (closest face), or {"normal": [x, y, z], "body": "B1"?} '
        "(the outermost planar face whose outward normal points in that direction, e.g. [0,0,1] = top face)."
    ),
    "items": {"type": ["string", "object"]},
}


@dataclass(frozen=True)
class Tool:
    name: str
    group: str
    description: str
    properties: dict[str, dict] = field(default_factory=dict)
    required: tuple[str, ...] = ()
    readonly: bool = False  # does not change the model (no save needed in batch mode)
    bridge_only: bool = False  # implemented in the Python bridge, not in the receiver

    @property
    def input_schema(self) -> dict:
        return {
            "type": "object",
            "properties": self.properties,
            "required": list(self.required),
            "additionalProperties": False,
        }


def _t(name, group, description, properties=None, required=(), readonly=False, bridge_only=False) -> Tool:
    return Tool(name, group, description, properties or {}, tuple(required), readonly, bridge_only)


TOOLS: list[Tool] = [
    # ---------------------------------------------------------------- basics
    _t(
        "nx_status",
        "basic",
        "Check the connection to NX. Returns NX version, receiver mode (live/batch/mock), "
        "the work part path and whether it has unsaved changes. Call this first.",
        readonly=True,
    ),
    _t(
        "get_model_info",
        "basic",
        "Summarise the work part: feature list, bodies with bounding box [xmin,ymin,zmin,xmax,ymax,zmax] (mm), "
        "volume (mm^3), surface area (mm^2) and centroid. " + UNITS_NOTE,
        {"include_features": boolean("Include the feature list.", True)},
        readonly=True,
    ),
    _t(
        "run_journal",
        "basic",
        "ESCAPE HATCH: compile and run arbitrary NX Open code inside NX. Use it when no dedicated tool exists. "
        "The code must be a complete journal with a public static/shared Main method (C# class or VB Module). "
        "It may return a string from Main; text written to Console is captured too. "
        "Compile errors and NX exceptions are returned verbatim so you can fix the code and retry. "
        "WARNING: this executes arbitrary code with the user's privileges.",
        {
            "code": string("Complete journal source code."),
            "language": string("Source language.", "vb", ["vb", "csharp"]),
            "args": {"type": "array", "items": {"type": "string"}, "description": "Arguments passed to Main(args).", "default": []},
        },
        required=("code",),
    ),
    _t(
        "nx_api_lookup",
        "basic",
        "Search the NX Open .NET API that is actually loaded in the running NX (works offline, no docs needed). "
        "Give `type` (e.g. 'NXOpen.Features.ExtrudeBuilder') to list its members with signatures, and/or `query` "
        "to find types/members whose name contains the text. Use before writing run_journal code.",
        {
            "type": string("Full or short type name to describe."),
            "query": string("Case-insensitive substring to search for in type and member names."),
            "limit": integer("Maximum number of results.", 60),
        },
        readonly=True,
    ),
    _t(
        "nx_batch",
        "basic",
        "Run several tool calls in order as one unit. In batch mode (no NX window) this starts NX only once, "
        "which is much faster than separate calls. Returns one result per command.",
        {
            "commands": {
                "type": "array",
                "description": 'List of {"tool": name, "params": {...}} objects.',
                "items": {"type": "object"},
            },
            "stop_on_error": boolean("Stop at the first failing command.", True),
        },
        required=("commands",),
        bridge_only=True,
    ),
    # ----------------------------------------------------------------- parts
    _t(
        "new_part",
        "part",
        "Create a new, empty millimetre part and make it the work part.",
        {"path": string("Absolute path of the .prt file to create, e.g. C:\\\\work\\\\bracket.prt. Must not exist yet.")},
        required=("path",),
    ),
    _t(
        "open_part",
        "part",
        "Open an existing .prt file and make it the work part.",
        {"path": string("Absolute path of the .prt file.")},
        required=("path",),
    ),
    _t("save_part", "part", "Save the work part to its current file."),
    _t(
        "save_part_as",
        "part",
        "Save the work part under a new file name; the new file becomes the work part.",
        {"path": string("Absolute path of the new .prt file.")},
        required=("path",),
    ),
    _t(
        "close_part",
        "part",
        "Close the work part. Unsaved changes are discarded unless save=true.",
        {"save": boolean("Save before closing.", False)},
    ),
    # ------------------------------------------------------------ primitives
    _t(
        "create_block",
        "primitive",
        "Create a rectangular block. `origin` is the corner with the smallest x/y/z; the block extends "
        "length along +X, width along +Y, height along +Z. " + UNITS_NOTE,
        {
            "length": num("Size along X (mm).", minimum=0),
            "width": num("Size along Y (mm).", minimum=0),
            "height": num("Size along Z (mm).", minimum=0),
            "origin": vec3("Corner point.", [0, 0, 0]),
            "boolean": BOOLEAN_PARAM,
            "target": TARGET_PARAM,
        },
        required=("length", "width", "height"),
    ),
    _t(
        "create_cylinder",
        "primitive",
        "Create a cylinder. `origin` is the centre of the base circle; the cylinder grows along `direction`. " + UNITS_NOTE,
        {
            "diameter": num("Diameter (mm).", minimum=0),
            "height": num("Height (mm).", minimum=0),
            "origin": vec3("Centre of the base.", [0, 0, 0]),
            "direction": vec3("Axis direction.", [0, 0, 1]),
            "boolean": BOOLEAN_PARAM,
            "target": TARGET_PARAM,
        },
        required=("diameter", "height"),
    ),
    _t(
        "create_cone",
        "primitive",
        "Create a cone or truncated cone. `origin` is the centre of the base circle. " + UNITS_NOTE,
        {
            "base_diameter": num("Diameter at the base (mm).", minimum=0),
            "top_diameter": num("Diameter at the top (mm); 0 for a sharp cone.", 0, minimum=0, inclusive=True),
            "height": num("Height (mm).", minimum=0),
            "origin": vec3("Centre of the base.", [0, 0, 0]),
            "direction": vec3("Axis direction.", [0, 0, 1]),
            "boolean": BOOLEAN_PARAM,
            "target": TARGET_PARAM,
        },
        required=("base_diameter", "height"),
    ),
    _t(
        "create_sphere",
        "primitive",
        "Create a sphere. " + UNITS_NOTE,
        {
            "diameter": num("Diameter (mm).", minimum=0),
            "center": vec3("Centre point.", [0, 0, 0]),
            "boolean": BOOLEAN_PARAM,
            "target": TARGET_PARAM,
        },
        required=("diameter",),
    ),
    # ---------------------------------------------------------------- sketch
    _t(
        "create_sketch",
        "sketch",
        "Create an empty sketch on a principal plane, on an arbitrary plane, or on a planar face. "
        "Returns the sketch name used by sketch_* tools, extrude and revolve. Sketch coordinates are 2D (x, y) "
        "in the sketch plane: for XY the sketch x/y are absolute X/Y (normal +Z); for XZ they are absolute X/Z (normal -Y); "
        "for YZ they are absolute Y/Z (normal +X). The result reports origin, x_axis, y_axis and normal. "
        + UNITS_NOTE,
        {
            "plane": string("Principal plane (ignored when `face` or `x_axis`+`normal` is given).", "XY", ["XY", "XZ", "YZ"]),
            "origin": vec3("Sketch origin in absolute coordinates.", [0, 0, 0]),
            "x_axis": vec3("Custom plane: direction of the sketch x axis."),
            "normal": vec3("Custom plane: plane normal."),
            "face": {"type": ["string", "object"], "description": "Planar face to sketch on (same selector format as FACES)."},
            "name": string("Optional sketch name (letters, digits, underscore). Auto-generated when omitted."),
        },
    ),
    _t(
        "sketch_line",
        "sketch",
        "Add a straight line to a sketch.",
        {"sketch": string("Sketch name."), "start": vec2("Start point"), "end": vec2("End point")},
        required=("sketch", "start", "end"),
    ),
    _t(
        "sketch_circle",
        "sketch",
        "Add a full circle to a sketch.",
        {"sketch": string("Sketch name."), "center": vec2("Centre"), "radius": num("Radius (mm).", minimum=0)},
        required=("sketch", "center", "radius"),
    ),
    _t(
        "sketch_rectangle",
        "sketch",
        "Add an axis-aligned rectangle (four lines) to a sketch.",
        {"sketch": string("Sketch name."), "corner1": vec2("First corner"), "corner2": vec2("Opposite corner")},
        required=("sketch", "corner1", "corner2"),
    ),
    _t(
        "sketch_arc",
        "sketch",
        "Add a circular arc to a sketch, counter-clockwise from start_angle to end_angle "
        "(degrees, measured from the sketch x axis).",
        {
            "sketch": string("Sketch name."),
            "center": vec2("Centre"),
            "radius": num("Radius (mm).", minimum=0),
            "start_angle": num("Start angle in degrees."),
            "end_angle": num("End angle in degrees."),
        },
        required=("sketch", "center", "radius", "start_angle", "end_angle"),
    ),
    # -------------------------------------------------------------- features
    _t(
        "extrude",
        "feature",
        "Extrude all curves of a sketch along the sketch normal (or `direction`). The curves must form closed loops "
        "to get a solid. " + UNITS_NOTE,
        {
            "sketch": string("Sketch name."),
            "distance": num("End distance (mm) measured from the sketch plane. Negative extrudes the other way."),
            "start": num("Start distance (mm).", 0),
            "direction": vec3("Extrude direction; default is the sketch normal."),
            "boolean": BOOLEAN_PARAM,
            "target": TARGET_PARAM,
        },
        required=("sketch", "distance"),
    ),
    _t(
        "revolve",
        "feature",
        "Revolve all curves of a sketch about an axis given in ABSOLUTE coordinates. " + UNITS_NOTE,
        {
            "sketch": string("Sketch name."),
            "axis_point": vec3("A point on the axis.", [0, 0, 0]),
            "axis_direction": vec3("Axis direction."),
            "angle": num("End angle in degrees.", 360),
            "start_angle": num("Start angle in degrees.", 0),
            "boolean": BOOLEAN_PARAM,
            "target": TARGET_PARAM,
        },
        required=("sketch", "axis_direction"),
    ),
    _t(
        "create_hole",
        "feature",
        "Drill a simple round hole into a body. `position` is the centre of the hole on the entry face; the hole "
        "goes along `direction` (pointing INTO the material). Implemented as a subtracted cylinder so the geometry is exact. "
        + UNITS_NOTE,
        {
            "position": vec3("Centre of the hole at the entry face."),
            "direction": vec3("Drilling direction, into the material.", [0, 0, -1]),
            "diameter": num("Hole diameter (mm).", minimum=0),
            "depth": num("Hole depth (mm). Omit or use through=true for a through hole.", minimum=0),
            "through": boolean("Through hole (depth is computed from the body size).", False),
            "body": string("Body to drill (body or feature name). Optional when the part has exactly one body."),
        },
        required=("position", "diameter"),
    ),
    _t(
        "edge_blend",
        "feature",
        "Round (fillet) edges with a constant radius. Use list_edges to find edges. " + UNITS_NOTE,
        {
            "radius": num("Blend radius (mm).", minimum=0),
            "edges": EDGES_PARAM,
            "all_edges_of_body": string("Shortcut: blend every edge of this body."),
        },
        required=("radius",),
    ),
    _t(
        "chamfer",
        "feature",
        "Chamfer edges with a symmetric offset. Use list_edges to find edges. " + UNITS_NOTE,
        {
            "distance": num("Chamfer offset (mm).", minimum=0),
            "edges": EDGES_PARAM,
            "all_edges_of_body": string("Shortcut: chamfer every edge of this body."),
        },
        required=("distance",),
    ),
    _t(
        "shell",
        "feature",
        "Hollow out a solid body leaving walls of the given thickness. Faces listed in `remove_faces` are opened. "
        + UNITS_NOTE,
        {
            "thickness": num("Wall thickness (mm).", minimum=0),
            "remove_faces": FACES_PARAM,
            "body": string("Body to shell. Optional when the part has exactly one body."),
        },
        required=("thickness",),
    ),
    # -------------------------------------------------------------- booleans
    _t(
        "boolean_unite",
        "boolean",
        "Unite (add) tool bodies to the target body. The tools are consumed.",
        {"target": string("Target body."), "tools": {"type": "array", "items": {"type": "string"}, "description": "Tool bodies."}},
        required=("target", "tools"),
    ),
    _t(
        "boolean_subtract",
        "boolean",
        "Subtract tool bodies from the target body. The tools are consumed.",
        {"target": string("Target body."), "tools": {"type": "array", "items": {"type": "string"}, "description": "Tool bodies."}},
        required=("target", "tools"),
    ),
    _t(
        "boolean_intersect",
        "boolean",
        "Keep only the volume shared by the target body and the tool bodies. The tools are consumed.",
        {"target": string("Target body."), "tools": {"type": "array", "items": {"type": "string"}, "description": "Tool bodies."}},
        required=("target", "tools"),
    ),
    # ------------------------------------------------------------ management
    _t("list_features", "manage", "List all features of the work part in timestamp order.", readonly=True),
    _t(
        "delete_feature",
        "manage",
        "Delete a feature (and features that depend on it).",
        {"feature": string('Feature name as shown by list_features, e.g. "BLOCK(1)".')},
        required=("feature",),
    ),
    _t(
        "list_expressions",
        "manage",
        "List the expressions (parameters) of the work part with name, value and formula.",
        readonly=True,
    ),
    _t(
        "set_expression",
        "manage",
        "Change an expression (parameter) and update the model. Give either a numeric `value` or a `formula` string.",
        {
            "name": string('Expression name, e.g. "p0".'),
            "value": num("New numeric value (mm or degrees)."),
            "formula": string('New right-hand side, e.g. "p1*2".'),
        },
        required=("name",),
    ),
    _t(
        "undo",
        "manage",
        "Undo the last modelling steps (each modifying tool call is one step).",
        {"steps": integer("Number of steps to undo.", 1)},
    ),
    _t(
        "list_edges",
        "manage",
        "List the edges of a body with id, type, length, end points and mid point. Ids are valid until the body changes.",
        {"body": string("Body or feature name. Optional when the part has exactly one body.")},
        readonly=True,
    ),
    _t(
        "list_faces",
        "manage",
        "List the faces of a body with id, type, a point on the face, its normal/axis direction and bounding box.",
        {"body": string("Body or feature name. Optional when the part has exactly one body.")},
        readonly=True,
    ),
    # ---------------------------------------------------------------- export
    _t(
        "export_step",
        "export",
        "Export the work part as a STEP file (AP214).",
        {"path": string("Absolute output path ending in .stp or .step.")},
        required=("path",),
        readonly=True,
    ),
    _t(
        "export_stl",
        "export",
        "Export solid bodies of the work part as a binary STL file.",
        {
            "path": string("Absolute output path ending in .stl."),
            "bodies": {"type": "array", "items": {"type": "string"}, "description": "Bodies to export; default all solid bodies."},
            "tolerance": num("Facet (chordal) tolerance in mm.", 0.05, minimum=0),
        },
        required=("path",),
        readonly=True,
    ),
    _t(
        "screenshot",
        "export",
        "Save a PNG image of the NX graphics window so you can SEE the model. The MCP server returns the image itself. "
        "Only available in live mode (batch mode has no graphics window).",
        {
            "path": string("Absolute output path ending in .png. Auto-generated when omitted."),
            "view": string(
                "Orient the view before capturing; 'current' leaves the view untouched.",
                "isometric",
                ["current", "isometric", "trimetric", "top", "front", "right", "back", "bottom", "left"],
            ),
            "fit": boolean("Fit the model to the window first.", True),
        },
        readonly=True,
    ),
]

TOOLS_BY_NAME: dict[str, Tool] = {t.name: t for t in TOOLS}
GROUPS = ["basic", "part", "primitive", "sketch", "feature", "boolean", "manage", "export"]


class ParamError(ValueError):
    """Raised when tool parameters do not match the schema."""


def _type_ok(value: Any, typ: Any) -> bool:
    if isinstance(typ, list):
        return any(_type_ok(value, t) for t in typ)
    if typ == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if typ == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if typ == "string":
        return isinstance(value, str)
    if typ == "boolean":
        return isinstance(value, bool)
    if typ == "array":
        return isinstance(value, list)
    if typ == "object":
        return isinstance(value, dict)
    return True


def validate_params(tool_name: str, params: dict | None) -> dict:
    """Validate ``params`` against the tool schema and fill in defaults.

    Returns a new dict.  Raises ParamError with a message that tells an AI
    exactly what to fix.
    """
    tool = TOOLS_BY_NAME.get(tool_name)
    if tool is None:
        raise ParamError(f"Unknown tool '{tool_name}'. Available: {', '.join(TOOLS_BY_NAME)}")
    params = dict(params or {})
    unknown = [k for k in params if k not in tool.properties]
    if unknown:
        raise ParamError(
            f"{tool_name}: unknown parameter(s) {unknown}. Allowed: {sorted(tool.properties)}"
        )
    out: dict[str, Any] = {}
    for key, schema in tool.properties.items():
        if key not in params or params[key] is None:
            if key in tool.required:
                raise ParamError(f"{tool_name}: missing required parameter '{key}' ({schema.get('description', '')})")
            if "default" in schema:
                out[key] = schema["default"]
            continue
        value = params[key]
        typ = schema.get("type")
        if not _type_ok(value, typ):
            raise ParamError(f"{tool_name}: parameter '{key}' must be of type {typ}, got {value!r}")
        if "enum" in schema and value not in schema["enum"]:
            raise ParamError(f"{tool_name}: parameter '{key}' must be one of {schema['enum']}, got {value!r}")
        if typ == "array":
            n = len(value)
            if "minItems" in schema and n < schema["minItems"] or "maxItems" in schema and n > schema["maxItems"]:
                raise ParamError(f"{tool_name}: parameter '{key}' must have {schema.get('minItems')} numbers, got {value!r}")
            item_type = schema.get("items", {}).get("type")
            if item_type and not all(_type_ok(v, item_type) for v in value):
                raise ParamError(f"{tool_name}: every item of '{key}' must be of type {item_type}, got {value!r}")
        if typ == "number":
            if "exclusiveMinimum" in schema and not value > schema["exclusiveMinimum"]:
                raise ParamError(f"{tool_name}: parameter '{key}' must be > {schema['exclusiveMinimum']}, got {value!r}")
            if "minimum" in schema and value < schema["minimum"]:
                raise ParamError(f"{tool_name}: parameter '{key}' must be >= {schema['minimum']}, got {value!r}")
        out[key] = value
    return out
