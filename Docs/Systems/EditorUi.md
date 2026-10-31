# Editor UI framework (host, tabs, skin, theme, hotkeys, overlays)

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) and [Editor vs build](EditorVsBuild.md) first.

## 1. Purpose and boundaries

The whole editor is one IMGUI "game view": one host object draws the menu bar, the main tabs and sub-tabs, the viewport input, the right-hand panel of the active tab and every overlay. Each tab has a **shell** that owns that tab's panel and viewport behaviour.
This document covers the host, the tab structure, the shell pattern, the global undo dispatch, the skin, theme, UI scale and type roles, the hotkey system (with alternative bindings), the start screen, the busy overlay, the runtime stand-ins for editor widgets and the per-user
preferences that are not part of a map.

Not included: what each tab does (its own system document), the editor-API stubs (`UnityEditorRuntimeShim`, see [editor vs build](EditorVsBuild.md)), saving ([document and persistence](DocumentAndPersistence.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Host | `Scripts/Unified/SC2GameViewEditorUI.cs` (the `partial` class: OnGUI order, menus, viewport input, shell creation, global undo, imported terrains, visibility), `SC2GameViewEditorUI.Startup.cs` (the start screen), `SC2GameViewEditorUI.Decals.cs` |
| Tabs | `Scripts/Unified/SC2EditorTabs.cs` (`SC2EditorMainTab`, `SC2ViewportTool`, the sub-tab name arrays, `GetSubtabs`), `SC2EditorContext.cs` (current tab, tool, overlays, dirty flags) |
| Shells | `SC2TerrainShell`, `SC2CostsShell`, `SC2Collision2Shell`, `SC2ObjectsShell`, `SC2ReferenceUnitsShell`, `SC2DecalsShell`, `SC2EnvironmentShell`, `SC2ScenarioShell`, `SC2ExportShell`, each created lazily by the host with the terrain shell and the map session |
| Look | `SC2UnifiedSkin.cs` (the runtime `GUISkin`), `SC2Theme.cs` (palette, one muted blue accent, presets, per-install overrides), `SC2ThemePanel.cs` ("Interface Colors"), `SC2Type.cs` (five text roles), `SC2UiScale.cs` (1.0 to 2.0), `SC2UiIcon.cs`, `SC2StartupUi.cs` (start-window styles), `SC2ForgeUi.cs`, `SC2EnvFieldGui.cs` (self-contained controls) |
| Hotkeys | `Scripts/Unified/Hotkeys/` (`SC2HotkeyActions` the catalogue, `SC2HotkeyBinding`, `SC2Hotkeys` lookup and persistence, `SC2HotkeysPanel` the rebinding window) |
| Overlays | `SC2BusyOverlay.cs` + `Core/SC2BusyGate.cs`, `SC2UpdateNotice.cs`, `SC2CostsGuide.cs`, `SC2LobbySpawnPanel.cs`, `SC2MapImportPanel.cs`, `SC2InfoBdfEditorPanel.cs`, `Scripts/SC2RuntimeFileDialog.cs`, `SC2RuntimeQuitConfirm.cs`, `SC2RuntimeObjectPicker.cs` |
| Runtime stand-ins | `Scripts/SC2RuntimePopups.cs` (`SC2RuntimeDropdown`, `SC2RuntimeColorPalette`, `SC2RuntimeConfirm`; `#if !UNITY_EDITOR`), `SC2RuntimeWindow.cs` |
| Undo | `MapDocument/Commands/SC2ShellUndoCoordinator.cs` (`ISC2UndoChannel`), the dispatcher `UndoTerrainEdit` and `UndoRedoCandidate` in the host |
| Preferences | `SC2CustomizationSettings`, `SC2VisibilitySettings`, `SC2SaveSettings`, `SC2UiScale`, `SC2Hotkeys` (all PlayerPrefs) |

## 3. Data

UI state lives in the host and `SC2EditorContext`, not in the map: the main tab and sub-tab index, the viewport tool (Select, Move, Rotate, Scale, Rect, Paint, Measure), the overlay toggles (Terrain, Hfield, Costs, Collision2, Props, Markers, Units, ReferenceUnits, the four graph kinds, Chains, Areas, Decals, Water, RNM, Grid,
Wireframe), the dirty flags. `StartupComplete` is a public field so that the editor's field serialization carries it across a domain reload. **Per-user preferences** (hotkeys under `SC2.Hotkeys.v1`, theme colours, UI scale, visibility, autosave and backup counts, update-check opt-out, the
skipped version, the game install root) are in PlayerPrefs. None of it enters the map document, so the same map looks and behaves the same for anyone, and none of it appears as a diff in a saved map.

The main tabs are Terrain (Geometry, Materials, Pairs, Paint Materials, Simplified, Imported, Water, Lighting Bake, Background), Costs, Collision2, Objects (Armies, Units, Props, Markers, Paths, Areas, Chains), Decals, Environment (sixteen sub-tabs), Scenario (Info, Scripts, Preview, and Mission, which exists only in the uncommitted working tree of the mission editor), Import, Export (Export,
Validation) and Diagnostics (Import, Export, Scene, Logs). **The order of the sub-tab arrays matters**: the shells switch on the index.

## 4. Flows

- **A frame** (`OnGUI`): the skin is applied; the start screen replaces everything until `StartupComplete`; otherwise the menu bar, tabs, the panel of the active shell and the viewport input are drawn; overlays are drawn after them (hotkeys, theme, update notice, panels), and the busy overlay is drawn last.
  `SC2BusyOverlay.Tick` runs from the update. While any modal or the busy overlay is open, viewport input is blocked (the host's "an overlay is open" test).
- **Viewport input**: the host raycasts and hands the active shell the camera, the viewport rect, the mouse position and whether input is allowed; shells never read raw `Input` for the viewport.
- **Hotkeys**: a call site asks `SC2Hotkeys.Matches(actionId, Event)` (or the mouse and held variants, or `GetKey*` for `Input`). An action has any number of bindings: the first is the primary and every other is an **alternative**, all shown and editable in the panel; for example Orbit is middle mouse and Alt + middle mouse by default. The panel detects conflicts
  between actions (`FindConflicts`) and can reset one action or all.
- **Global undo**: Ctrl+Z builds eight candidates (terrain, objects, costs, Collision2, scenario, environment, decals, and the host's imported terrains), each with an availability test and the timestamp of its last action, and runs the one with the newest timestamp; the reference units and the background are reached through the terrain stack ([Undo across systems](../Interactions/UndoAcrossSystems.md)). Inside a shell, `SC2ShellUndoCoordinator` merges the shell's legacy snapshot stack, the document command history and, for the terrain, the opaque blend channel, in true chronological order. Imported terrains keep their own stack.
- **Long jobs**: `SC2BusyOverlay.RunInBackground(title, detail, startOnMain, onError)` shows the plate first, runs the main-thread part, then the worker part, then the result action on the main thread; input is swallowed meanwhile; a worker that trips on a main-thread-only API is rerun on the main thread. Used by autosave backup, export and the lighting bake.
- **Start screen**: set the game install root once, then open an archive map, one of the user's saved maps, a browsed `.scd` (through the Import Map panel) or create a new map.
- **Update notice**: once per launch (not inside the Unity Editor unless a test sets `CheckInEditor`) it asks the GitHub API for releases, then tags, and offers a newer version with Download, Later and Skip this version ([support](Support.md)).

## 5. Invariants

- **Plain GUI only in shells.** A shell uses `GUILayout` and the `SC2` controls; `EditorGUILayout` and `EditorStyles` only through the shim, never in new code, so the look and layout are the same in the editor and a player.
- **No global `GUI.matrix` scaling.** The UI scale changes the numbers (fonts, widths, heights through `SC2UiScale.Px`/`Pxf` and `SC2Type`), because viewport raycasting assumes raw screen pixels. A hardcoded pixel size in a new control breaks the scale.
- **Text sizes come from `SC2Type`'s five roles** (Title 16, Nav 14, Body 13, Caption 11, Micro 10 design pixels), never from a number of their own; colours come from `SC2Theme`, and anything that bakes a colour into a texture or style must rebuild when `SC2Theme.RenderVersion` changes.
- **Every hotkey is looked up by action id.** A literal `KeyCode` comparison in a shell is a bug (the user cannot rebind it); a new action needs an entry in `SC2HotkeyActions.All` with its defaults, and all bindings of an action must work, not only the first.
- **Hotkey storage is versioned** (`HotkeysFile.Version`); a binding that is not stored falls back to the action's defaults.
- **One overlay at a time blocks input**; a new modal must be added to the host's open-overlay test or clicks leak through to the viewport.
- **Sub-tab names are code.** Renaming one without the matching `switch` in its shell silently shows the wrong panel.
- **The busy gate is shown before the job starts**, so the main-thread part never runs before the plate is on screen.

## 6. Editor and build differences

- The UI is drawn by the same code in the Game View and in a player. What differs is the widgets the shim fakes, and these have a runtime replacement: dropdowns open a floating list (`SC2RuntimeDropdown`), colours a palette (`SC2RuntimeColorPalette`), confirmations a modal (`SC2RuntimeConfirm`), file picking `SC2RuntimeFileDialog`,
  quitting `SC2RuntimeQuitConfirm`. `EditorUtility.DisplayDialog` always agrees in a player, so a cancelable question must use the runtime confirm.
- The window controls are the operating system's: `SC2RuntimeWindow` patches the real window's style bits so the native title bar can maximise it.
- The update notice does not run in the Unity Editor by default.

## 7. Extension points and how to change it

- **A new main tab or sub-tab**: the enum or array in `SC2EditorTabs`, the host's switch that draws it, a shell, the overlay and undo wiring if it edits data, a visibility toggle if it draws in the viewport (`SC2VisibilitySettings.Names` and `SC2EditorContext`).
- **A new hotkey**: an entry in `SC2HotkeyActions.All` (id, label, category, defaults), a lookup at the call site, nothing else; the panel lists it by category.
- **A new saved preference**: a PlayerPrefs key in the matching `...Settings` class (lazy-loaded static, with a default), a control in the Customize or View menu.
- **A new overlay or modal**: a static or shell-owned `IsOpen`, a draw call in the host's overlay block, an entry in the open-overlay test, closing on Escape if the others do.
- **A new shell**: constructed lazily by the host with the terrain shell and the map session; exposes `HasUndo`, `HasRedo`, `LastActionTimestamp`, `Undo`, `Redo` so the global dispatcher can include it.

## 8. Editing, verification and tests

**How to edit it safely.** `SC2GameViewEditorUI.cs` is very large (about 8 500 lines) and several work streams edit it at once; stage only your own hunks. After any change: compile the editor and a player build, open every main tab once, then test the interaction you touched with real events in the scenario harness
(a window that receives mouse and key events and reports state).

**Tests today**: `TypographyTests` (the type roles), `EditorVersionTests` (version compare and the update feed), `BusyGateTests`, `ShellUndoCoordinatorTests`, `SessionTests`, `CommandHistoryTests`. The hotkey classes, the host, the skin, the theme and the panels have no unit test.

**Tests a change must add.** A hotkey change: a case for the lookup, the alternative binding and the conflict finder (these classes use `UnityEngine` types, so extract the matching rule into a pure type first, or add an EditMode test). An undo change: a case in `ShellUndoCoordinatorTests`. A new
control or modal: a harness scenario that clicks it and reads the result. A layout change: the scenario at UI Scale 1.0 and 2.0.

## 9. Known limitations and unverified behaviour

- The host is one file of about 8 500 lines; there is no automated test of tab drawing or of input blocking.
- The behaviour of every modal in a player build was verified case by case in the past, not by a repeatable suite.
- `FindConflicts` is informational and never blocks a rebind; it compares exact key and modifier combinations of every pair of actions, whatever their category, and skips modifier-only bindings. Two actions in contexts that are never active together can therefore be listed as a conflict. Whether a listed pair is a real clash is the maintainer's judgement.

## 10. Related documents and review

[Editor vs build](EditorVsBuild.md), [Brushes](Brushes.md), [Objects](Objects.md), [Support](Support.md), [Document and persistence](DocumentAndPersistence.md), [Process: Editing](../Process/Editing.md), [Process: Verification](../Process/Verification.md).
Interactions with other systems: [Input and overlays](../Interactions/InputAndOverlays.md), [Undo](../Interactions/UndoAcrossSystems.md), [Mechanisms](../Interactions/Mechanisms.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
