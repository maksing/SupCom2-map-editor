# The mission panel harness: pictures, clicks and flows without the game

Status: first draft written from the harness as it stands and from the runs made with it (the canvas flows, the catalog probe and the pictures of every
scenario - 344 files in under two minutes - were run through the scripts of this folder on 2026-11-03; the full click run was done with the same driver
before it was moved here). Not yet reviewed by a second reader. Read [Verification](Verification.md) (the technique and the rules for reporting) and
[ScratchVerifyProject](ScratchVerifyProject.md) (the shared scratch project) first.

Code: `Assets/SC2/Tools~/MissionPanelHarness/` (the `~` hides it from Unity; it is copied into a scratch Unity project, never compiled in the repository's own project).

## 1. Why it exists

The mission editor's panel is IMGUI: its layout, hit testing, dropdowns and keyboard handling cannot be unit tested in the standalone suite, and the game never runs the editor.
This harness drives the **real panel code** in a Unity `EditorWindow`, sends it synthetic mouse and keyboard events, and records what happened. It answers three questions
that the `dotnet test` suite cannot:

1. **How does it look?** A picture of the panel in many states (every node's property page, the library at every scroll position, each tab, the search box, ...). A person - or an assistant that can read images - looks at them for clipped text, overlaps and things off screen.
2. **Does every control do something?** It clicks every button, switch and text field the panel draws and reports the ones that changed nothing.
3. **Do the interactions work?** *Flows*: scripted sequences with an expected result (a library button adds a node, Undo removes it, a wire let go over empty canvas opens the search and Return adds the node, Home brings the nodes back, a node held at the canvas edge scrolls the view).

A fourth tool belongs with them: a **catalog probe** that compiles the Lua of every node inside Unity's own runtime (Mono). It exists because the standalone tests run on .NET and once missed a failure that only
Mono showed (see [KopiLua change log](../../Assets/SC2/Runtime/KopiLua/CHANGES_FROM_UPSTREAM.md)).

## 2. What it is not

- **Not the game and not a real player.** Nothing here runs a mission. The generated Lua is only syntax-checked.
- **Not real input.** Events are constructed (`new Event { type = MouseDown, mousePosition = ... }`) and sent with `EditorWindow.SendEvent`. A real mouse can behave differently, especially for time-based things (the edge scroll was checked with sleeps between repaints, not with a held mouse).
- **Not the player build's input path.** The panel is patched to take the **player** code path (`#if UNITY_EDITOR` is switched off, the host's runtime dropdowns are used), but it still runs in the Editor. A *Build* run (section 4) compiles a real Windows player, but does not run it.
- **A patched copy.** The panel the harness draws is the repository's code with its GUI calls rewritten (section 5.2). A bug in that rewriting would not be in the real panel.
- **One window size and scale**: 1500 x 900 px at the project's default UI scale (the pictures show a scale factor of about 1.25). Other sizes and scales are not covered.

## 3. What you need

- Unity **6000.0.77f1** (the version in `ProjectSettings/ProjectVersion.txt`), installed at `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe`, or `UNITY_EXE` / `-Unity` pointing at it.
- A **scratch Unity project** of that version whose `Assets/SC2` the scripts keep in step with the repository. `C:\temp\SC2UnityVerify45` is the shared one ([ScratchVerifyProject](ScratchVerifyProject.md)); it was in use by other work when this page was written, so the runs here used a **copy** of it
  (`C:\temp\SC2MissionHarnessProject`: the same project without its logs). Making such a copy: `robocopy C:\temp\SC2UnityVerify45 <copy> /E /XD Logs Temp /XF build_*.txt *.log` (keep `ProjectSettings\ProjectVersion.txt`), then remove any scenario file from `Assets/VerifyBuildScript/Editor` that is not
  yours. **Recreating the project from an empty one was not tried**; `Packages/manifest.json` and `ProjectSettings` in the shared project are the record of what it needs, and the first open reimports for several minutes.
- **A desktop session with a GPU** for the picture and click modes: they open a real editor window (without `-nographics`) and read it back from a `RenderTexture`. `Catalog` and `Build` run with `-nographics`.
- The project **must not be open in another Unity**, and two batch runs on one project cannot overlap (a lock stops the second). Check with `Get-CimInstance Win32_Process -Filter "Name='Unity.exe'"` before a run.

## 4. How to run it

From the folder `Assets/SC2/Tools~/MissionPanelHarness` in PowerShell:

```powershell
.\Invoke-MissionHarness.ps1 -Mode Ux      -Project C:\temp\SC2MissionHarnessProject
.\Invoke-MissionHarness.ps1 -Mode Shots   -Project C:\temp\SC2MissionHarnessProject
.\Invoke-MissionHarness.ps1 -Mode Shots   -Project C:\temp\SC2MissionHarnessProject -ShotName quick_add
.\Invoke-MissionHarness.ps1 -Mode Catalog -Project C:\temp\SC2MissionHarnessProject
```

What the script does, in this order: **syncs** the repository's `Assets/SC2` code into the project and **patches** the mission panel for the harness (`Sync-MissionHarness.ps1`); starts Unity in batch mode with the right method and environment; waits; and in a `finally` block **restores the
project to the plain code** (`Sync-MissionHarness.ps1 -Plain`: no harness file left, the panel files overwritten by the repository's), also when the run fails or times out. Then it prints the result lines of the mode and the exit code. `-KeepPatched` skips the restore (to open the project and look; restore by hand afterwards).

| Mode | What it runs | Takes | Result |
|---|---|---|---|
| `Ux` | the canvas flows only (`MISSION_UX_ONLY`) | about 2 minutes | ten `[Click] FLOW OK/FAIL` lines, `TOTAL ... imguiErrors=0` |
| `Shots` | a picture of every scenario, no clicks (`MISSION_SHOTS_ONLY`); `-ShotName x` only that scenario | about 2 minutes for all 344 pictures | the pictures in `-Out`, `pictures: 344 in <dir>` |
| `Library` | clicks every node button of the library at every scroll position (`MISSION_ONLY_PREFIX=library_scroll`) | not timed through the script; the earlier run took about 15 minutes | `click_report.txt` |
| `Clicks` | everything: pictures, a click on every control of every scenario, then the flows | about 40 minutes (10 000+ clicks) | `click_report.txt`, the pictures, `[Click] FLOW`, `TOTAL`, `ERROR` |
| `Catalog` | compiles the Lua of every node 5 times (`MISSION_REPEAT`) in Unity's Mono | about 2 minutes | `[Catalog] nodes=177 ... notCompiling=0` |
| `Build` | a Windows **player** of the plain code (`SC2VerifyStandaloneBuild.Run`, which the scratch project must have) | about 5 minutes | `SC2VERIFY_RESULT=Succeeded`, `SC2VERIFY_ERRORS=0` |

Times are what was seen on the author's machine (`Shots` and `Ux` were timed through the script; `Clicks` took about 40 minutes in the earlier runs); they depend on the graphics card. Parameters: `-Project` (or `SC2_HARNESS_PROJECT`), `-Out` (or `MISSION_OUT`; default `%TEMP%\mission_harness`), `-Unity` (or `UNITY_EXE`), `-TimeoutMinutes` (default 60).
The Unity log of the run is `<Out>\unity_<mode>.log`. The exit code is 0 when no flow failed and no error was logged, else 1.

**Reading the result.**

- `[Click] FLOW OK <text>` / `FLOW FAIL <text>`: one scripted interaction and its expected effect; the text shows the values compared.
- `[Click] TOTAL clicks=N unchanged=M imguiErrors=K`: `imguiErrors` counts exceptions and errors logged during the run (including a failed flow). It must be 0.
- In `click_report.txt` every control of every scenario is a line: `OK` (the click changed the document or the panel's state), `NOCHANGE` (it changed nothing), and `!!` lines for a control list that changed between two passes. **`NOCHANGE` is not a failure by itself**: disabled buttons (Undo with
  nothing to undo), labels and static toggles legitimately change nothing. Look for an **enabled button that never changes anything in any scenario**; the analysis used so far counts buttons that have an `OK` line in at least one scenario and lists the rest.
- `[Catalog]` lines: `does not compile: <type> (k of 5) <message>` and the failing Lua in `<Out>\catalog_fail_<type>.lua`.
- `error CS...` lines in any mode: the patched copy does not compile; look at the first one in the log.

## 5. How it works

### 5.1 The pieces

| File (in `Assets/SC2/Tools~/MissionPanelHarness`) | Role |
|---|---|
| `Invoke-MissionHarness.ps1` | the entry point: sync, run Unity, restore, print the result |
| `Sync-MissionHarness.ps1` | `robocopy`s `Assets/SC2/Scripts` and `Runtime` into the project; with no switch also patches the panel and adds the harness files; with `-Plain` removes them again. `-Purge` also deletes files the repository no longer has (`/MIR`) |
| `Editor/SC2MissionClickCheck.cs` | the driver: the window, the scenarios, the clicking, the flows (class `SC2MissionClickCheck`, method `Run`) |
| `Runtime/MissionProbe.cs` | stand-ins for `GUI.Button`, `GUILayout.Toggle`, `GUILayout.TextField`... that call the real function and **record where the control was drawn**, and the dropdown stand-in `MissionProbe.Popup` |
| `Editor/SC2MissionCatalogProbe.cs` | the catalog compile probe |

The scratch project must also have `SC2VerifyStandaloneBuild` (the `Build` mode) and an `Assets/VerifyBuildScript/Editor` folder; the scripts create the folder if it is missing.

### 5.2 The patch of the panel

`Sync-MissionHarness.ps1` rewrites, in each `SC2MissionPanel*.cs` of the **project's copy** only: `GUILayout.Button(` to `MissionProbe.LButton(`, `GUI.Button(` to `MissionProbe.Button(`, the same for `Toggle`, `GUILayout.TextField(`, `GUILayout.TextArea(`, `EditorGUILayout.Popup(`
to `MissionProbe.Popup(`; and `#if UNITY_EDITOR` to `#if SC2_NEVER`, so that the **player path** of the code runs (the host's runtime dropdown list instead of Unity's editor popup). It generates `HarnessDropdown.cs` from `SC2RuntimePopups.cs` (the host's dropdown with the screen size fixed to the window) and
copies `MissionProbe.cs`. If one of these steps stops matching the code (the script says so), the harness has to follow the panel.

### 5.3 The window and the events

`SC2MissionClickCheck.ClickWin` is an `EditorWindow` of 1500 x 900 that draws the panel the way the host does (`DrawLikeHost`): the panel first, then the dropdown lists, and while a list is open the panel sees an `Ignore` event. A picture is taken by drawing the window into a `RenderTexture` and reading it back - **never a screenshot of the screen**.

- A mouse position in an event is the GUI position **plus 26 px** (the editor's tab strip); `ToEvent` converts a screen position recorded by the probe.
- `MissionProbe` records, on each Repaint, the screen rectangle of every control the panel draws. The driver finds a control by its label (`ClickLabel("Button", "Undo")`) or iterates all of them.
- The canvas is not made of controls (nodes and ports are drawn with `FillRect`), so the canvas flows compute positions from the panel's private layout (reflection on `LayoutAll`, `StartPort`, `_pan`, `_zoom`, `_canvasSize`) and send events at those points.

### 5.4 The scenarios

The driver builds one mission (`SeedMission`): the sample **Hold the line**, a stage **All nodes** with one node of every catalog type and typical parameters, and the stage **Waves** (the loop sample). A scenario sets the panel's private state by reflection (`Set("_tab", "stage1")`) and the panel is redrawn. The pictures are named after the scenario:

| Scenario | Shows |
|---|---|
| `intro`, `stage1`, `waves`, `waves_loop`, `quick_add`, `stage1_edge` | a stage, a stage with a selected loop node, the node search open, a selected wire |
| `stage2_validation`, `stage2_log`, `stage2_bottomclosed`, `validation_scroll`, `stage_props_confirm` | the bottom tabs, the closed bottom area, the stage delete confirmation |
| `bases`, `variables`, `settings`, `bases_scroll_<y>`, `settings_scroll` | the forms, scrolled |
| `library_scroll_<y>` | the library at scroll position y (250 ... 10000) |
| `node_<type>`, `node_<type>_scrolled` | the property page of every node type, and scrolled to the end when it has many parameters |
| `dropdown_open` | a runtime dropdown list open over the panel |

**Clicking** (`Clicks`, `Library`): for every control of a scenario the driver rebuilds the panel, applies the scenario, clicks the control and compares a **fingerprint** (the document's serialized hash, the panel's selection, tab, scroll, pan, undo counters, copy buffer) before and after. A click that moves the fingerprint is `OK`. The library buttons are clicked only in `library_scroll_*` and `stage1`,
where they are on screen.

**Flows** (`RunFlows`, `RunUxFlows`): scripted interactions with explicit expectations. `RunFlows`: library button adds a node, Undo, Redo, the header switch, `+ Stage`, a stage tab, `Test from this stage`, `Delete stage` (two clicks), `Home (fit all)`, zoom `+` and `-`, a validation line that jumps to its node, typed text reaching the document
when the field loses focus. `RunUxFlows`: a wire let go over empty canvas opens the search, typing fills it, Return adds the node wired from Start, a double click opens the search, Escape closes only the search, Home returns the nodes into view, F centres the selected node, a node held at the edge scrolls the view and goes along.

### 5.5 Traps found while writing it

- **`Pump()` ends a drag.** The helper that redraws the panel sends a right-button click at the window's corner first; a mouse-up while a drag is in progress ends it. Compute positions once (`_origin = CanvasOriginScreen()` after the panel is set up), then send the down, drag and up events without a `Pump` between them. A flow that dragged "successfully" because the `Pump` had
  dropped the wire at the corner passed for the wrong reason until this was noticed.
- A flow that expects a zoom change cannot compare with the fitted zoom: when the graph is wide the fitted zoom already is the smallest allowed.
- **Keep the project plain between runs.** A patched panel in the shared project made other scenarios there fail to compile (they do not know `MissionProbe`). This is why the script always restores.
- A new `EditorWindow` per run, `win.position` set before the first event; the panel needs two repaints after being opened before the control list is complete (`Pump(); Pump();`).

## 6. Looking at the pictures

The pictures are PNG files in `-Out` (default `%TEMP%\mission_harness`). Open them in any image viewer; an assistant that can read images can be given the paths. What to look for, as it was done so far:

- text **cut at a box edge** (a long node title, a parameter label, a library button, a dropdown item) or running under another control;
- a control **off screen** or covered (the quick-add list, the properties scrollbar, the bottom tabs);
- a node whose title or port label overlaps its neighbour; wires that end at the wrong port;
- the same scenario **before and after** a change: keep the folder of the previous run (`-Out <dir>`) and compare the files of the same name;
- the state matches the scenario (the right tab is selected, the right node is highlighted).

A clean picture proves the panel drew what the harness asked for at 1500 x 900; it does not prove that a build at another size or scale looks the same.

## 7. Changing it

- **A new picture:** add `Add("<name>", () => { Set("_tab", "..."); ... });` in `Scenarios()` of `SC2MissionClickCheck.cs`; for a private method use `PanelMethod("<name>").Invoke(panel, args)` (the search box scenario does).
- **A new flow:** add a `FlowCheck("<what must hold>", <condition>)` in `RunFlows` or `RunUxFlows`; build the situation with `NewPanel(); Set("_tab", ...); Pump(); Pump();`, send events with `MouseAt`, `Key`, `TypeText`, read the result with `Get<T>("_field")` or `DocStage("<id>")`.
- **The panel changed so that the patch no longer fits:** `Sync-MissionHarness.ps1` throws when `SC2RuntimePopups.cs` changes shape; a new GUI call type needs a stand-in in `MissionProbe.cs` and a rewrite line in the script.
- **Outputs** go to `MISSION_OUT`; do not write elsewhere. Do not put anything of the harness under `Assets/SC2` (only `Tools~`).
- Give the report of a run the **commit and the uncommitted files** the project's copy held (the sync copies files other work has changed but not committed).

## 8. Rules

- Use a project that nobody else is running; never run two on one project. Prefer your own copy when the shared project is busy.
- Leave the project plain: let the script restore it; if a run is killed, run `Sync-MissionHarness.ps1 -Project <path> -Plain`.
- Never take a screenshot of the desktop; pictures come from a `RenderTexture` ([Verification](Verification.md), section 3).
- A green run is a statement about the **panel code of the commit that was copied**, at one window size; say so in the report, and say what was not run.

## 9. Known limitations and unverified behaviour

- The scripts were run end to end for `Ux`, `Catalog` and `Shots` (one scenario, then all 344) on 2026-11-03, and each left the project plain afterwards. `Library` and `Clicks` were done earlier with the same driver before it was moved into this folder (`Clicks`: 10 620 clicks, 0 errors, all flows passing, on the code of that day); they were not repeated through `Invoke-MissionHarness.ps1`. `Build` was not run through it.
- Recreating the scratch project from nothing, running on another machine, another Unity version or another window size were not tried.
- The edge scroll and every time-based behaviour are checked with synthetic events and sleeps, not with a real held mouse.
- The harness checks that controls **do something**, not that they do the **right** thing; the flows cover the rules that were thought of, not every panel behaviour.
- The scratch project's copy of `Assets/SC2` carries whatever uncommitted work the repository held when the sync ran, so a failure can come from code that is not yours.

## 10. Related documents

[Verification](Verification.md), [ScratchVerifyProject](ScratchVerifyProject.md), [Testing](Testing.md), [MissionEditor](../Systems/MissionEditor.md) (what the panel is), [EditorVsBuild](../Systems/EditorVsBuild.md) (why the player path differs).
