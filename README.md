# nx-ai-bridge

**Let any AI tool drive Siemens NX (UG) modelling.** [中文说明](README.zh-CN.md)

> ## ⚠️ ALPHA - not yet verified on a real NX installation
>
> The bridge (MCP server, CLI, mock) is tested on Windows, macOS and Linux. The part that runs **inside NX was written
> without access to NX** and has only been compiled and exercised against a stand-in for the NX Open API. Expect some
> tools to fail on real NX until the first [self-check](#self-check) reports have been worked through.
> See [Verification status](#verification-status).

Tell your AI assistant:

> Install the plugin https://github.com/qiuyekai/nx-ai-bridge and draw an L-bracket with two holes in NX.

The assistant reads [AGENTS.md](AGENTS.md), installs the bridge, asks you to start one journal inside NX, and then
models directly in your NX window while you watch.

```
AI tool ──MCP (stdio)──▶ bridge (Python) ──127.0.0.1──▶ receiver journal inside NX ──▶ NX Open API
   └────── or CLI ───────┘
```

- **Any AI tool**: an MCP server for Claude Code / Claude Desktop / Cursor / Codex / Gemini CLI / VS Code, and an
  identical command line interface for tools that can only run commands.
- **No installation inside NX**: the receiver is a *journal* (Alt+F8). No NX Open author licence, no compiled DLL,
  no administrator rights. Windows-side setup is a folder copy.
- **The AI can see and verify**: every modelling step returns the body's volume and bounding box; `screenshot`
  returns a picture of the NX window.
- **Errors the AI can act on**: structured JSON with NX's original error text; failed steps are rolled back.
- **Escape hatch**: `run_journal` executes NX Open code written by the AI; `nx_api_lookup` lets it explore the API
  of the running NX version without documentation.

## Supported versions

| | |
|---|---|
| NX | target **NX 10.0**; the code avoids newer APIs and adapts at run time, NX 11/12/1xxx/2xxx should need little work - untested |
| OS (NX side) | Windows 10 or newer |
| Python (bridge) | 3.10+ (or just [uv](https://docs.astral.sh/uv/), which fetches Python itself) |
| AI tools | anything that speaks MCP over stdio, or can run shell commands |

## Quick start (Windows with NX)

```powershell
git clone https://github.com/qiuyekai/nx-ai-bridge.git
cd nx-ai-bridge
powershell -ExecutionPolicy Bypass -File install.ps1
```

`install.ps1` needs no administrator rights. It locates NX, copies the receiver to
`%USERPROFILE%\.nx-ai-bridge\nx_receiver`, prepares Python and prints the configuration for your AI tool.
No Python on the machine? `install.ps1 -InstallUv` puts [uv](https://docs.astral.sh/uv/) into your user profile.

Then the **one manual step**, inside NX:

1. Start NX and open or create a part.
2. Press **Alt+F8** (menu **Tools > Journal > Play...**).
3. Select `%USERPROFILE%\.nx-ai-bridge\nx_receiver\NxAiBridgeReceiver.cs` and click **Run**.
4. A small "NX AI Bridge" window appears. Leave it open - closing it stops the receiver.

### Configure your AI tool

`install.ps1` prints the exact command for your machine. The generic form, using uv straight from GitHub:

<details open><summary>Claude Code</summary>

```powershell
claude mcp add nx -- uvx --from git+https://github.com/qiuyekai/nx-ai-bridge nx-ai-bridge serve
```
</details>

<details><summary>Cursor, Claude Desktop, Gemini CLI, Windsurf (<code>mcpServers</code> JSON)</summary>

`.cursor/mcp.json`, `claude_desktop_config.json`, `~/.gemini/settings.json`:

```json
{
  "mcpServers": {
    "nx": {
      "command": "uvx",
      "args": ["--from", "git+https://github.com/qiuyekai/nx-ai-bridge", "nx-ai-bridge", "serve"]
    }
  }
}
```
</details>

<details><summary>Codex CLI (<code>~/.codex/config.toml</code>)</summary>

```toml
[mcp_servers.nx]
command = "uvx"
args = ["--from", "git+https://github.com/qiuyekai/nx-ai-bridge", "nx-ai-bridge", "serve"]
```
</details>

<details><summary>VS Code (<code>.vscode/mcp.json</code>)</summary>

```json
{
  "servers": {
    "nx": { "type": "stdio", "command": "uvx", "args": ["--from", "git+https://github.com/qiuyekai/nx-ai-bridge", "nx-ai-bridge", "serve"] }
  }
}
```
</details>

<details><summary>No MCP? Use the command line</summary>

```powershell
nx-ai-bridge tools
nx-ai-bridge call nx_status
nx-ai-bridge call create_block length=100 width=50 height=20
nx-ai-bridge call create_hole '{"position": [50, 25, 20], "diameter": 10, "through": true}'
```

Every call prints a JSON envelope and exits with code 1 on failure.
</details>

A PyPI release (`pip install nx-ai-bridge` / `uvx nx-ai-bridge`) will follow once the receiver has been verified on real NX.

### Try it without NX

Works on any OS; the mock receiver fakes NX with simple geometry:

```bash
git clone https://github.com/qiuyekai/nx-ai-bridge.git && cd nx-ai-bridge
NX_AI_BRIDGE_TRANSPORT=mock uv run nx-ai-bridge demo --part /tmp/bracket.prt
```

## Tools

All lengths are **millimetres**, all angles **degrees**. `nx-ai-bridge tools <name>` shows the parameters of a tool.

| group | tools |
|---|---|
| basics | `nx_status` · `get_model_info` (features, bounding box, volume) · `run_journal` (arbitrary NX Open code) · `nx_api_lookup` · `nx_batch` |
| parts | `new_part` · `open_part` · `save_part` · `save_part_as` · `close_part` |
| primitives | `create_block` · `create_cylinder` · `create_cone` · `create_sphere` |
| sketch | `create_sketch` (principal plane, custom plane or planar face) · `sketch_line` · `sketch_circle` · `sketch_rectangle` · `sketch_arc` |
| features | `extrude` · `revolve` · `create_hole` · `edge_blend` · `chamfer` · `shell` |
| booleans | `boolean_unite` · `boolean_subtract` · `boolean_intersect` |
| manage | `list_features` · `delete_feature` · `list_expressions` · `set_expression` · `undo` · `list_edges` · `list_faces` |
| export | `export_step` · `export_stl` · `screenshot` (PNG; the MCP server returns the image) |

Every tool answers with

```json
{"ok": true, "result": {"feature": "BLOCK(1)", "body": "B1", "volume": 32000, "bbox": [0, 0, 0, 80, 40, 10]}}
```

or, on failure, with NX's own error message so the AI can correct itself:

```json
{"ok": false, "error": {"code": "NX_ERROR", "message": "Tool body completely outside target body.",
                        "nx_error": {"type": "NXOpen.NXException", "error_code": 670030, "message": "..."}}}
```

## Two modes

| | live (default) | batch |
|---|---|---|
| how | you play the receiver journal in NX | `NX_AI_BRIDGE_TRANSPORT=batch`; the bridge starts `run_journal.exe` itself |
| NX window | yes - you watch the AI work | none |
| speed | instant | one NX start per call (use `nx_batch` to run many steps at once) |
| screenshots | yes | no |
| output | your open part | `.prt` files on disk |

Two transports exist for live mode and are chosen automatically: a TCP socket on `127.0.0.1` and, as a fallback,
polling a request folder (`%USERPROFILE%\.nx-ai-bridge\ipc`). Details: [docs/protocol.md](docs/protocol.md).

## Self-check

Because the NX side was written blind, the project ships a self-check that replaces days of manual testing with
two clicks. If you have NX, running it is the most valuable contribution you can make:

```powershell
powershell -ExecutionPolicy Bypass -File selfcheck.ps1
```

Then play `%USERPROFILE%\.nx-ai-bridge\nx_receiver\NxAiBridgeSelfCheck.cs` in NX (Alt+F8) and answer one question.
You get a single file, `nx-ai-bridge-selfcheck-<time>.json` (a copy lands on your Desktop). Please attach it to a
[GitHub issue](https://github.com/qiuyekai/nx-ai-bridge/issues). It contains: NX version and install path, whether
`run_journal.exe` works under your licence, which transport works, whether NX stays responsive while the receiver
runs, the result of every tool with the original NX error, analytic volume checks of a reference bracket, and a
listing of NX Open member names (no geometry or personal files of yours; the report does include your Windows user
name inside file paths).

## Verification status

| component | status |
|---|---|
| bridge: MCP server, CLI, transports, mock | ✅ automated tests on Windows / macOS / Linux, Python 3.10 and 3.12 |
| receiver kernel: JSON, TCP + folder transport, run-time compiler, fallback logic, batch runner, self-check report | ✅ compiled with the .NET Framework C# 5 compiler and run end-to-end in CI - **without NX** |
| all tool handlers compile | ✅ against a hand-written stand-in for NX Open, which proves syntax only |
| journal loads and runs inside NX 10 | ❌ **not verified** |
| every modelling tool on real NX | ❌ **not verified** |
| NX stays interactive while the receiver runs | ❌ **not verified** |
| batch mode with `run_journal.exe` | ❌ **not verified** |

Findings from real machines are collected in [docs/nx10-notes.md](docs/nx10-notes.md).

## Security and limitations

- **`run_journal` executes arbitrary code** inside NX with your user's privileges - that is its purpose. Only connect
  AI tools you trust, and review what they are about to run when working on valuable data. To disable it, delete
  `%USERPROFILE%\.nx-ai-bridge\nx_receiver\handlers\run_journal.cs`.
- The receiver listens on **127.0.0.1 only** and never opens a port to the network. There is no authentication:
  any program running under your Windows account can send it requests while the receiver window is open.
- The receiver compiles its own source files (C#) at start-up with the compiler that is part of Windows and caches
  the result under `%USERPROFILE%\.nx-ai-bridge\cache` - the same mechanism NX uses to play journals. Anyone who can
  write to that folder can change what runs inside NX.
- One request at a time; long NX operations block the queue.
- AI-driven modelling makes mistakes. Work on copies, save often, and check results before relying on them.

## Not affiliated with Siemens

This is an independent open-source project. It is **not** a Siemens product and is not endorsed by Siemens.
"NX" and "Unigraphics" are trademarks of Siemens Industry Software Inc. You need **your own valid NX licence**; the
project contains no Siemens files (no DLLs, no documentation). It works through NX's own journal mechanism and the
NX Open API of your installation; whether and how you may automate NX is governed by your NX licence terms.

## Repository layout

```
AGENTS.md            instructions for AI agents (install, use, troubleshoot)
install.ps1          Windows setup, no admin rights
selfcheck.ps1        self-check part 1 (outside NX)
nx_receiver/         runs inside NX
  NxAiBridgeReceiver.cs    the journal you play (a small loader)
  NxAiBridgeSelfCheck.cs   the self-check journal
  kernel/                  JSON, transports, run-time compiler, dispatcher, control window (no NX Open calls)
  nxlib/                   shared NX Open helpers
  handlers/                one small C# file per tool group, compiled individually, hot-reloaded
  vb/                      VB.NET versions of the two journals (fallback)
src/nx_ai_bridge/    MCP server, CLI, transports, mock receiver
tests/               tests incl. a stand-in for NX Open used by CI
docs/                protocol, design notes, NX 10 findings
```

Design rationale: [docs/design.md](docs/design.md).

## Development

```bash
uv venv && uv pip install -e ".[dev]"
uv run pytest                      # no NX needed
python scripts/sync_loaders.py     # after editing the loader journals
```

## License

[MIT](LICENSE)
