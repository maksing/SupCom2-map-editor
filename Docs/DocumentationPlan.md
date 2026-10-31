# Plan: documenting the editor's systems for maintenance

Status: a first draft of every system document exists (the 23 systems of the table in section 3 plus ten that were found missing while checking coverage, rows 24 to 34, the last being the interactions between systems; see the
[index](README.md)), together with this plan, the three process documents ([Editing](Process/Editing.md),
[Verification](Process/Verification.md), [Testing](Process/Testing.md)), the architecture overview, the glossary, `CONTRIBUTING.md` and the
pull request template. Every draft was written from reading the code at one commit and has had no second reader. The game's file formats are documented one page per data structure in `Docs/Formats/`. Not yet written: the
check script and the scenario harness (sections 5.5 and 5.6), which the process documents name as planned. Writing tests is mandatory for every change of behaviour; the rules for editing, verifying and testing (section 5) are part of
the documentation, not an extra.

## 1. Goal and audience

**Goal.** Every system of the editor has a document that lets a maintainer who did not write it change it safely: what the system is for,
where its code is, how its data flows, which rules must not be broken, how to edit it, how to check a change, which tests a change must
add, and what is known to be wrong or unverified.

**Audience.** The developer who maintains the editor (first), a contributor reading the public repository (second). Documentation for map
makers (how to use the editor) is a separate track and only linked from here; the existing `SC2_Costs_Guide_Draft.md`, the Steam guide and
the README stay the starting points of that track.

**Language.** English, like the README and the code comments, so the repository reads as one.

**Where.** `Docs/` in the repository: `Docs/README.md` (index), `Docs/Architecture.md`, `Docs/Systems/<System>.md`, `Docs/Glossary.md`,
`Docs/Formats/<Format>.md`, `Docs/Process/{Editing,Verification,Testing}.md`, `CONTRIBUTING.md` at the root. Diagrams as Mermaid inside the
markdown, so they are reviewed and versioned like text.

## 2. What the code base is made of (inventory)

Sizes are lines of C# measured in the working tree. They show where the weight is, not how hard a system is.

| Folder | Lines | What lives there |
|---|---:|---|
| `Scripts/Unified` | 48 400 | the whole editor UI (one IMGUI game view with tabs) and the shell of each tab |
| `Scripts/TerrainEditor` | 17 900 | terrain runtime on the GPU, sculpt and paint modes, simplification, export, lighting bake, background terrain |
| `Scripts/MapDocument` | 15 900 | the document: commands, undo, serializer, blob store, bridges to the old data, migrations |
| `Scripts/Core` | 8 500 | the data model and the pure rules (terrain data, materials, objects, costs rules, symmetry, blend layers, path grid) |
| `Scripts` (root) | — | importers, material and water factories, Lua writers, game archive, runtime stand-ins for editor APIs |
| `Scripts/TextureBrowser` | 3 500 | texture catalog, browser, custom texture import |
| `Runtime/BdfIO` | 4 200 | readers and writers of the game's `.bdf` formats |
| `Runtime/KopiLua` | 18 500 | vendored third-party Lua port: documented by its own README, not by us |
| `Tests`, `Tests~` | — | Unity EditMode tests and the standalone characterization suite (`dotnet test`) |
| `Tools~/InfoBdfEditor` | — | a separate standalone tool |

## 3. The systems to document

Each row becomes one document in `Docs/Systems/`. "Priority" says what to write first: **P1** = a wrong change loses user data or breaks the
exported map, **P2** = large or often changed, **P3** = self-contained and stable.

| # | System | Main code | Priority |
|---|---|---|---|
| 1 | Document, commands, undo, persistence and backups (save layout, blob store, migrations, the legacy bridges, autosave, Restore Backup) | `MapDocument/*`, `SC2RuntimeTerrainPersistence`, `SC2SaveStorage`, `SC2SaveLayout` | P1 |
| 2 | Export pipeline (terrain, hfield, costs, collision2, info, mapobjs, decals, lua, texture copying, path safety) | `SC2TerrainExporter`, `SC2TerrainLayerBuilder`, `SC2HfieldProjectionService`, `BdfIO` | P1 |
| 3 | Terrain data model and the live terrain runtime (heights, pair map, blend render textures, chunks, preview modes) | `SC2TerrainData`, `SC2TerrainRuntime*`, `SC2TerrainShell` | P1 |
| 4 | Materials, pairs and shared materials (core and background share one list) | `SC2MaterialsPairsMode`, `SC2SharedMaterials`, `SC2MaterialPairLinker` | P1 |
| 5 | Blend mask: painting, layers, outline, seam painting, persistence, export of the mask files | `SC2PaintMode`, `SC2BlendMaskLayers`, `SC2TerrainRuntime.BlendLayers`, `SC2RnmDocument` | P1 |
| 6 | Costs (blocks, navmesh rules, auto-generate, derived blocks, shore, block set switch, island map) | `SC2CostsShell`, `SC2CostsAutoGen`, `SC2CostsNavmeshRules`, `SC2IslandMap` | P1 |
| 7 | Lighting bake (RNM masks, shadows, point cloud, background bake, preview shader) | `SC2RnmBakeService`, `SC2RnmMaskBaker`, `SC2PointCloudBakeService`, `SC2TerrainView.shader` | P2 |
| 8 | Background terrain and the core/background seam | `SC2BackgroundTerrain`, `SC2BackgroundRuntimeSync`, `SC2SeamReach` | P2 |
| 9 | Objects: props, markers, units, armies, zones, chains, symmetry links, validation, placement | `SC2ObjectsShell`, `SC2ObjectsModel`, `SC2ObjectsExport` | P2 |
| 10 | Path graphs and the path grid | `SC2ObjectsShell` (Paths), `SC2ObjectGraphRefs`, `SC2PathGrid` | P3 |
| 11 | Water (plane, depth texture, shore detail, preview, material slots) | `SC2WaterPlane`, `SC2WaterDepthTextureGenerator`, `SC2ShoreDetail` | P2 |
| 12 | Simplification and the layer builder | `SC2TerrainSimplifier`, `SC2TerrainLayerBuilder` | P2 |
| 13 | Sculpt and paint brushes, symmetry, stamps, custom brushes | `SC2GeometryMode`, `SC2BrushLibrary`, `SC2Symmetry`, `SC2StampPlacement` | P2 |
| 14 | Decals | `SC2DecalsShell`, `SC2DecalsExportService`, `MapDocument/Decals` | P2 |
| 15 | Environment and info.bdf (field drawer, limits, textures of the environment, the standalone InfoBdf editor) | `SC2EnvironmentShell`, `SC2InfoModelFieldDrawer`, `InfoFieldLimits` | P3 |
| 16 | Scenario, lobby start positions and the mission editor (Lua writers, KopiLua use, mission graph) | `SC2ScenarioShell`, `Unified/Mission`, `SC2ScenarioLuaWriter`, `SC2SaveLuaWriter` | P2 |
| 17 | Import pipeline (`.scd`, terrain.bdf, map objects, decals.bdf, imported terrain, texture capture for sharing) | `SC2MapImportOrchestrator`, `SC2TerrainBdfImporter`, `SC2MapshareTextureCapture` | P2 |
| 18 | Textures: catalog, browser, game archive access, custom library, vanilla classification | `TextureBrowser/*`, `SC2VanillaArchiveClassifier`, `SC2GameArchiveRegistry` | P2 |
| 19 | The editor UI framework (shells, tabs, skin, theme, UI scale, fonts, hotkeys incl. alternatives, busy overlay, runtime dialogs and dropdowns) | `SC2GameViewEditorUI*`, `SC2UnifiedSkin`, `Hotkeys/*`, `SC2Runtime*` | P2 |
| 20 | Editor APIs in a build (what `UnityEditorRuntimeShim` fakes, what differs between the editor and a player) | `UnityEditorRuntimeShim`, `#if UNITY_EDITOR` blocks | P1 |
| 21 | Reference units, buildable overlay, intel circles | `SC2ReferenceUnitsShell`, `SC2BuildableOverlay`, `SC2ReferenceUnitIntel` | P3 |
| 22 | Support: update notice, settings, logging, file manager | `SC2UpdateNotice`, `SC2SaveSettings`, `SC2Log`, `SC2FileManager` | P3 |
| 23 | Tests, verification harness and the release process (build, standalone tests, versioning, GitHub release) | `Tests`, `Tests~`, `SC2EditorVersion` | P2 |
| 24 | Collision2 (generation from the hfield, preview, OBJ round trip, custom mesh at export). Added while writing; not in the first inventory | `SC2Collision2Shell`, `SC2Collision2MeshBuilder`, `MapDocument/Collision2` | P3 |
| 25 | BdfIO: the container and the seven formats in code | `Runtime/BdfIO` | P1 |
| 26 | Smart export layer | `Runtime/SmartExport` | P2 |
| 27 | The map document's domains, commands, validators, session | `Scripts/MapDocument` | P1 |
| 28 | Game assets: blueprints, models, units, props, markers, effects, sounds | `Runtime/GameAssets`, `Scripts/GameAssets`, `SC2GameAssetScenePreview` | P2 |
| 29 | Shaders | `Resources/*.shader` | P2 |
| 30 | Project layout, Unity settings, building the player | `ProjectSettings`, `.asmdef` files, `.gitignore` | P1 |
| 31 | The pure rules of `Core`, one by one | `Scripts/Core` | P2 |
| 32 | Editor-only tools and the older scene-based workflow | `Assets/SC2/Editor`, scene data carriers | P3 |
| 33 | The standalone Info.bdf editor | `Tools~/InfoBdfEditor` | P3 |
| 34 | Interactions between systems (mechanisms, data ownership, life of an edit, height and water, masks and lighting, textures, objects and scenario, undo, input, save and export, change impact) | `Docs/Interactions/` | P1 |

Cross-cutting documents (not systems, but every system document points at them):

- **Architecture overview**: the three layers (document, legacy data and runtime, UI shells), how a user edit travels through them, what is
  authoritative where, and the two undo mechanisms (legacy snapshots and document commands).
- **Format notes** (`Docs/Formats/`): `terrain.bdf`, `hfield.bdf`, `costs.bdf`, `info.bdf`, `mapobjs.bdf`, `decals.bdf`, `collision2.bdf`,
  the RNM mask files, scenario/save Lua. Much of what is known about them sits in code comments as measured facts about vanilla maps;
  these pages collect it once, with the evidence.
- **Glossary**: pair, mask group, block, derived block, shell, core/background, layer (terrain layer vs blend layer), marker/node/graph.
- **Conventions**: comment and naming rules the project already keeps, the commit rules, how `.meta` files are made.

## 4. The shape of one system document

Every document uses the same headings, so a maintainer always knows where to look. A section that does not apply says "Not applicable" and
why; it is not left out.

1. **Purpose and boundaries**: what the system does and, as important, what it deliberately does not (and which system does).
2. **Where the code is**: files and their roles, one line each; entry points.
3. **Data**: the structures it owns, where each is stored (live data, document, blob), and how it is versioned or migrated.
4. **Flows**: the two or three that matter, as a short list or a Mermaid sequence: user action to data to preview to save to export.
5. **Invariants**: the rules that must hold, with the bug each one prevents, for example "a mask group is painted on the first pair
   of a surface; the preview shows it over every pair, but the export writes one file per group".
6. **Editor and build differences**: behaviour that differs (stubbed dialogs, `#if UNITY_EDITOR` branches, paths).
7. **Extension points and how to change it**: where a new option, tool or field goes, and the checklist of files to touch.
8. **Editing, verification and tests**, three required parts:
   - *How to edit it safely*: the checklist of files to touch for the common additions to this system and the traps of the system.
   - *The tests that cover it today*: named, with the command that runs them, and the harness scenario for what a unit test cannot reach.
   - *The tests a change to it must add*: the minimum list for this system (see section 5). A system document without this list is not
     accepted, and every test it names must exist or be scheduled before the system's next change.
9. **Known limitations and unverified behaviour**: what was never confirmed in the game, with the date and the evidence level.
10. **Related documents** and the **last reviewed** line: date, the commit it was checked against, the reviewer.

Style rules: state the durable reason, not the history of how it was found; name files and methods so they can be searched; no screenshots
of code; at most one screenshot per tab, kept in `Docs/Images/` and listed in the document that uses it.

## 5. Rules for editing, verifying and testing

These rules are written once in three process documents and repeated, as the concrete minimum, in the "Editing, verification and tests"
part of every system document.

**The policy.** Writing a test is mandatory for every change of behaviour. A bug fix starts with a test that fails and ends with it passing.
A new feature ships with tests of its rules and of its persistence. When a unit test is impossible (IMGUI layout, shaders, behaviour in a
player build) a reproducible scenario in the harness that lives in the repository takes its place, together with a note in the commit of
what it showed. There is no "no test" exception, only "a test of another kind". A commit that changes behaviour without its test is not
finished.

**Enforcement.** The written rule, the pull request checklist below, and one script that runs every check with one command. There is no CI
in this step, because the repository has none today; it can be added later for the part that runs without Unity.

### 5.1 `Docs/Process/Editing.md`: how to change the code safely

- Orientation: find the system in `Docs/Systems/`, read its invariants and its "Editor and build differences" before changing anything.
- The rules the project already keeps, in one place: comments in English that state the durable reason (no dates, no history of a chat, no
  quoted requests); one change per commit; never stage files that are not yours; no attribution trailers; a new `.meta` file gets a new
  guid; user data folders are read-only; the textures of the game are never committed.
- Editor versus build: every `EditorUtility`, `AssetDatabase` and `EditorGUILayout` call has a stub or another path in a player
  (`UnityEditorRuntimeShim`). A dialog that must be cancelable uses the runtime confirm, never `EditorUtility.DisplayDialog` alone, which
  always agrees in a build.
- Data changes: a new saved field needs its place in the document (serializer, both bridges, a default for older saves), in the undo
  snapshot, and a round-trip test. An old save without the field must load.
- Export changes: nothing the game cannot resolve may reach a `.bdf`; a new texture slot goes through the same resolver and the final
  sweep of editor markers and local paths.
- The file checklists for the common additions (a new pair option, a new tab, a new marker type, a new hotkey, a new saved setting),
  collected from the "how to edit it safely" part of each system document.

### 5.2 `Docs/Process/Verification.md`: how to check a change beyond the unit tests

- The order of checks: compile the editor and a player build (the `#if` branches differ), the unit tests, the EditMode tests, the scenario
  of the touched system, a look at the result (a screenshot or an exported file), and only then a manual pass in the build.
- The scenario harness: what it is, where it lives, how to add a scenario, how to run one headless and in a player. A player run uses a
  throwaway save folder, never the data of a user.
- What a report or a commit says openly: what was verified, what was not, and by what evidence (code reading, unit test, headless run,
  player run).
- Verifying an export: parse the written files back (terrain, info, costs) and scan every file as text for editor markers and local paths.
- Verifying the UI: the harness delivers real IMGUI events to a window and reads the state afterwards; a screenshot is taken by the player
  itself, never of the desktop.

### 5.3 `Docs/Process/Testing.md`: the policy and the how-to

- The policy first, stated plainly (above).
- Which kind of test where:

  | What is under test | Kind | Where |
  |---|---|---|
  | Pure logic and file formats | standalone xUnit, preferred whenever the code can be made free of `UnityEngine` objects | `Tests~/StandaloneCharacterization` |
  | Code that needs `ScriptableObject`s or other Unity types | EditMode test | `Tests/EditMode` |
  | IMGUI, shaders, behaviour in a build | a harness scenario, and a unit test for every rule that can be pulled out | the harness |

- How to add a test: file placement, naming (`Method_Condition_Result`), the `Compile Include` line for a new source file in the standalone
  project (a new `Core` or `MapDocument` file is not tested until it is added), the `.meta` file for Unity test files, and what a good
  characterization test of a fact about vanilla maps looks like (the evidence is named in a comment).
- The minimum every kind of change must test:
  - a data model change: the round trip through save and load, an old save loads, undo and redo restore it;
  - an export change: the written file parsed back;
  - a rule change: its boundary cases;
  - a UI change: the state after real events.
- Running: the one command, what it runs, the expected baseline, how to read a failure, the "filename too long" workaround of the standalone
  project, how to run a single test.
- A test is not skipped. One that cannot run on a machine says why and is listed.

### 5.4 The pull request checklist (`.github/pull_request_template.md`, also linked from `CONTRIBUTING.md`)

- [ ] The system document was read; its invariants still hold, or the document is updated in this commit.
- [ ] A test was added or changed for the behaviour (kind: unit, EditMode or scenario); a bug fix has a test that failed before.
- [ ] The check script passes (compile of the editor and of a player, standalone tests, EditMode tests, scenarios of the touched systems).
- [ ] Saved data: the round trip and an old save were tested (or "no saved data changed").
- [ ] Export: the written files were parsed back and scanned (or "export not touched").
- [ ] The commit says what was verified, what was not, and how.
- [ ] Only my own changes are staged; no game textures; no user data touched.

### 5.5 The one-command check script (`Tools~/check.ps1` or similar)

Run from the repository root, in this order, stopping at the first red step with a clear message:

1. `dotnet test` of the standalone suite;
2. the Unity EditMode tests in batch mode;
3. a Windows player build, to compile the `#if !UNITY_EDITOR` branches;
4. the scenario harness: every scenario, or those of the named systems.

The Unity editor path comes from an environment variable. Steps 2 to 4 are skipped with a loud notice when Unity is not installed, and
the notice says the check was partial.

### 5.6 The scenario harness in the repository

Today the scenarios used to confirm behaviour (headless Unity runs that export, bake, paint, render, run a player build and read its
report) live in a scratch project outside the repository, so nobody else can repeat them. Phase 0 moves the ones worth keeping into the
repository (the folder is to be chosen, `Tools~/Harness` is assumed), each with a name, the system it covers and a command, and makes
them runnable from the check script. A scenario that touches the save folder uses a throwaway one.

## 6. Order of work

Sizes: S = a few hours of reading and writing, M = about a day, L = several days, because the code is large or the knowledge is spread over
comments, tests and the history.

| Phase | Content | Size |
|---|---|---|
| 0. Foundations | `Docs/README.md` index, the architecture overview, glossary, conventions, the document template, a link and heading checker; the three process documents (section 5), `CONTRIBUTING.md` and the pull request template; the one-command check script; the scenario harness moved into the repository; a first run of the whole suite to record the baseline (green, or the known failures named) | L |
| 1. What loses data or breaks the map (P1) | systems 1, 2, 3, 4, 5, 6, 20; the `.bdf` format pages they depend on. A P1 document is not accepted until its "tests a change must add" list exists and every test it names exists or is scheduled before that system's next change | L each for 1, 2 and 6; M for the rest |
| 2. Large or often changed (P2) | 7, 8, 9, 11, 12, 13, 14, 16, 17, 18, 19, 23 | M to L |
| 3. Self-contained (P3) | 10, 15, 21, 22 | S each |
| 4. Review pass | read every document against the code it names, fix drift, fill the cross links, publish the index | M |

Within a phase, the order follows how often the system was changed in the last months (from `git log`), and what the open bug reports touch:
the most changed system is where the next maintainer will land first.

## 7. How each document is produced

1. **Collect.** List the files of the system, read the code, the tests, the existing long comments and the relevant `git log` messages.
   The code base keeps many measured facts as comments (vanilla corpus findings, past regressions); they are the raw material.
2. **Check, do not copy.** Every statement about behaviour is confirmed by reading the code path or running a scenario in the verification
   harness, and points at the test or scenario that pins it. A statement with nothing that pins it is listed as unpinned, and writing that
   test is added to the work list of the system. What could not be confirmed goes into "Known limitations and unverified behaviour",
   not into the main text.
3. **Write** into the template; keep the main text short, move evidence and long histories into an appendix of the same file.
4. **Review.** A second pass by someone who did not write it follows one real scenario from the document alone (for example "add a new
   shader flag to a pair") and notes every place where the document was not enough.
5. **Close.** The "last reviewed" line is filled in, the index and the cross links are updated.

## 8. Keeping the documents true

- **A change that alters a rule updates its document in the same commit.** The pull request checklist (section 5.4) has a line for it, and
  another for the test that goes with the change.
- **Each document lists the files it covers.** A small script compares them with `git log` since the "last reviewed" commit and reports the
  documents whose files changed (a drift report, run before a release, not a gate on every commit). The report also lists changed source
  files with no changed test.
- **Comments and documents split the work.** The code comment says why a line is as it is; the document says how the system works as a whole.
  Neither repeats the other; a comment that has grown into an explanation of a system moves to the document and leaves a pointer.
- **Ownership.** Every document has a named maintainer in its header; the drift report names them.
- **The verification harness is kept.** Scenarios used to confirm behaviour (headless exports, player runs, EditMode and standalone tests)
  that are worth rerunning become tests or documented scripts under `Tests`/`Tests~`, so "how to verify" stays true.

## 9. Definition of done for the whole effort

- All 23 systems have a document in the template, and every cross-cutting document exists.
- A new maintainer can, from the documents alone, find the code of any tab, say where its data is saved and what the export writes from it.
- Every P1 document has been reviewed by a second reader following one real change scenario.
- Every P1 system has its "tests a change must add" list, and the tests it names exist.
- The check script runs green from a clean checkout with the documented command, and the scenario harness is in the repository and runs
  headless.
- The drift report runs and reports nothing stale at a release.

## 10. Risks and open questions

- **The knowledge lives partly in comments and in history, not in the code's structure.** Reading alone can miss why a rule exists;
  every P1 document needs a check against the commit messages and the tests that pin the rule.
- **Some behaviour was never confirmed in the game.** The documents must say so; guessing is worse than a gap.
- **Two undo mechanisms and a legacy data model next to the document model** make the architecture hard to explain; the overview should be
  written (and agreed with the maintainer) before the system documents rely on it.
- **The Unity-dependent parts cannot be tested without an editor.** Their coverage depends on the harness, so it has to be made
  reproducible first (section 5.6).
- **The standalone test project uses an absolute path to the repository** (`SC2Root`), so it breaks on another machine; phase 0 makes it
  relative or settable by an environment variable.
- **To decide before starting:** whether `Docs/` stays in this repository (assumed here), whether the map-maker guides move under `Docs/` too,
  and who reviews. Whether the vendored KopiLua gets only a pointer to its own README (assumed). Where the scenario harness lives and
whether the existing scratch scenarios may be moved there as they are (`Tools~/Harness` assumed).
