# Project layout, Unity settings and building the player

Status: first draft from reading the project files at commit `17863e4`; not yet reviewed by a second reader. Priority P1 for the graphics settings (a wrong value makes content invisible in the player), P3 for the rest. Read [Editor vs build](EditorVsBuild.md) first.

## 1. Purpose and boundaries

The facts about the Unity project itself that code cannot show: the engine version, the render pipeline, the assemblies and what references what, the folders that matter and which are ignored by git, the graphics setting that the player depends on, and how a player is built. The README has the short version for users; this page is for the maintainer.

Not included: what each system does (their documents), the tests ([tests and release](TestsAndRelease.md)).

## 2. Where the code is

| Item | Where |
|---|---|
| Unity version | `ProjectSettings/ProjectVersion.txt`: `6000.0.77f1` |
| Product | `ProjectSettings/ProjectSettings.asset`: company `BDFTM`, product `SupCom2 map editor` (the player's save folder is derived from these, see [Document and persistence](DocumentAndPersistence.md)) |
| Graphics settings | `ProjectSettings/GraphicsSettings.asset` |
| Packages | `Packages/manifest.json` (Unity modules plus collab-proxy, timeline, ugui, visual scripting, the development feature set, multiplayer center) |
| Assemblies | the `.asmdef` files below |
| Standalone projects | `Assets/SC2/Tests~/StandaloneCharacterization`, `Assets/SC2/Tools~/InfoBdfEditor` (folders ending in `~` are ignored by Unity) |
| Ignore rules | `.gitignore` at the repository root |

## 3. Data

**Render pipeline**: the built-in render pipeline (`m_CustomRenderPipeline` is empty). **Colour space**: linear (`m_ActiveColorSpace: 1`). The shaders are written for the built-in pipeline (`CGPROGRAM`).

**Assemblies**

| Assembly | Folder | References |
|---|---|---|
| `BdfIO.Core` | `Runtime/BdfIO/Core` | none |
| `BdfIO.Formats` | `Runtime/BdfIO/Formats` | `BdfIO.Core` |
| `SC2.ThirdParty.KopiLua` | `Runtime/KopiLua` | none |
| `SC2.GameAssets` | `Runtime/GameAssets` | `SC2.ThirdParty.KopiLua` (to read blueprints stored as Lua bytecode); otherwise dependency-light, also built by the standalone project |
| `SC2.MapEditor.SmartExport` | `Runtime/SmartExport` | `BdfIO.Core`, `BdfIO.Formats` |
| `SC2.Core` | `Scripts/Core` | `BdfIO.Core`, `BdfIO.Formats`, `SC2.ThirdParty.KopiLua` |
| `SC2.GameAssets.Unity` | `Scripts/GameAssets` | `SC2.GameAssets` |
| `SC2.Tests.EditMode` | `Tests/EditMode` | `SC2.Core`, `BdfIO.Core`, `BdfIO.Formats`, the test runner; editor only, with the `UNITY_INCLUDE_TESTS` constraint |
| the default assembly | everything else under `Assets/SC2/Scripts` and `Assets/SC2/Editor` | everything |

Code in the default assembly (the document layer, the terrain runtime, all shells) **cannot be referenced by a test assembly**; a rule that needs a unit test belongs in `Scripts/Core`. (The `MapDocument` folder sits in the default assembly; the standalone test project compiles its pure files directly.)

**Graphics settings that matter**: `m_InstancingStripping: 2` (Keep All), `m_LightmapStripping: 0`, `m_FogStripping: 0`. The instancing value is required, see below.

**Folders**

| Folder | Content | In git |
|---|---|---|
| `Assets/SC2/Scripts`, `Runtime`, `Editor`, `Tests`, `Tests~`, `Tools~`, `Resources` (shaders, brushes, marker models, costs guide, the texture catalog JSON and thumbnails, `SC2TerrainData.asset`) | the editor | yes |
| `Assets/SC2Textures/*`, `Assets/SC2/Resources/SC2Textures/*`, `Assets/StreamingAssets/SC2Textures/` | the game's own textures copied from the user's install by the importer | **never** (copyrighted; ignored) |
| `SC2_Costs_Guide_Draft.md`, `SC2_Costs_Guide_Images/` | working drafts; the shipped guide is in `Assets/SC2/Resources/CostsGuide` | ignored |
| `Library/`, `Temp/`, `Obj/`, `Logs/`, `UserSettings/`, `*.csproj` except the three hand-written ones, `*.sln` | generated | ignored |
| `bin/`, `obj/` of the standalone projects | generated; one publish output once exceeded GitHub's 100 MB per-file limit | ignored |
| `Assets/Exports`, `Assets/SC2Maps`, `Assets/maps`, `Assets/lighting`, `Assets/Textures`, `Assets/Scenes`, `Assets/111.unity`, `Assets/NewTerrainData.asset` | working and sample content | present in the tree; treat as scratch unless a document says otherwise |

The three `.csproj` files that are real source (`InfoBdfEditor`, its tests, `StandaloneCharacterization`) are re-included by `!` rules after the blanket `*.csproj` ignore.

## 4. Flows

- **Building the player** (README): open the project in Unity Hub with editor `6000.0.77f1`, `File > Build Settings`, target Windows Standalone, Build or Build And Run. The player starts at the start screen ([editor UI](EditorUi.md)); user data goes to the user's `LocalLow` folder under the company and product name.
- **Window behaviour**: default size 1920 x 1080, `resizableWindow: 0` in the project settings, and `SC2RuntimeWindow` patches the real window's style at startup so the native title bar can maximise it.
- **Texture population**: importing a map copies the textures it references out of the install into the ignored folders so the editor and a build can load them as loose files; `SC2TextureManifest.txt` and the catalog JSON are regenerated after imports ([textures](Textures.md), [editor-only tools](EditorOnlyAndLegacy.md)).

## 5. Invariants

- **`m_InstancingStripping` must stay 2 (Keep All).** Placed units and props are drawn with `Graphics.DrawMeshInstanced` using materials created at runtime, so no asset references them with instancing on, and Unity's build-time variant stripper removes the `INSTANCING_ON` variant. The result is a player in which the draw call fires every frame with correct data and **nothing is drawn, with no error, warning or log line**, because the failure is inside the graphics driver.
  The same project renders fine in the editor, and terrain and water (ordinary renderers) render fine in the player. Whenever something works in the editor game view and is invisible in a player and is drawn with `DrawMesh` or `DrawMeshInstanced` from runtime-only materials, **check the stripping settings first**, before shader resolution, mesh data, cameras or lighting.
- **Runtime-only shaders live in `Resources/`** so a build includes them ([shaders](Shaders.md)).
- **The game's textures are never committed.** The three ignored folders are the whole list; a new place that stores game files needs an ignore rule in the same commit.
- **Do not reference the default assembly from `SC2.Core` or the test assembly**; the compiler cannot, and moving code into `Core` is how code becomes testable.
- **A new `.meta` file gets a new guid.**
- **Unity version changes are deliberate**: the standalone test project points at that version's `UnityEngine.CoreModule.dll` ([tests and release](TestsAndRelease.md)).

## 6. Editor and build differences

`#if UNITY_EDITOR` code is absent from the player; `UnityEditorRuntimeShim` supplies stand-in types so shared files compile. Variant stripping, shader inclusion and the `Resources` folder rules only apply to a player build. The consequences are in [Editor vs build](EditorVsBuild.md).

## 7. Extension points and how to change it

- **A new assembly**: an `.asmdef` with explicit references, and the list above updated; a pure assembly should have no references to the default assembly and gets a `Compile Include` in the standalone project if it is to be unit tested.
- **A new runtime shader**: a `.shader` under `Resources/`, loaded with `Resources.Load<Shader>`; if it is drawn instanced, confirm the player keeps the variant.
- **A new package**: through the Package Manager; keep `Packages/manifest.json` and the lock file in the same commit.
- **A new ignored data location**: `.gitignore`, with a comment saying why.

## 8. Editing, verification and tests

**How to edit it safely.** Any change to `GraphicsSettings.asset`, `ProjectSettings.asset`, an `.asmdef` or the package manifest is verified by building a player and running it, not by the editor alone. Check that a unit or prop placed on the map is visible in the player.

**Tests today**: none for project settings. A player build with a placed unit is the only check for the instancing setting.

**Tests a change must add.** A player scenario that places a unit and a prop and reads back from the screenshot taken by the player itself that pixels were drawn ([Verification](../Process/Verification.md)).

## 9. Known limitations and unverified behaviour

- `apiCompatibilityLevel: 6` is recorded as is; its label in the Unity UI was not checked here. `fullscreenMode: 1` likewise.
- The sample scene and folders at the top of `Assets` (`111.unity`, `NewTerrainData.asset`, `Exports`, `SC2Maps`, `maps`, `lighting`) were not audited; whether they are still used is unknown.
- The player was not built for this document.

## 10. Related documents and review

[Editor vs build](EditorVsBuild.md), [Game assets](GameAssets.md), [Shaders](Shaders.md), [Tests and release](TestsAndRelease.md), [Textures](Textures.md), the README.
Last reviewed: author, project files, commit `17863e4`. Second reader: not yet.
