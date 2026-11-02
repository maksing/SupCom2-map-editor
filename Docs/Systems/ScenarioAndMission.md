# Scenario, lobby positions, Lua files and the mission editor

Status: first draft from reading the code at commit `17863e4`, the mission parts at commit `4fa4ae8`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) first.
The mission editor (`Scripts/Unified/Mission`, `Scripts/MapDocument/Domain/Mission`, committed as `4fa4ae8`) has its own documents: [MissionEditor](MissionEditor.md) (how it works), [MissionNodeReference](MissionNodeReference.md), [CampaignFramework](../Formats/CampaignFramework.md) (what the game does with the script) and the [guide](../Guides/MissionEditorGuide.md). The mission parts below are a summary.

## 1. Purpose and boundaries

The Scenario tab (Info, Scripts, Preview, Mission) edits what the scenario file and the Lua files of a map say: the map's name, the playable area, the lobby start positions drawn on the minimap, and the mission script. It
also covers how the three Lua files (`_scenario`, `_save`, `_script`) are produced.

Not included: armies and markers themselves ([objects](Objects.md)); the export call that writes the files ([export pipeline](ExportPipeline.md)); reading Lua bytecode of imported maps (the vendored KopiLua and the importers).

## 2. Where the code is

| Part | Files |
|---|---|
| Scenario shell | `Scripts/Unified/SC2ScenarioShell.cs` (sub-tabs Info, Scripts, Preview, Mission), `SC2LobbySpawnPanel.cs` (the Lobby Spawns overlay) |
| Document | `MapDocument/Domain/Scenario/` (`SC2ScenarioDocument` with `KnownFields` and `LobbyStartPositions`, `SC2LobbyStartPosition`, `SC2ScenarioLegacyBridge`, `SC2ScenarioValidator`, `SC2SetScenarioNameCommand`, `SC2SetLobbyStartPositionsCommand`) |
| Lua writers | `Scripts/Core/SC2ObjectsExport.cs` (`RegenerateScenarioLua`, `RegenerateSaveLua`, `RegenerateScriptLua`, `PlayableAreaName`), `Scripts/SC2ScenarioLuaWriter.cs` and `Scripts/SC2SaveLuaWriter.cs` (older, called only by the editor-only `Editor/ExportWindow.cs`), `Scripts/Core/SC2LuaBlocks.cs`, `SC2LuaField.cs`, `SC2LuaValue.cs` |
| Mission graph | `MapDocument/Domain/Mission/` (`SC2MissionDocument` nodes, edges, stages, variables, armies, bases; `SC2MissionNodeCatalog`, `SC2MissionExtraNodes` (nodes that are a few lines of Lua around the game's framework, as templates), `SC2MissionArmies` (the armies as the script sees them), `SC2MissionLuaMarkers` (the fences around generated regions), `SC2MissionCompiler`, `SC2MissionValidator`, `SC2MissionSamples`, `SC2LuaSyntaxCheck`, `SC2ReplaceMissionCommand`) |
| Mission UI | `Scripts/Unified/Mission/` (`SC2MissionPanel` split over `SC2MissionPanelCanvas`, `SC2MissionPanelForms`, `SC2MissionPanelProperties`, plus `SC2MissionMapInfoBuilder`, `SC2MissionLog`) |
| Lua reading | `Runtime/KopiLua` (vendored third-party library, documented by its own README) |

## 3. Data

`SC2ScenarioDocument`: `KnownFields` (name and the other scenario fields; for an imported map filled by running the real `scenario.lua` bytecode through KopiLua), `LobbyStartPositions` (per army, a normalised point on the minimap),
the playable area name (`SC2TerrainData.PlayableAreaName`, else the first zone with role Playable). `SC2MissionDocument` (in the scenario document): nodes with typed parameters and canvas positions, edges between ports, stages, variables, armies,
base definitions and settings.

## 4. Flows

- **Info and Scripts**: the name edits `DisplayName` (the map name field of the scenario); the Scripts tab holds the one real per-map script setting, the playable area.
- **Lobby spawns**: the overlay draws the minimap with one draggable marker per `ARMY_n`; it edits only `LobbyStartPositions` (written into `_scenario.lua`'s `StartPositions`) and only reads the real marker positions to find which armies mirror each other.
- **Writing Lua**: `RegenerateScenarioLua` rewrites the original text or a fresh template with the army list in the order of the start markers and the lobby positions; `RegenerateSaveLua` writes armies, areas and chains from the objects;
  `RegenerateScriptLua` writes the plain `_script.lua`, or, when the mission graph is in use, the Lua compiled from it.
- **Mission**: the panel edits the graph document (`ApplyMission`); `SC2MissionCompiler.Compile` turns every stage into a start function and every node into a global function that calls the functions of the nodes wired to its outputs; nodes that
  wait for the game (timers, triggers, objectives, dialogue, camera moves) hand the call of their outputs to the engine as a callback. The validator reports names that the map does not contain (`SC2MissionMapInfoBuilder` supplies the map's contents) and a syntax check
  runs on the generated Lua.

### How the generated script is fenced and extended

- **Fences** (`SC2MissionLuaMarkers`): every generated region of the script is wrapped in comment lines `-- SCED:begin <id> hash=<8 hex>` and `-- SCED:end <id>`. The hash covers the text between the lines, so a region edited by hand after generation can be told from one that still matches the graph; `Parse` lists the regions of a script and `Hash` is FNV-1a over the text with every line end turned into `
`, as 8 hex digits.
- **Extra nodes** (`SC2MissionExtraNodes`): nodes that are a few lines of Lua around the game's own framework, found by reading how every campaign mission (`SC2_CA_*`), the D1 demos, the tutorials and the framework files they import use it. Each has a template the compiler fills from the node's parameters, so **adding such a node is one table entry**. Placeholders: `{name}` the parameter written as a Lua value of its kind,
  `{raw:name}` the parameter's text as it is, `{opt:name}` the parameter as a string or `nil` when empty, `{map:name}` the Lua fragment that belongs to the chosen value of a choice parameter.
- **Armies** (`SC2MissionArmies`): `ArmyNames` and `Effective` give the armies in map order with the tuning each gets (`Default`, `DefaultAlias`, `Store`); the number of human slots follows the mission's co-op setting.

## 5. Invariants

- **The order of armies in `_scenario.lua` is the order of the `ARMY_*` start markers** (see [objects](Objects.md)); renaming an army renames its lobby position.
- **Lobby positions are document data only**; the real spawns are the markers.
- **Original Lua text is kept** for imported maps and only the regenerated blocks are replaced (`SC2LuaBlocks`).
- **A mission node's generated function name is stable per node and stage**, so wired outputs call the right function.
- **The mission compiler output must pass the Lua syntax check** before it replaces the plain script.

## 6. Editor and build differences

- All of it is plain GUI and runs in both. The lobby panel and the mission panel are overlays, not windows.

## 7. Extension points and how to change it

- **A new mission node type**: an entry in `SC2MissionNodeCatalog` (parameters, ports, how it compiles), the compiler branch if it needs one, a validator rule, and a test of the generated Lua.
- **A new scenario field**: the document class, the serializer default, the writer, the Info or Scripts tab.

## 8. Editing, verification and tests

**How to edit it safely.** Change the Lua writers together with a test that parses the output. Keep `ARMY_*` ordering and the lobby rename cascade in step with the Objects shell.

**Tests today**: `ScenarioCommandTests`, `ScenarioDocumentBridgeTests`, `LobbySpawnTests`, `LuaBlockParsingTests`, `ScdEntryPointResolverTests`, `MissionGraphTests`, `MissionGraphFeatureTests`, `MissionNodeLibraryTests`, `MissionPortsAndLoopsTests`,
`MissionCampaignScriptTests`, `MissionReviewFixTests`, `MissionNodeReferenceTests` (see [MissionEditor](MissionEditor.md) section 8). The shells have no unit test.

**Tests a change must add.** A writer change: a test that regenerates from a fixture and re-parses. A mission node: the generated Lua of a small graph and the validator outcome.

## 9. Known limitations and unverified behaviour

- The mission editor's output was not run in the game by the author of this page; correctness is judged by the Lua syntax check and the tests.
- The set of scenario fields the game reads beyond those written here was not audited.

## 10. Related documents and review

[Objects](Objects.md), [Export pipeline](ExportPipeline.md), [Document and persistence](DocumentAndPersistence.md), [Import pipeline](ImportPipeline.md).
Interactions with other systems: [Objects and scenario](../Interactions/ObjectsAndScenario.md), [Save, import, export](../Interactions/SaveImportExport.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
