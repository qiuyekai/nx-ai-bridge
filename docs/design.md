# Design notes

## Constraints that shaped the design

1. **No NX on the development machine.** Every line that touches NX Open is a guess until someone runs it.
2. **Very little time on a real NX machine**, probably a shared PC without administrator rights.
3. **Journals only**: no NX Open author licence, so no signed DLL - NX must run plain source code.
4. Target NX 10 (2014, .NET Framework 4.x, C# 5), but newer versions should not need a rewrite.

The goal is therefore: *maximum information per minute of real-machine time*, and *failures that never cascade*.

## Why the journal is only a loader

NX compiles a journal as one unit. In a single 3000-line journal one wrong member name anywhere would be a compile
error that prevents everything from running - including the self-check that is supposed to find such errors.

So the journal the user plays (`NxAiBridgeReceiver.cs`, about 100 lines, using only certain APIs) does one thing:
it compiles the **kernel** with CodeDom - the same compiler mechanism NX itself uses for journals - and starts it.
The kernel has **no NX Open reference at all**, which is why CI can compile and run it for real on a Windows
runner. The kernel then compiles **each handler file separately**:

| a mistake in ... | consequence |
|---|---|
| one handler | that tool reports `COMPILE_ERROR` with file/line/message; all other tools work |
| a late-bound call (`Dyn`) | that call reports `API_MISMATCH` listing the members that really exist |
| `nxlib` (shared helpers) | all NX tools report the same `COMPILE_ERROR`; `nx_api_lookup` and `run_journal` still work |
| the kernel | caught by CI before release |
| the loader | caught by CI; a VB loader exists in case NX cannot play C# journals |

Side effects worth having: handlers are hot-reloaded when edited (an AI agent on the NX machine can repair a
handler and retry within seconds), and several implementations ("variants") of one tool can coexist - if variant 1
fails, the model is rolled back and variant 2 runs. Primitives, `new_part` and `screenshot` ship with two variants.

## Why late binding (`Dyn`)

Recorded NX journals are the only reliable API reference available from memory, and member names differ between NX
versions. `Dyn.Call(builder, "AddChainset", collector, "5")` resolves the member by reflection at run time:

- a wrong guess costs one tool call instead of a compile failure, and the error contains the real signatures;
- strings convert to enums (`Dyn.Set(b, "Type", "DiametersAndHeight")`), arrays are re-typed, `out` values are
  written back - so handler code stays close to what a recorded journal looks like;
- the same handler can serve NX 10 and newer releases (`SetAny`, `TrySet`, try/fallback).

Strong typing is used only for members that appear in virtually every journal (`Session.GetSession()`,
`workPart.Bodies`, `SetUndoMark`, `Curves.CreateLine`, ...).

## Staying interactive inside NX

NX Open must be called on NX's UI thread. A journal that loops forever freezes NX. The receiver instead creates a
small modeless WinForms window with a 50 ms timer, returns from `Main`, and stays loaded
(`GetUnloadOption` = at termination). NX's own message loop then fires the timer; each tick polls the socket and
the request folder without blocking and executes at most what is pending. If NX does not pump the timer (to be
verified!), `"ui_mode": "modal"` runs a modal window with its own message loop, and `"blocking"` is a plain loop.

## What the self-check answers

One run (`selfcheck.ps1`, then one journal) produces one JSON file that answers every open question from the
project plan: NX path/version; does `run_journal.exe` work with the available licence; can a journal use
`System.Net.Sockets`; which transport works; does NX stay responsive (timer ticks after `Main` returned, plus the
user's answer); per-tool result with the raw NX error and which variant ran; analytic volume checks (primitives,
blend, chamfer, holes, shell, booleans, extrude, revolve, and the reference bracket); and a dump of the real
member names of ~60 NX Open types, so that wrong guesses can be corrected offline in one pass.

## Geometry conventions

- Bodies get an NX object name on first use (`B1`, `B2`, ...). The name is stored in the part, so references
  survive save/open and batch sessions. Feature names (`BLOCK(1)`) are accepted wherever a body is expected.
- Edge / face ids (`E:B1:3`) are positions in `Body.GetEdges()` / `GetFaces()` and are only valid until the body
  changes; `{"near": [x,y,z]}` and `{"normal": [x,y,z]}` selectors are the robust way to pick geometry.
- A sketch is a named set of curves (`<SKETCH>_C<n>`) plus a frame stored as a part attribute. The receiver also
  tries to create a real NX sketch feature and to add the curves to it; when that fails, modelling continues with
  plain curves ("curves" mode). Extrude/revolve collect curves by name, so both modes behave the same.
- `create_hole` is a subtracted cylinder: exact, predictable geometry instead of the large, version-dependent
  hole-package API.

## Licensing consideration

The receiver uses only what a hand-written journal could use: source code compiled on the fly by the .NET compiler
that ships with Windows, calling the NX Open API of the user's installation. It ships no Siemens files. It does
cache the compiled kernel/handlers as DLLs in the user's profile and loads them from the journal; users for whom
that matters should check their NX licence terms.
