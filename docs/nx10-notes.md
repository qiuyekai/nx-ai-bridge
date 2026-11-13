# NX 10 notes - what has been verified on real machines

**Nothing yet.** This file is the logbook for real-machine findings. Each self-check report that comes back gets
an entry under [Reports](#reports), and the list of open assumptions below shrinks.

## How to process a self-check report

1. `summary` - passed / failed / volume mismatches.
2. `compile` - any unit that is not `"ok"` is a compile error against the real NX Open: fix those first
   (file, line and message are in `details.errors`).
3. `transport` and `ui_probe` - decide the default transport and `ui_mode`.
4. `steps[]` with `"ok": false` - look at `error.code`:
   - `API_MISMATCH`: pick the right member from `error.details.candidates` or from `api_dump`.
   - `NX_ERROR`: the call exists but NX rejected it - wrong arguments or wrong call order.
5. `steps[]` with `"volume_ok": false` - the tool "worked" but built the wrong geometry.
6. `meta.attempts` on successful steps - variant 1 failed and the fallback ran; fix or drop variant 1.
7. `env_report.batch` and `batch_report` - does `run_journal.exe` work, and how do the tools behave without UI.

## Open assumptions (all UNVERIFIED)

### Loading and running

| # | assumption | where | how the self-check tests it |
|---|---|---|---|
| L1 | NX 10 can play a C# journal (`.cs`) | `NxAiBridgeReceiver.cs` | the journal starts at all; otherwise use `vb/*.vb` |
| L2 | journal code may use `System.CodeDom.Compiler` and load the DLL it produced | loader | `compile` section exists in the report |
| L3 | NX Open assemblies have a file `Location` usable as compiler reference | `Compiler.cs` | `environment.nxopen_assemblies` is non-empty |
| L4 | `GetUnloadOption` = `AtTermination` keeps statics/window alive after `Main` returns | loader | `ui_probe.modeless_timer_ticks` > 0 |
| L5 | NX's message loop dispatches a WinForms timer created by a journal | `LiveUi.cs` | `ui_probe.modeless_ok`, `user_could_rotate_model` |
| L6 | `System.Net.Sockets` works inside a journal | `Host.cs` | `transport.sockets`, `transport.tcp_roundtrip` |
| L7 | `run_journal.exe <file> -args ...` passes arguments to `Main(string[] args)` and runs under the available licence | batch mode | `env_report.batch`, `batch_report` |
| L8 | NX Open calls from a timer tick (outside journal replay) are allowed and refresh the display | `LiveUi.cs` | tools work in live mode; watch the screen |

### NX Open API guesses

| # | assumption | where |
|---|---|---|
| A1 | `PartCollection.NewDisplay(path, units)` accepts the enum value named `Millimeters` | `part_new.cs` |
| A2 | `OpenDisplay(path, out status)`; `Save(SaveComponents.True, CloseAfterSave.False)`; `Close(CloseWholeTree.True, CloseModified.CloseModified, null)` | `part_files.cs` |
| A3 | builders: `CreateBlockFeatureBuilder` / `CreateCylinderBuilder` / `CreateConeBuilder` / `CreateSphereBuilder`, their `Type` enum names, `BooleanOption.Type` + `SetTargetBodies`, `Axis`, `CenterPoint`, `*.RightHandSide` | `primitives.cs` |
| A4 | `UF_MODL_create_block1 / cyl1 / cone1 / sphere1` wrappers `CreateBlock1(sign, corner, edges, out tag)` ... and `FeatureSigns.Nullsign` | `primitives.uf.cs` |
| A5 | `Feature.GetBodies()` returns the resulting body, also after a boolean | `NxLib.cs` |
| A6 | `UFModl.AskMassProps3d(tags, n, 1, 3, 1.0, 1, acc[11], props[47], stats[13])`, units 3 = g/cm, `props[1]` = volume, `props[0]` = area, `props[3..5]` = centroid | `NxLib.cs` |
| A7 | `UFModl.AskFaceData(tag, out type, point, dir, box, out radius, out radData, out normDir)`; `AskCurveProps(tag, 0.5, point, ...)` | `NxLib.cs` |
| A8 | `CreateBooleanBuilderUsingCollector(null)`, `Operation`, `Targets.Add`, `ToolBodyCollector`, `ScRuleFactory.CreateRuleBodyDumb(bodies[, true])` | `NxLib.cs` |
| A9 | `UFSket.InitializeSketch(ref name, out tag)`, `CreateSketch(name, 2, matrix9, Tag[2], int[2], 1, out tag)` with matrix = x axis, y axis, origin; `AddObjects(tag, n, tags)`; `UpdateSketch`; `TerminateSketch` | `sketch.cs` |
| A10 | `Sections.CreateSection(0.0095, 0.01, 0.5)`, `ScRuleFactory.CreateRuleCurveDumb(curves)`, `Section.AddToSection(rules, seed, null, null, helpPoint, Section.Mode.Create, false)` | `extrude.cs` |
| A11 | `ExtrudeBuilder`: `Section`, `Direction`, `Limits.StartExtend/EndExtend.Value.RightHandSide`, `BooleanOperation`; `RevolveBuilder`: `Section`, `Axis`, `Limits`, `Tolerance` | `extrude.cs` |
| A12 | `EdgeBlendBuilder.AddChainset(collector, radiusString)`; `ChamferBuilder.Option = SymmetricOffsets`, `FirstOffsetExp` or `FirstOffset`, `SmartCollector` | `edge_ops.cs` |
| A13 | `ShellBuilder.DefaultThickness`, `RemovedFacesCollector` | `shell.cs` |
| A14 | `UpdateManager.AddToDeleteList(obj)`, `DoUpdate(mark)`, `ErrorList`; `ExpressionCollection.Edit(expr, rhs)` | `manage.cs` |
| A15 | `DexManager.CreateStepCreator()`: `ExportAs = Ap214`, `ExportFrom = DisplayPart`, `SettingsFile`, `ObjectTypes.Solids`, `OutputFile`, `FileSaveFlag`, `LayerMask`, `ProcessHoldFlag` | `export_step.cs` |
| A16 | `UFStd.OpenBinaryStlFile(file, append, header, out handle)`, `PutSolidInStlFile(handle, csys, body, min, max, tol, out nErr, out errs)`, `CloseStlFile(handle)` | `export_stl.cs` |
| A17 | `UFDisp.CreateImage(file, ImageFormat.Png, BackgroundColor.White)`; `ModelingViews.WorkView.Orient(View.Canned.X, View.ScaleAdjustment.Fit)`, `.Fit()` | `screenshot.cs` |
| A18 | `SetUserAttribute(title, -1, value, Update.Option.Now)` / `GetStringAttribute(title)` on a part | `NxLib.cs` |
| A19 | body object names set with `SetName` survive booleans on the target body and save/open | `NxLib.cs` |

## Reports

*(none yet)*

<!-- Template:
### YYYY-MM-DD - NX 10.0.x.y, Windows 10, <who>
- report file: ...
- loading: L1 ok / L2 ok / ...
- failed steps: ...
- fixes made: commit ...
-->
