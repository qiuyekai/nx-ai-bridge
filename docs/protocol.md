# Protocol between the bridge and the receiver

The same JSON messages travel over every transport. Protocol version: **1**.

## Messages

```json
{"id": "9f0c...", "tool": "create_block", "params": {"length": 80, "width": 40, "height": 10}, "protocol": 1}
```

```json
{"id": "9f0c...", "ok": true,
 "result": {"feature": "BLOCK(1)", "type": "BLOCK", "bodies": ["B1"], "body": "B1", "volume": 32000, "bbox": [0, 0, 0, 80, 40, 10]},
 "meta": {"receiver": "nx", "mode": "live", "variant": "primitives.cs", "elapsed_ms": 41.3}}
```

```json
{"id": "9f0c...", "ok": false,
 "error": {"code": "NX_ERROR", "message": "...", "nx_error": {"type": "NXOpen.NXException", "message": "...", "error_code": 670030},
           "hint": "...", "details": {}, "stack": "..."},
 "meta": {"variant": "boolean.cs", "elapsed_ms": 12.0}}
```

- `params` have already been validated and completed with defaults by the bridge (`src/nx_ai_bridge/tools.py` is the
  single source of truth for tool names and parameters).
- Lengths are millimetres, angles degrees, points/vectors `[x, y, z]`.
- `meta.variant` is the handler file that produced the answer. `meta.attempts` lists failed earlier variants.

### Error codes

| code | produced by | meaning |
|---|---|---|
| `INVALID_PARAMS` | bridge or receiver | the request is wrong |
| `NOT_FOUND` | receiver | body / feature / expression / face / edge / file does not exist |
| `NO_WORK_PART` | receiver | NX has no work part |
| `NOTHING_TO_UNDO` | receiver | |
| `NX_ERROR` | receiver | an `NXOpen.NXException`; `nx_error` carries NX's message and error code |
| `UPDATE_FAILED` | receiver | model update failed after an edit; rolled back |
| `EXPORT_FAILED`, `NOT_AVAILABLE` | receiver | export produced no file / tool not available in this mode |
| `API_MISMATCH` | receiver | a late-bound NX Open member does not exist in this NX version; `details.candidates` lists what exists |
| `COMPILE_ERROR` | receiver | a handler (or `run_journal` code) does not compile; `details.errors` |
| `INTERNAL_ERROR`, `UNKNOWN_TOOL`, `BAD_REQUEST` | receiver | bug / unknown tool / unparsable request |
| `RECEIVER_NOT_RUNNING`, `TIMEOUT`, `TRANSPORT` | bridge | cannot reach the receiver |
| `NX_NOT_FOUND`, `NOT_INSTALLED`, `BATCH_FAILED` | bridge | batch mode problems |
| `BATCH_STEP_FAILED` | bridge | a step of `nx_batch` failed (see `result.results`) |

## Transports

Home folder: `%NX_AI_BRIDGE_HOME%` or `%USERPROFILE%\.nx-ai-bridge` (bridge and receiver use the same rule).

### Heartbeat

A live receiver rewrites `<home>/receiver.json` once per second:

```json
{"pid": 4242, "mode": "live", "ui_mode": "modeless", "tcp_port": 47110, "tcp_error": null,
 "heartbeat": 1794567890.12, "nx_version": "...", "receiver_version": "0.1.0a1", "protocol": 1}
```

With `NX_AI_BRIDGE_TRANSPORT=auto` (default) the bridge uses TCP on `tcp_port` when the heartbeat is younger than
20 s and `tcp_port > 0`, the folder transport when `tcp_port` is 0, and otherwise tries TCP on the configured port.

### TCP (preferred)

- The receiver listens on **127.0.0.1** only, default port **47110** (`port` in `config.json`,
  `NX_AI_BRIDGE_PORT`). If the port is taken it binds a free one and publishes it in the heartbeat.
- One request per connection: the client sends the request as one UTF-8 line terminated by `\n`, the receiver
  answers with one line and closes.
- The receiver never blocks on the socket: it polls `Pending()` / `Available` from a 50 ms WinForms timer on NX's
  UI thread - the only thread that may call NX Open.

### Folder polling (fallback)

- Request: the bridge writes `<home>/ipc/requests/<id>.json.tmp`, then renames it to `<id>.json`.
- Response: the receiver deletes the request and writes `<home>/ipc/responses/<id>.json` the same atomic way; the
  bridge deletes it after reading.
- Used automatically when sockets are unavailable inside NX, or forced with `NX_AI_BRIDGE_TRANSPORT=file`.

### Batch (`run_journal.exe`)

```
run_journal.exe <home>\nx_receiver\NxAiBridgeReceiver.cs -args --batch <job dir>
```

`<job dir>/requests.json` is `{"stop_on_error": true, "requests": [request, ...]}`; the receiver writes
`<job dir>/responses.json` (an array of responses) and exits. Each run is a fresh NX session, so the bridge keeps
the work part path in `<home>/batch_state.json` and wraps single calls as `open_part -> tool -> save_part`.

## Receiver command line

Arguments reach the journal through `run_journal.exe ... -args`; when played with Alt+F8 there are none.

| argument | effect |
|---|---|
| *(none)* / `--live` | control window + timer; `Main` returns immediately |
| `--modal` | same with a modal window (also `"ui_mode": "modal"` in `config.json`) |
| `--console` | blocking loop, no window (`"ui_mode": "blocking"`); ends on a `__shutdown` request or when `<home>/stop` exists |
| `--batch <dir>` | see above |
| `--selfcheck [--no-ui]` | run the self-check |
| `--compile-check` | compile nxlib and all handlers, print a JSON report |
| `--home <dir>`, `--handlers <dir>`, `--port <n>`, `--no-tcp`, `--timeout <s>` | overrides (tests) |

## Handler contract (C#)

```csharp
// tools: my_tool, my_other_tool      <- required header: which tools this file serves
// variant: 1                         <- optional: lower numbers are tried first
using NXOpen;
namespace NxAiBridge.Handlers
{
    public class MyTool : ITool
    {
        public object Run(string tool, Args a)
        {
            Part part = Nx.Work();                        // throws NO_WORK_PART
            double size = a.Num("size");                  // throws INVALID_PARAMS
            object builder = Dyn.Call(part.Features, "CreateSomethingBuilder", new object[] { null });
            ...
            return R.Obj("feature", "...");               // becomes "result"
        }
    }
}
```

The dispatcher sets an undo mark before `Run` and rolls back when it throws. Files are compiled one by one with
the C# 5 compiler of .NET Framework 4.x and recompiled when they change.
