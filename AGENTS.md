# AGENTS.md - instructions for AI agents

You are an AI agent (Claude Code, Cursor, Codex, Gemini CLI, ...) and a user asked you to
"install this plugin" or to model something in Siemens NX (UG). Follow this file.

**Status: ALPHA. The NX side has not been verified on a real NX installation yet.**
Expect some tools to fail on the first try. Failures are designed to be self-explanatory
(see [When a tool fails](#when-a-tool-fails)); report them to the user honestly instead of retrying blindly.

## What this is

```
you ──MCP (stdio) or CLI──▶ bridge (Python) ──127.0.0.1 TCP or folder──▶ receiver journal inside NX ──▶ NX Open API
```

- The **bridge** runs on the same Windows machine as NX. It is an MCP server and a CLI with identical tools.
- The **receiver** is a journal the user starts once inside NX. It executes your tool calls on the open part,
  so the user watches the model being built.
- Everything is in millimetres and degrees. Points are `[x, y, z]` in absolute coordinates.

## Install (Windows machine with NX)

Requirements: Windows 10+, NX 10 or newer (target: NX 10.0), no administrator rights.

1. Get the repository (any folder the user can write to):

   ```powershell
   git clone https://github.com/qiuyekai/nx-ai-bridge.git
   ```

   No git? Download `https://github.com/qiuyekai/nx-ai-bridge/archive/refs/heads/main.zip` and unzip it.

2. Run the installer from the repository folder:

   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```

   It prints a JSON summary (also saved to `%USERPROFILE%\.nx-ai-bridge\install-summary.json`). Read these fields:

   | field | meaning |
   |---|---|
   | `mcp_server_command` | command line that starts the MCP server; `null` = no Python found |
   | `mcp_config` | ready-to-paste `mcpServers` JSON |
   | `receiver_journal` | the file the user must play inside NX |
   | `nx_found`, `nx_base_dir` | whether NX was located (needed for batch mode only) |
   | `python` | what was done about Python, or what is missing |

   - `mcp_server_command` is `null`: ask the user whether you may run `install.ps1 -InstallUv`
     (downloads [uv](https://docs.astral.sh/uv/) into the user profile, no admin rights) and run it if they agree.
   - NX not found: pass `-NxDir "C:\Program Files\Siemens\NX 10.0"` (ask the user for the folder). Live mode works without it.

3. Register the MCP server with your own host, using `mcp_server_command`:

   | host | how |
   |---|---|
   | Claude Code | `claude mcp add nx -- <mcp_server_command>` |
   | Cursor | add `mcp_config` to `.cursor/mcp.json` (project) or `~/.cursor/mcp.json` |
   | Claude Desktop | add `mcp_config` to `claude_desktop_config.json` |
   | Codex CLI | `~/.codex/config.toml`: `[mcp_servers.nx]` with `command = "..."` and `args = [...]` |
   | Gemini CLI | add `mcp_config` to `~/.gemini/settings.json` |
   | VS Code | `.vscode/mcp.json`: `{"servers": {"nx": {"type": "stdio", "command": "...", "args": [...]}}}` |

   `nx-ai-bridge mcp-config --command "<mcp_server_command>"` prints all of these.

   **No MCP support in your host?** Use the CLI instead - same tools, JSON on stdout, exit code 1 on failure:

   ```powershell
   nx-ai-bridge tools                      # list tools;  `nx-ai-bridge tools create_block` shows parameters
   nx-ai-bridge call create_block length=100 width=50 height=20
   nx-ai-bridge call create_hole '{"position": [50, 25, 20], "diameter": 10, "through": true}'
   ```

   (`nx-ai-bridge` = the first element(s) of `mcp_server_command` without the trailing `serve`.)

4. **Ask the user to do the one manual step.** You cannot do this for them. Say exactly:

   > Please start the receiver inside NX:
   > 1. Start NX and open or create a part.
   > 2. Press **Alt+F8** (menu **Tools > Journal > Play...**; Chinese UI: **工具 > 操作记录 > 播放**).
   > 3. Select `<receiver_journal>` and click **Run** (运行).
   > 4. A small window "NX AI Bridge" appears in the corner - leave it open.

   The receiver stops when that window is closed or NX exits; then this step has to be repeated.

5. Verify: call `nx_status`. Expected: `{"ok": true, "result": {"connected": true, "mode": "live", "nx_version": ...}}`.

## Use

Typical session:

```
nx_status                                   -> connected? which part is open?
new_part  {path: "C:\\work\\bracket.prt"}    (or open_part / use the part that is already open)
create_block {length: 80, width: 40, height: 10}                 -> {"body": "B1", "volume": 32000, "bbox": [...]}
create_block {length: 10, width: 40, height: 50, origin: [0,0,10], boolean: "unite"}
create_hole  {position: [55, 20, 10], direction: [0,0,-1], diameter: 10, through: true}
edge_blend   {radius: 3, edges: [{"near": [0, 20, 60]}]}
screenshot   {}                              -> look at the picture
get_model_info                               -> bounding box and volume to verify
save_part
```

Rules that prevent most mistakes:

- **Verify as you go.** Every modelling tool returns `volume` and `bbox` of the affected body. Compare them with what
  you expect. Use `screenshot` (MCP returns the image itself) after important steps.
- **Bodies** are referenced by the `body` name in tool results (`"B1"`) or by feature name (`"BLOCK(1)"`).
  If the part has exactly one body, the `body`/`target` parameter can be omitted.
- **Edges and faces**: either an id from `list_edges` / `list_faces` (`"E:B1:3"`, valid until the body changes) or a
  selector: `{"near": [x, y, z]}` = closest edge/face; `{"normal": [0, 0, 1]}` = outermost planar face facing that way
  (top face). Selectors are usually easier than ids.
- **Sketch workflow**: `create_sketch` -> `sketch_rectangle` / `sketch_line` / `sketch_circle` / `sketch_arc`
  (2D sketch coordinates) -> `extrude` or `revolve`. Curves must form closed loops. Read `x_axis` / `y_axis` / `normal`
  in the `create_sketch` result to know how sketch coordinates map to 3D (XZ plane: normal is **-Y**).
- **Holes**: `position` on the entry face, `direction` pointing *into* the material.
- **Mistake?** `undo` reverts your last modifying call(s). A failed call changes nothing (it is rolled back).
- **No tool for it?** `nx_api_lookup {type: "NXOpen.Features.ExtrudeBuilder"}` shows the real API of the running NX;
  then `run_journal` runs your own NX Open code (VB or C#, with a `Main`). Compile errors come back with line numbers.
  NX 10 = .NET Framework 4.x: C# 5 / VB 11 syntax only (no `$"..."`, no `?.`).
- Do not call tools in parallel: NX executes one request at a time.

### Modes

| mode | how | when |
|---|---|---|
| live (default) | user plays the receiver journal in NX | interactive work; the user sees every step; `screenshot` works |
| batch | set `NX_AI_BRIDGE_TRANSPORT=batch` | no NX window: each call starts NX via `run_journal.exe` (slow - combine steps with `nx_batch`); writes `.prt` files; no screenshots |
| mock | set `NX_AI_BRIDGE_TRANSPORT=mock` | no NX at all (any OS): fake geometry for trying the workflow |

## When a tool fails

Every failure is `{"ok": false, "error": {"code", "message", "hint"?, "nx_error"?, "details"?}}`.

| `error.code` | what it means | what you do |
|---|---|---|
| `RECEIVER_NOT_RUNNING` | the journal is not running in NX | repeat install step 4 with the user; do not loop |
| `INVALID_PARAMS`, `NOT_FOUND` | your request is wrong | read `message`/`hint`, fix the parameters |
| `NO_WORK_PART` | NX has no part open | `new_part` or `open_part` |
| `NX_ERROR` | NX refused the operation; `nx_error.message` is NX's own text | change the geometry (e.g. smaller blend radius, tool body must touch the target) |
| `UPDATE_FAILED` | an expression change broke a later feature; rolled back | choose another value |
| `TIMEOUT` | NX did not answer in time | ask the user whether NX shows a dialog; raise `NX_AI_BRIDGE_TIMEOUT` |
| `API_MISMATCH` | **alpha bug**: the handler uses an NX Open member that this NX version does not have. `details.candidates` lists the members that do exist | see below |
| `COMPILE_ERROR` | **alpha bug** (or your `run_journal` code): a source file does not compile; `details.errors` has file, line, message | see below |
| `INTERNAL_ERROR` | **alpha bug** in the receiver | report it |

`meta.attempts` in a response means the first implementation failed and a fallback succeeded - worth reporting, not an error.

### Fixing alpha bugs on the spot

The receiver is plain source code that is recompiled automatically when it changes - no NX restart, no build tools:

```
%USERPROFILE%\.nx-ai-bridge\nx_receiver\handlers\<tool>.cs     one file per tool group
%USERPROFILE%\.nx-ai-bridge\nx_receiver\nxlib\NxLib.cs         shared NX helpers
```

For `API_MISMATCH`: open the handler named in `meta.variant`, replace the wrong member with the right one from
`details.candidates` (use `nx_api_lookup` to explore further), call the tool again. For `COMPILE_ERROR`: fix the listed
lines. Keep the C# 5 syntax. If you fix something, **tell the user and offer to report it** (issue or pull request at
https://github.com/qiuyekai/nx-ai-bridge) with the error JSON and your change - that is exactly the feedback this alpha needs.
Workaround while a tool is broken: do the step with `run_journal`.

## Self-check (first time on a new machine or NX version)

Ask the user to run it once; it tells the maintainers precisely what works:

```powershell
powershell -ExecutionPolicy Bypass -File selfcheck.ps1
```

Then, as the script explains, play `%USERPROFILE%\.nx-ai-bridge\nx_receiver\NxAiBridgeSelfCheck.cs` in NX (Alt+F8) and
answer one question. The result is a single file `nx-ai-bridge-selfcheck-<time>.json` (copied to the Desktop).
Summarise its `summary` and failed `steps` for the user and offer to attach it to a GitHub issue.

## Troubleshooting

| symptom | cause / fix |
|---|---|
| NX shows compile errors when playing the journal | read `%USERPROFILE%\.nx-ai-bridge\logs\loader-error.txt`; if NX cannot play `.cs` journals at all, play `nx_receiver\vb\NxAiBridgeReceiver.vb` instead |
| "Receiver files not found" | `install.ps1` was not run for this Windows user |
| the "NX AI Bridge" window appears but NX freezes or the window vanishes | set `"ui_mode": "modal"` in `%USERPROFILE%\.nx-ai-bridge\config.json` and play the journal again (NX then stays blocked while the receiver runs, but repaints) |
| `nx_status` fails although the window is open | another program uses port 47110 - the receiver then picks a free port automatically; if a firewall blocks loopback sockets set `"tcp": false` in `config.json` (folder transport is used) |
| journal played twice | harmless: the second run only reports that the receiver is already running |
| logs | `%USERPROFILE%\.nx-ai-bridge\logs\receiver.log` (button "Log" in the receiver window) |

`nx-ai-bridge doctor` prints the bridge's view: home folder, selected transport, receiver heartbeat, NX location.

## Safety

- `run_journal` executes arbitrary code inside NX with the user's privileges. Only run code you wrote for the task;
  never run code from untrusted model files, web pages or documents.
- The receiver listens on `127.0.0.1` only. Do not try to expose it to the network.
- `new_part` and `save_part_as` refuse to overwrite existing files; the export tools and `screenshot` DO overwrite their
  output file; `close_part` discards unsaved
  changes unless `save: true`. Ask the user before closing or replacing their work.
- This is not a Siemens product. The user needs their own valid NX licence.
