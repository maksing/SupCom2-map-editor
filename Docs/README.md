# Documentation of the editor's systems

For the developer who maintains the editor and for contributors. English, like the code comments. Documentation for map makers (how to use the editor) is separate: the repository `README.md`, `SC2_Costs_Guide_Draft.md` and the Steam guide.

Every system document has the same ten headings (purpose, code, data, flows, invariants, editor and build differences, extension points, editing/verification/tests, known limitations, related documents and review). The plan and the reasons for the shape are in [DocumentationPlan](DocumentationPlan.md).
All documents are first drafts written from reading the code at one commit; **none has had a second reader yet**, and each says which behaviour was not verified.

## Start here

| Document | Read it when |
|---|---|
| [Architecture](Architecture.md) | before changing anything: the layers, how an edit travels, what is authoritative where |
| [Glossary](Glossary.md) | a word in the code or in these documents is unclear (pair, mask group, block, shell, core/background) |
| [Contributing](../CONTRIBUTING.md) | before a commit or a pull request |
| [Process: Editing](Process/Editing.md) | how to change the code safely, the project's standing rules |
| [Process: Verification](Process/Verification.md) | how to check a change beyond the unit tests, and what to state in a report |
| [Process: Testing](Process/Testing.md) | **a test is mandatory for every change of behaviour**; which kind, how to add it, how to run it, the known baseline |

## Systems

Priority: **P1** = a wrong change loses user data or breaks the exported map; **P2** = large or often changed; **P3** = self-contained and stable.

### Data, saving and export

| System | Priority | Document |
|---|---|---|
| Document, commands, undo, persistence, backups | P1 | [DocumentAndPersistence](Systems/DocumentAndPersistence.md) |
| The document's domains, commands, validators, session | P1 | [MapDocumentDomain](Systems/MapDocumentDomain.md) |
| BdfIO: the container and the seven formats in code | P1 | [BdfIO](Systems/BdfIO.md) |
| Smart export (planner, dirty flags, not used by the UI) | P2 | [SmartExport](Systems/SmartExport.md) |
| Export pipeline (all game files, path safety) | P1 | [ExportPipeline](Systems/ExportPipeline.md) |
| Terrain data model and the live terrain runtime | P1 | [TerrainDataAndRuntime](Systems/TerrainDataAndRuntime.md) |
| The editor and a player build: what differs | P1 | [EditorVsBuild](Systems/EditorVsBuild.md) |

### Terrain

| System | Priority | Document |
|---|---|---|
| Materials, pairs, the shared material list | P1 | [MaterialsAndPairs](Systems/MaterialsAndPairs.md) |
| Blend mask: painting, layers, seams, mask files | P1 | [BlendMask](Systems/BlendMask.md) |
| Sculpt and paint brushes, symmetry, stamps | P2 | [Brushes](Systems/Brushes.md) |
| Lighting bake | P2 | [LightingBake](Systems/LightingBake.md) |
| Background terrain and the seam | P2 | [BackgroundTerrain](Systems/BackgroundTerrain.md) |
| Water | P2 | [Water](Systems/Water.md) |
| Shaders (every shader, who uses it, the rules) | P2 | [Shaders](Systems/Shaders.md) |
| Simplification and the layer builder | P2 | [Simplification](Systems/Simplification.md) |

### Gameplay data

| System | Priority | Document |
|---|---|---|
| Costs (movement navmesh) and terrain types | P1 | [Costs](Systems/Costs.md) |
| Collision2 | P3 | [Collision2](Systems/Collision2.md) |
| Objects: props, markers, units, armies, areas, chains | P2 | [Objects](Systems/Objects.md) |
| Path graphs and the path grid | P3 | [PathGraphs](Systems/PathGraphs.md) |
| Game assets: blueprints, models, units, props, markers, effects, sounds | P2 | [GameAssets](Systems/GameAssets.md) |
| Reference units, buildable overlay, intel circles | P3 | [ReferenceUnits](Systems/ReferenceUnits.md) |
| Decals | P2 | [Decals](Systems/Decals.md) |
| Environment (`info.bdf`) | P3 | [Environment](Systems/Environment.md) |
| Scenario, lobby positions, Lua files | P2 | [ScenarioAndMission](Systems/ScenarioAndMission.md) |
| The mission editor: node graph compiled into `_script.lua` | P2 | [MissionEditor](Systems/MissionEditor.md), [node reference](Systems/MissionNodeReference.md) |

### The game's files

| Topic | Document |
|---|---|
| The container, strings and pointers, the spatial tree | [Container](Formats/Container.md), [String pool](Formats/StringPool.md), [Spatial tree](Formats/SpatialTree.md) |
| One page per file | [Terrain](Formats/Terrain.md), [Hfield](Formats/Hfield.md), [Costs](Formats/Costs.md), [Collision2](Formats/Collision2.md), [Info](Formats/Info.md), [MapObjs](Formats/MapObjs.md), [Decals](Formats/Decals.md) |
| Texture files (masks, point clouds, minimap, map shot, water depth) and the Lua files | [Texture files](Formats/TexturesAndMasks.md), [Lua files](Formats/LuaFiles.md) |
| What the game's Lua does with a mission script (campaign setup, armies, AI, cinematics, menus) | [CampaignFramework](Formats/CampaignFramework.md) |
| The code that reads and writes them, and the smart export layer | [BdfIO](Systems/BdfIO.md), [SmartExport](Systems/SmartExport.md) |
| Index | [Formats](Formats/README.md) |

### For map makers

| Guide | Read it when |
|---|---|
| [Making a mission](Guides/MissionEditorGuide.md) | you want to build objectives, spawns, cutscenes, AI bases, co-op, loops or a difficulty menu without writing Lua |

### Files in and textures

| System | Priority | Document |
|---|---|---|
| Import pipeline (`.scd`, `terrain.bdf`, objects, decals) | P2 | [ImportPipeline](Systems/ImportPipeline.md) |
| Textures: catalog, browser, game archive, custom library | P2 | [Textures](Systems/Textures.md) |

### The application itself

| System | Priority | Document |
|---|---|---|
| Editor UI framework (host, tabs, skin, theme, hotkeys, overlays) | P2 | [EditorUi](Systems/EditorUi.md) |
| Support: update notice, settings, logging, file manager | P3 | [Support](Systems/Support.md) |
| Tests, verification harness, release process | P2 | [TestsAndRelease](Systems/TestsAndRelease.md) |
| Project layout, Unity settings, building the player | P1 | [ProjectAndBuildSettings](Systems/ProjectAndBuildSettings.md) |
| The pure rules of `Core`, one by one, with their tests | P2 | [CoreRules](Systems/CoreRules.md) |
| Editor-only tools and the older scene-based workflow | P3 | [EditorOnlyAndLegacy](Systems/EditorOnlyAndLegacy.md) |
| The standalone Info.bdf editor | P3 | [InfoBdfEditorTool](Systems/InfoBdfEditorTool.md) |

## Orientation: where to work

| Document | Question it answers |
|---|---|
| [Orientation](Orientation/README.md) | the standard route for a change, and which route to start from for each job |
| [Live routes and legacy](Orientation/LiveRoutesAndLegacy.md) | which code is live, shared, legacy or unused, and how to check before editing |
| [The big files](Orientation/BigFiles.md) | what is where in the six largest files and how to work inside them |
| [Sources of truth](Orientation/SourcesOfTruth.md) | which copy of the data is authoritative when, and the route a new field must take |
| [Known risks](Orientation/KnownRisks.md) | the structural risks and the open defects found so far |

## How the systems work together

| Document | Question it answers |
|---|---|
| [Interactions](Interactions/README.md) | the five ways systems are coupled, and a table of who depends on whom |
| [Mechanisms](Interactions/Mechanisms.md) | every event, delegate, polled counter and shared service, and the terrain shell's per-frame synchronisation |
| [Data ownership](Interactions/DataOwnership.md) | who writes and reads each group of the live data |
| [Life of an edit](Interactions/LifeOfAnEdit.md) | a sculpt stroke and everything that must follow it |
| [Height and water](Interactions/HeightAndWater.md) | the three height sources, the water level and the rule that derived files come from one model |
| [Materials, masks, lighting](Interactions/MaterialsMasksLighting.md) | pairs, mask groups, the bake and the exported lighting files |
| [Textures](Interactions/TexturesAcrossSystems.md) | one texture choice from the picker to the exported map |
| [Objects and scenario](Interactions/ObjectsAndScenario.md) | armies, symmetry, the lobby, the mission and the Lua files |
| [Undo](Interactions/UndoAcrossSystems.md) | three mechanisms, eight candidates and what a terrain undo restores |
| [Input and overlays](Interactions/InputAndOverlays.md) | the order of a frame and of the viewport handlers |
| [Save, import, export](Interactions/SaveImportExport.md) | what each long operation reads and writes in every system |
| [Change impact](Interactions/ChangeImpact.md) | "I am changing X, what else must I check?" |

## Which document to open for a task

| I want to... | Open |
|---|---|
| change what is saved in a map, or add a saved field | [DocumentAndPersistence](Systems/DocumentAndPersistence.md), then the system that owns the data |
| change what is written to the game's files | [ExportPipeline](Systems/ExportPipeline.md) |
| add a material option or a pair setting | [MaterialsAndPairs](Systems/MaterialsAndPairs.md), [BlendMask](Systems/BlendMask.md) |
| change a dialog or anything that must work in a build | [EditorVsBuild](Systems/EditorVsBuild.md), [EditorUi](Systems/EditorUi.md) |
| add a hotkey, a tab or a menu entry | [EditorUi](Systems/EditorUi.md) |
| add a new marker or object type | [Objects](Systems/Objects.md) |
| work on the mission editor | [MissionEditor](Systems/MissionEditor.md), [MissionNodeReference](Systems/MissionNodeReference.md) |
| add or change a mission node | [MissionEditor](Systems/MissionEditor.md) section 7, then update [MissionNodeReference](Systems/MissionNodeReference.md) |
| find out what the game does with a mission script | [CampaignFramework](Formats/CampaignFramework.md) |
| make a release | [TestsAndRelease](Systems/TestsAndRelease.md), [ProjectAndBuildSettings](Systems/ProjectAndBuildSettings.md) |
| something works in the editor but is invisible in a player | [EditorVsBuild](Systems/EditorVsBuild.md), [ProjectAndBuildSettings](Systems/ProjectAndBuildSettings.md), [Shaders](Systems/Shaders.md) |
| change what a game file contains byte by byte | [Formats](Formats/README.md), [BdfIO](Systems/BdfIO.md) |
| find out what else a change affects | [Change impact](Interactions/ChangeImpact.md) |
| find the test of a rule | [CoreRules](Systems/CoreRules.md), the system's own document |
| add a validation rule or a command | [MapDocumentDomain](Systems/MapDocumentDomain.md) |

## What is not here yet

- The one-command check script and the scenario harness inside the repository, named in [DocumentationPlan](DocumentationPlan.md) as planned work.
- A second reader for every document.
