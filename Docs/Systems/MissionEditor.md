# The mission editor

Status: implementation reference for the new-mission node editor. Existing mission import/decompilation is out of scope. Priority P2 (a wrong change breaks the exported mission, not saved data). Read [Architecture](../Architecture.md) first.
For the node list see [MissionNodeReference](MissionNodeReference.md); for what the game does with the generated script see [CampaignFramework](../Formats/CampaignFramework.md); for how to use it see the [guide](../Guides/MissionEditorGuide.md).

## 1. Purpose and boundaries

A node graph that is compiled into the whole `_script.lua` of a map, so that a map maker can build a campaign-style mission (objectives, spawns, cutscenes, dialogue, an AI base with attack waves, co-op, a difficulty menu) without writing Lua. The graph is
saved with the map, edited in an overlay panel, validated against the map's contents and compiled on export.

It owns: the graph document, the node catalog, the compiler that turns it into Lua, the validator, the panel, and the rules for when the generated script replaces the plain one.

Not included: the plain `_script.lua` and the other two Lua files ([Scenario and mission](ScenarioAndMission.md), [Lua files](../Formats/LuaFiles.md)); armies, markers, areas, chains and unit groups themselves ([Objects](Objects.md)) - the graph only
points at them by name; the export call ([Export pipeline](ExportPipeline.md)); running the mission (the game does).

## 2. Where the code is

| Part | Files |
|---|---|
| Document | `Scripts/MapDocument/Domain/Mission/SC2MissionDocument.cs`: `SC2MissionDocument`, `SC2MissionStage`, `SC2MissionNode`, `SC2MissionParam`, `SC2MissionEdge`, `SC2MissionVariable`, `SC2MissionBase` (+ `SC2MissionBuildGroup`, `SC2MissionOpAI`), `SC2MissionArmy`, `SC2MissionSettings`; `SC2MissionGraphLayout.cs` (deterministic authoring layout) |
| Node catalog | `SC2MissionNodeCatalog.cs` (definitions, parameter kinds, `InputsOf`/`PortsOf`, the core nodes written by hand), `SC2MissionExtraNodes.cs` (nodes written as templates: about 150 of them, loops included) |
| Compiler | `SC2MissionCompiler.cs` (`Compile`, the regions, the template engine `Expand`, and `SC2MissionCompileResult.SourceMap`), `SC2MissionLuaMarkers.cs` (the fences around regions), `SC2MissionArmies.cs` (the army block), `SC2LuaSyntaxCheck.cs` (compile-only KopiLua) |
| Validator | `SC2MissionValidator.cs` (`Validate`, `SC2MissionMapInfo` - the names the map has) |
| Samples | `SC2MissionSamples.cs` (Destroy the outpost, Hold the line (co-op), Three waves) |
| Command | `SC2ReplaceMissionCommand.cs` (one undoable step replaces the whole mission state) |
| Saving | `Scripts/MapDocument/Storage/SC2MapDocumentSerializer.cs` (`WriteMission`/`ReadMission`), `SC2ScenarioDocument.Mission` |
| Panel | `Scripts/Unified/Mission/`: `SC2MissionPanel.cs` (frame, header, tabs, styles, undo counters, keys), `SC2MissionPanelCanvas.cs` (library, canvas, ports, wires, input, zoom, the draggable borders), `SC2MissionPanelSelection.cs` (selection of several nodes, box select, group move, copy/paste, frames, hover and issue marks, minimap, status line, shortcut list), `SC2MissionPanelProperties.cs` (field helpers, node/edge/stage properties, bottom tabs), `SC2MissionPanelForms.cs` (Variables, Settings, Bases), `SC2MissionMapInfoBuilder.cs` (reads the map into `SC2MissionMapInfo`), `SC2MissionLog.cs` |
| Hooks into the editor | `SC2ScenarioShell.cs` (the Mission sub-tab, `ApplyMission`, `OpenMissionEditor`), `SC2GameViewEditorUI.cs` (owns the panel, draws it before the runtime overlays, masks input while it is open), `SC2EditorTabs.cs`, `SC2TerrainShell.cs` (`AdoptLoadedDocument`), `SC2RuntimeTerrainPersistence.cs` (carries the mission in a build), `SC2TerrainExporter.cs` (`RegenerateScriptWithMission`), `SC2ObjectsExport.cs` (`ScenarioArmyNames`, `PlayableAreaName`) |
| Tests | `Tests~/StandaloneCharacterization/Mission*Tests.cs` (section 8) |

## 3. Data

`SC2MissionDocument` (in `SC2ScenarioDocument.Mission`): `SchemaVersion` (currently `1`), `Enabled` (use the graph for `_script.lua`), `Title`, `Stages`, `Variables`, `Bases`, `Armies`, `Settings`.

| Part | What it holds |
|---|---|
| Stage | `Id` (lower-case letters, digits, underscores; part of the Lua function names), `Name`, `Nodes`, `Edges`, `NextNodeId`. A stage is a tab of the graph and one Lua function (`SCED_<id>_Start`). The virtual node 0 is the stage start. |
| Node | `Id`, `Type` (a key of the catalog), `X`/`Y` (graph units), `Params` (key to text). |
| Edge (wire) | `FromNode`, `FromPort` (an output), `ToNode`, `ToPort` (an input, `in` by default). One output may have several wires, one input may receive several. |
| Variable | `Name`, `Type` (number, bool, text), `Initial`. Lives in `ScenarioInfo.SCED.vars`. |
| Base | an AI base of one army: `Id`, `Army`, `Marker`, `Radius`, `BuildGroups` (group name to priority), `Engineers`, `StartMode` (filled/empty), `StartAtBegin`, `OpAIs` (type, name, thread, chains, child count, max platoons, attack delay, enabled). |
| Army | the tuning of one army: `Name`, `Alias` (the script's name, `PLAYER`, `ENEM01`...), `Role`, three colours, `UnitCap`, `MassBoost`, `EnergyBoost`, `Mass`, `Energy`, `Research`, `UnlockResearch`. Only armies the user changed are stored; the others use `SC2MissionArmies.Default`. |
| Settings | `Coop`, `HumanSlots` (1 to 4), `SharedCap`, `FailWhen` (any/all/never), `TestStartStage`. |

`IsEmpty` is true only when nothing at all has been entered (no stages, bases, variables, army tuning, non-default settings or title). An empty graph, or one with `Enabled` off, leaves the plain script alone.

**Saved as** the `mission` object of the scenario domain in `manifest.json` (`WriteMission`/`ReadMission`): `schemaVersion`, `enabled`, `title`, `stages[{id,name,nextNodeId,nodes[{id,type,x,y,params}],edges[{from,port,to,toPort?}]}]`, `variables`, `bases`, `armies`, `settings`. Missing or zero schema versions are upgraded to the current version on read/clone.
`toPort` is written only when it is not `in`. A save written before the mission existed has no `mission` key and reads as an empty, disabled graph. Graphs saved before wires had an input name read with `in`; a node that has no input `in` (the loops, the gate) then reports the wire as an error.

## 4. Flows

### 4.1 Editing

The panel (`SC2MissionPanel`) works on a **working copy** (`_work`). A change calls `Mutate(name)` (marks it dirty); `Commit()` writes the copy into the document with `SC2ScenarioShell.ApplyMission`, one `SC2ReplaceMissionCommand` per commit, so an
undo restores the whole mission state. A text field commits when it loses focus (`TrackFocus`). `SyncFromDocument` re-reads the working copy when something else changed the document (an undo from another place, a load). The panel keeps its own
undo/redo counters (the steps it made) and enables its buttons only while the shared history (capped at `SC2ShellUndoCoordinator.MaxSteps`, 100) still has them. Keys: Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z, Delete/Backspace (delete the selection), Ctrl+A/C/V/D (select all, copy, paste, duplicate), Home, F, F1 (shortcut list), Escape (one level back: the shortcut list, then the selection, then the panel).

### 4.2 Compiling

`SC2MissionCompiler.Compile(doc, map)` returns the Lua and the issues (`SC2MissionCompileResult`). It validates first, then writes the **whole `_script.lua`** as regions in this order, each fenced by `-- SCED:begin <id> hash=<8 hex>` / `-- SCED:end <id>`
(`SC2MissionLuaMarkers`; the hash covers the text between the lines, so a region edited by hand can be told from an unchanged one):

| Region | Content |
|---|---|
| `common` | imports; the state table `ScenarioInfo.SCED`; the variables' start values; helpers (`SCED_HasArmy`, `SCED_FindUnit`, `SCED_Group` (a name with commas, as the `GroupList` parameter writes it, returns one table with the units of every named group), `SCED_Platoon`, `SCED_HumanCount`); the loop engine (`SCED_LoopRound`, `SCED_LoopStop`) only when a loop node exists; the co-op watcher; the army report; `SCED_BuildBases`, `SCED_Setup`, `SCED_Begin` |
| `armies` | the army block of every campaign script: `ScenarioInfo.ARMY_<alias> = <index>`, `ScenarioInfo.AssignedObjectives = {}`, colour and tuning tables, the `local ARMY_<alias>` copies, `SCED_ResolveArmies`, then `doscript '/lua/ai/campaign/system/CAIInitializeSystems.lua'` unconditionally |
| `bases` | `SCED_Bases_Setup`: one block per base (BaseManager, build groups, OpAI, start); only when there are bases |
| one per stage | `SCED_<stage>_Start`, then for every node its output functions and its input functions |
| `campaign` | `OnPopulate`, `OnStart`, `OnInitCamera` as the campaign scripts have them, and the debug hotkeys of the plain script |

Function names: a stage start `SCED_<stage>_Start`; a node `SCED_<stage>_N<id>` (the function of its first input `in`); other inputs `SCED_<stage>_N<id>_in_<input>`; an output `SCED_<stage>_N<id>_<port>` (what runs when the node
finishes through that port; it calls the input functions of every node wired to it). A node that waits for the game (a timer, a trigger, an objective, a dialogue, a camera move) hands the output function to the engine as a callback; a node that does not wait calls
its outputs itself.

**Flow of a run** (the campaign flow): `OnPopulate` resolves the army indices, sets the camera, calls `CAMPAIGN_OnPopulate` (which creates the armies and their INITIAL groups), then the per-army setup; `OnInitCamera` gives the game `funcSetup = SCED_Setup`
(runs on the load screen: builds the bases, unless a difficulty question does it later) and `funcMain = SCED_Begin` (runs when the game is released: starts the co-op watcher, the army report and the first stage, or the test stage).

### 4.3 Nodes: how a node becomes Lua

A node type is a `SC2MissionNodeDef`: `Type`, `Title`, `Category`, `Params`, `Ports` (outputs), `Defers` (it only arms something; its outputs run later), and how it is written:

- **by hand** in `SC2MissionCompiler.AppendNodeBody` (a `switch` on the type): the core nodes (timer, area entered, unit/group killed, spawn group, give order, the simple cutscene, dialogue, set variable, branch, co-op branch, base start, OpAI set, advance, victory, defeat, raw Lua) and `sequence`, `sequence_timed`, `switch_variable`;
- **as an objective spec** (`SC2MissionObjectiveSpec`): the framework function that creates the objective and the pieces it gets; the compiler writes the result callback to `completed`/`failed`;
- **as a template** (`Template`, `InputTemplates`): a few lines of Lua with placeholders. Adding such a node is one table entry in `SC2MissionExtraNodes`.

Placeholders of a template: `{name}` the parameter written as a Lua value of its kind (number or `$variable`, true/false, a categories expression, a quoted string); `{raw:p}` the text as it is; `{opt:p}` a string or `nil` when empty; `{list:p}`,
`{brains:p}`, `{positions:p}` a comma list as a Lua table of strings, brains or positions; `{brain:p}`, `{armyindex:p}`, `{pos:p}` an army's brain or index, a marker's position; `{map:p}` the Lua fragment belonging to the chosen value of a choice parameter
(`ParamDef.Lua`); `{varvalue:p}` a value written the way the node's variable asks for; `{node}` the quoted key `<stage>/N<id>`; `{groupkey}` `<army>/<group>`; `{input}` the quoted name of the input being written; `{loopunit}` the unit the for-each loop named
by the node's `loop` parameter is at; `{cb:port}` the output function of a port.

**Inputs and outputs.** A node has one input `in` unless the definition lists `Inputs`, or `CountedInputsParam` makes inputs `1..N` from a number parameter. Outputs are `Ports`, or `CountedPortsParam` makes them `1..N` from a number or the length of a list
parameter, followed by `ExtraPorts`. Always read them through `SC2MissionNodeCatalog.InputsOf(def, node)` and `PortsOf(def, node)`. Each input runs its own Lua: `InputTemplates[name]` if present, otherwise `Template`; only the `Template` path gets an automatic call of
an `out` output (the inputs with a template of their own, a gate's open and close, only change state).

### 4.4 Loops

Nodes of the category Loops (`loop_repeat`, `loop_while`, `loop_forever`, `loop_foreach`) share the small engine in the `common` region. A loop keeps its state in `ScenarioInfo.SCED.loops[<node key>]`; `start` makes a new state (and stops the old one), then
`SCED_LoopRound(state)` runs a round **in a thread of its own** (so the stack never deepens and the game goes on between rounds): wait the pause, check the stop conditions (rounds, a variable comparison, the units left, a safety limit), set the counter variable, call the body
output. With `auto` the next round follows by itself; otherwise the body must lead back to the loop's `next` input. `break` stops it and fires `stopped`; the end fires `done`. The for-each loop copies the group's units at `start`, skips dead ones and puts the current unit in
`ScenarioInfo.SCED.loopUnit[<node key>]`, where the `loop_unit_*` nodes read it.
A loop is a node that defers, so the validator's check for an immediate cycle (see 4.6) accepts a wire that goes back into it.

### 4.5 Armies, co-op, difficulty, bases

- **Army indices** are the 1-based position in the scenario's army list, which is the order of the `ARMY_*` markers (`SC2ObjectsExport.ScenarioArmyNames`). The script writes the fixed numbers (as the campaign scripts do) and, when the game is set up, `SCED_ResolveArmies` looks them up by army name
  through `ListArmies()`: a lobby slot nobody took has no army, the armies after it move up, and the army that is not there is `nil`. The per-army setup, the research unlock, the bases, the unit lookup and the co-op watcher all skip an absent army.
- **Roles**: the first `HumanSlots` armies are player armies (`SetupPlayerArmy`, aliases `PLAYER`, `PLAYER2`...), the others enemy armies (`SetupNonPlayerArmy`, `ENEM01`...) unless the user set the role generic (`SetupGenericArmy`). Defaults are the Termesis values: player cap 1500, mass 2500, energy 6500, research 10; enemy cap 1000, boosts 9999999, a palette that cycles.
- **Co-op**: `Settings.Coop` sets the human slots, shared or separate unit cap (`DoNotShareUnitCap`) and the watcher thread that defeats the mission when `any`/`all` players lose their commander (`never` disables it). A solo run takes the `solo` output of the co-op branch.
- **Difficulty**: the node `ask_difficulty` shows the game's own `SimDialogue` (text and up to three buttons), writes the answer to `ScenarioInfo.Options.Difficulty` (1 to 3) and optionally to a variable. A mission that has this node builds its bases **after** the answer (`SCED_BuildBases`, once), because the campaign AI reads the difficulty
  when it creates a base; otherwise bases are built on the load screen. Only player 1 may answer by default; a timeout picks a default. The game is not paused (there is no way to pause it from the simulation, see [CampaignFramework](../Formats/CampaignFramework.md)).
- **Bases** (Bases tab): each base becomes `AddBaseManager` with build groups, `AddOpAI`/`SetPlatoonThread`/`SetChildCount`/`SetMaxActivePlatoons`/`SetAttackDelay`, and `StartNonZeroBase` or `StartEmptyBase`. A filled base needs the chain `<Id>_EngineerChain` on the map (the validator makes it an error): the game takes the engineers'
  patrol route by that name. Expansion bases (`base_expansion`) are the empty ones.

### 4.6 Validation

`SC2MissionValidator.Validate(doc, map)` returns issues (Error stops the export, Warning does not). `SC2MissionMapInfo` supplies the map's armies, areas, markers, chains, unit groups, single units, per-group and per-army unit counts and the army order; with no map (a test) the
name checks are skipped. The rules, by area:

- **Mission**: empty mission; settings (slots 1 to 4, fail rule, test stage exists, a test stage is a warning "clear it before release"); armies (alias and colour format, unique alias, role, cap, negative resources, an army with settings that is not on the map, an army with no units at all - a warning, because the OpAI of a base is built only against an army that has units); variables (name, unique, type, start value); bases (id, army, marker, radius, `_EngineerChain` of a filled base, build groups exist and are not empty, OpAI target).
- **Stage and wires**: stage id format and uniqueness; a stage nothing advances to; unknown node types; wires from an output or to an input the node does not have (including a counted port that no longer exists); nothing connected to the stage start; nodes that cannot be reached; an **immediate cycle** (nodes calling each other with no timer, trigger, objective or loop in the loop - an error: the game would never leave it).
- **Parameters**: required values set; numbers (or `$variable`); army, area, marker, chain, group, unit and base names exist on the map (warnings) and groups hold units (an error: `CreateArmyGroup` raises on an empty group); categories expressions match a safe pattern (an injection guard); variable types for set/branch; raw Lua may not contain `SCED:`.
- **Flow**: a cutscene begin with no cutscene end reachable from it (through wires or `Advance to stage`); loops without a wire to `next` (and with `auto` on and a wire to `next`), an empty loop body, a `loop_unit_*` node whose `loop` is not a for-each node of the stage; no Victory or Defeat node.

### 4.7 Export

`SC2TerrainExporter.RegenerateScriptWithMission` is called at the start of `BuildExportArtifacts`, before any file is written. With the graph disabled or empty it returns the plain script. Otherwise it compiles and syntax-checks the Lua with KopiLua (`SC2LuaSyntaxCheck.TryCompile`):
**an error or a Lua that does not compile stops the export** with `Export failed: the mission graph has N error(s)...` (a dialog in the editor, the returned text in a build); it never writes the plain script as a substitute. A failed export is not packed into an archive.
`TestStartStage` makes the generated script begin at that stage (a test aid; the validator warns).

`SC2MissionCompileResult.SourceMap` contains deterministic 1-based line ranges for every generated node (`StageId`, `NodeId`, `LineStart`, `LineEnd`). The editor can use this mapping to jump from a validation error to the generated Lua without parsing Lua again.

### 4.8 The panel

An overlay drawn by `SC2GameViewEditorUI` **before** the runtime overlays (dropdowns), with the host's input masked (`EventType.Ignore`) while it is open. Layout: header (title, "Use graph" switch, problem count, Undo, Redo, close), stage tabs (one per stage, `+ Stage`, `+ Intro`, `+ Outro`; Bases, Variables, Settings on the right), then
the library (search, categories) | canvas | properties, and a bottom area (Generated Lua with a Copy button, Validation, Mission Log). The canvas pans (drag on empty space), zooms (wheel, `+`, `-`), drags nodes, and draws wires as curves; a wire is started by dragging from an output port and dropped on an input port (a node with several inputs lists them in rows under its
header); a click on a wire selects it and a right click deletes it.
**Moving the view**: Home (or the `Home (fit all)` button) shows every node; F centres the selected node; `Auto-layout` arranges connected nodes in deterministic columns and rows without changing ids, wires or parameters; while a node or a wire is being dragged and the pointer is within `44` UI pixels of a canvas edge the view scrolls that way (`ApplyEdgePan`, faster closer to the edge), the dragged node going along.
**Adding a node from the canvas** (`OpenQuickAdd`, `DrawQuickAdd`): a wire let go over empty canvas, or a double click on it, opens a search box over the canvas (a text field and the matching nodes, title-prefix matches first); Up/Down move, Return or a click adds the node with its first input at that point and, when a wire was
dragged, wires it from that output; Escape or a click outside cancels (Escape closes only the search). While it is open the canvas ignores the mouse and the panel's keys (Home, F, Delete) do not run. Settings holds the armies editor, co-op, the test start stage and the Samples. The Scenario tab's Mission sub-tab only shows a summary and opens the panel.

**Window and hotkey**: `DrawOverlay` lays the panel out over the whole window (`Rect(0, 0, screenWidth, screenHeight)`), not as a centred card. The host opens and closes it with the remappable action `Global.MissionEditor` (default F3, `SC2HotkeyActions`), handled in `HandleGlobalUndoRedoSaveHotkey` so that it works while the editor itself is open (`ToggleMissionPanel`, `SC2MissionPanel.Close`, which commits the pending edit first).

**Selection and editing tools** (`SC2MissionPanelSelection.cs`; pure rules in `Domain/Mission/`): the selection is `_selNode` (the primary, the one the properties show) plus `_multi`, which counts only while it holds more than one node, the current tab and the primary (`HasMulti`); every place that clears or sets a selection goes through `SelectOnly`/`SetSelection`. A press on a node of a selection starts a move of all of them (`BeginNodeDrag`/`MoveDragged`, one undo step on release; a frame carries the nodes inside it); a press on empty canvas starts a box (`DragMode.Box`, rectangle kept relative to the pan so edge scrolling does not move it). Copy keeps the nodes and only the wires between them (`SC2MissionClipboard.Copy`); paste gives new ids from `NextNodeId`, remaps `loop`/`node` parameters that point inside the copy, and re-creates the wires (`Paste`). Align, spread and snap are in `SC2MissionNodeLayoutTools` (`GridSize` 20). `SC2MissionNodeDescriptions.Of` gives every node one sentence (curated, otherwise by category) for the library tooltip and the properties. A *Comment frame* is the catalog node `comment` (`IsComment`): no ports, skipped by the compiler and by the unreachable-node warning; the panel draws it behind the other nodes, moves it by its title and resizes it by its corner (`DragMode.ResizeFrame`; size in the `width`/`height` parameters). The library rows are reserved with `GUILayoutUtility.GetRect` and drawn with `GUI.Button`: a pressed button uses the drag and release events, so `DrawLibrary` reads the event type before the rows are drawn (a drag from the library to the canvas adds the node at the drop point). Panel sizes live in static fields and in `PlayerPrefs` (`SC2Mission.libraryWidth`, `propsWidth`, `bottomHeight`); the splitters compare pointer positions, not `Event.delta`.

## 5. Invariants

- **The generated script is the whole `_script.lua`**, not a patch; it must pass the Lua syntax check, and an invalid graph stops the export.
- **The army block is always present** and `CAIInitializeSystems.lua` is always `doscript`-ed, in every generated script.
- **Function names are stable** per stage id, node id, port and input name, so wires call the right function. Changing a node's id or a port's name breaks saved graphs.
- **A node must be read through `InputsOf`/`PortsOf`**, never `def.Ports` alone, because counted nodes depend on parameters.
- **Anything that names an army checks that it exists** (`SCED_HasArmy`); the generated code never assumes a full lobby.
- **A loop round runs in its own thread**; a loop never calls itself from its own stack.
- **The document is pure data**; the compiler and validator take the map's contents as `SC2MissionMapInfo`, not the live data, so they run in the standalone tests.
- **Text written into Lua goes through `Quote`**; the categories and raw Lua parameters are the only free text and are guarded by the validator.
- **Layout heights of wrapped text are computed, not left to IMGUI** (`WrappedLabel`, the library buttons): auto-layout of wrapped text was not trusted in a build.

## 6. Editor and build differences

- Dropdowns in a build are drawn by the host (`SC2RuntimeDropdown`, a keyed popup: `Popup(string fieldKey, ...)`), after the panel, so the panel masks its input while a list is open and draws before the lists; in the Editor they are `EditorGUILayout.Popup`.
- `SC2RuntimeTerrainPersistence` carries the mission in its document snapshot; `SC2TerrainShell.AdoptLoadedDocument` makes a loaded map's mission the live one (it used to be lost on re-entry).
- `ExportDialog` is a no-op in a build; the failure text is also the return value of the export, which the export panel shows.
- Under Unity's Mono runtime two things behave differently from the standalone tests: static fields of two classes that read each other are initialised in another order (the catalog's `All` is lazy, and `SC2MissionExtraNodes` keeps its own copies of the choice arrays), and
  KopiLua's table hashing could index before an array when an object hash code was negative (fixed in `Runtime/KopiLua/ltable.cs`, see its `CHANGES_FROM_UPSTREAM.md`).

## 7. Extension points and how to change it

- **A node that is a call of the game's framework**: one `Act`/`Event`/`Nis`/`Objective` entry (or a literal `SC2MissionNodeDef` with `Template`) in `SC2MissionExtraNodes.cs`. Every `{placeholder}` must name a parameter (or be one of `node`, `groupkey`, `input`, `loopunit`, or `cb:<port>` of a real output);
  a node that waits (`Defers`) must call every output through `{cb:port}`. Add it to [MissionNodeReference](MissionNodeReference.md) (`MissionNodeReferenceTests` fails otherwise).
- **A node that needs logic**: a case in `AppendNodeBody` and, if its ports depend on a parameter, `CountedPortsParam`/`CountedInputsParam`.
- **Several inputs**: `Inputs` (and `InputTemplates` for the ones that do something different), or `CountedInputsParam`.
- **A new parameter kind**: the enum, `ParamValue` in the compiler, `ValidateParams`, `DrawParam` and `OptionsFor` in the panel.
- **A new validator rule**: `SC2MissionValidator`, with a test that builds a small graph.
- **A new document field**: the class and its `Clone`/`CopyFrom`, the serializer (read with a default), `IsEmpty` if it counts as content, the panel.
- **A new category**: `CategoryOrder` in the catalog and `CategoryColor` in the panel.

## 8. Editing, verification and tests

**How to edit it safely.** Keep the template engine, the catalog and the validator in step: the library tests fail on a placeholder with no parameter, a choice without fragments, a node that does not compile or leaves a placeholder behind. A change of generated Lua is a change of every user's script - keep names stable.

**Tests today** (`Tests~/StandaloneCharacterization`, run with `dotnet test`):

| File | Covers |
|---|---|
| `MissionGraphTests` | compile, validate, markers, undo command, serialization of the graph |
| `MissionGraphFeatureTests` | variables, bases, settings, dialogue, objectives, the simple cutscene |
| `MissionCampaignScriptTests` | the army block, the Termesis reproduction, units and engineer checks, research unlock, absent lobby slots, samples fit the map |
| `MissionNodeLibraryTests` | placeholders, choices, every node compiles under KopiLua, specific mappings, the difficulty node, the simple cutscene on the NIS system |
| `MissionPortsAndLoopsTests` | inputs, counted ports, join, gate, counter, every loop, loop-unit nodes, the loop warnings |
| `MissionReviewFixTests` | path-based cutscene check, `IsEmpty`, a settings-only graph |
| `MissionGraphFeatureTests` | deterministic compiler source maps, schema defaults, and graph auto-layout invariants |
| `MissionNodeReferenceTests` | every node type is listed in the node reference |

**Not covered by a unit test** (Unity only): the panel, the exporter's abort, the undo counters, the dropdowns in a build. They were checked with a headless IMGUI harness (below) and a Windows player build, not by an automated test.

**Verifying the panel without the game.** The harness in `Assets/SC2/Tools~/MissionPanelHarness` draws the real panel in an `EditorWindow` of a scratch Unity project, sends it synthetic events, writes pictures of many states, clicks every control it drew and runs scripted flows;
it also compiles the Lua of every catalog node under Mono (a `SC2LuaSyntaxCheck` that passes in `dotnet test` can still fail in Unity, see section 6). One command per check (`Invoke-MissionHarness.ps1 -Mode Ux|Shots|Library|Clicks|Catalog|Build`);
how it works, how to run it, how to read the pictures and what it does not prove: [MissionPanelHarness](../Process/MissionPanelHarness.md).

**Tests a change must add.** A node: the generated Lua of a small graph. A validator rule: the issue for a graph that breaks it and none for one that does not. A document field: a serialization round trip. A change of the generated script: the exact text it must contain.

## 9. Known limitations and unverified behaviour

- **No generated mission was run in the game by the author.** Everything is judged by the Lua syntax check, the tests and reading the game's own scripts; the node semantics (loops, NIS, transports, drones, the Krakken, the dialogue, OpAI waves) are unverified in play.
- The difficulty menu uses `SimDialogue`, which no original mission uses; that it draws and returns the click in SC2 is unverified. The buffs of enemy armies (`SetupNonPlayerArmy`) are applied in `OnPopulate`, before the answer, and do not follow it.
- Unit groups that the map keeps inside an army's `INITIAL` group are created by the game when the map loads, whatever a Spawn group node does; the editor does not hide that (a group that must appear later has to be outside `INITIAL`).
- A wire from a node to its own input is allowed only for nodes with several inputs; a loop whose body is long and asynchronous relies on the user wiring `next`.
- The simple cutscene nodes cover begin, one camera move and end; the NIS family has the rest.
- The panel's wires that go back (a body to `next`) are drawn as ordinary curves and may pass under nodes.
- Source-map ranges are generated from compiler node markers and are intended for diagnostics; they are not a persistence format.
- The library lists 170-odd nodes; some framework facilities seen in the campaign scripts have no node (preloading dialogue data, fake builds, carrier and cannon tables, loading and unloading transports by hand, `SetMaxStorage`, `SetIntelRadius`).

## 10. Related documents and review

[ScenarioAndMission](ScenarioAndMission.md), [MissionNodeReference](MissionNodeReference.md), [CampaignFramework](../Formats/CampaignFramework.md), [Guide](../Guides/MissionEditorGuide.md), [Lua files](../Formats/LuaFiles.md), [Export pipeline](ExportPipeline.md),
[Objects](Objects.md), [Document and persistence](DocumentAndPersistence.md), [Editor vs build](EditorVsBuild.md), [Interactions: objects and scenario](../Interactions/ObjectsAndScenario.md).
Last reviewed: author, Milestones 1–4 implementation pass, 2026-11-03. Visual scratch verification: pending Unity window run; standalone tests run with one pre-existing unrelated costs-profile failure.
