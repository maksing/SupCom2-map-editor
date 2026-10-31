# Environment (`info.bdf`)

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P3. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

The Environment tab edits the map's `info.bdf`: sun, shadow, water, fog, glow, bloom, point clouds, fog of war, intel, depth of field, sharpen, render passes, air elevation, wind, the five environment textures and a tab of fields
whose meaning is not confirmed. It also applies the sun, fog and sky to the viewport, and hosts the point-cloud bake. A standalone window edits any `info.bdf` file outside a map.

Not included: the water plane and depth texture ([water](Water.md)), the lighting bake itself ([lighting bake](LightingBake.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Shell | `Scripts/Unified/SC2EnvironmentShell.cs` (tabs, snapshots, undo), `SC2InfoModelFieldDrawer.cs` (the per-tab fields, shared with the standalone editor), `SC2EnvFieldGui.cs` (self-contained controls), `InfoFieldLimits.cs` (slider ranges) |
| Preview | `Scripts/SC2Environment.cs` (typed water fields, `ApplyToScene`), `SC2SkyPreview.cs`, `SC2TerrainMaterialFactory.ResolveSun`/`ResolveShadowColor` |
| Document | `MapDocument/Domain/Environment/` (`SC2EnvironmentDocument` with `WorkingHeader`, `SC2EnvironmentTextureExport`, commands `SC2SetEnvironmentHeaderCommand`, `SC2SetEnvironmentTexturePathCommand`, `SC2SetEnvironmentTextureExportCommand`) |
| Standalone editor | `Scripts/Unified/SC2InfoBdfEditorPanel.cs` (overlay), `Tools~/InfoBdfEditor` (a separate .NET tool) |
| Export | `SC2TerrainExporter.BuildInfoModel` and the environment texture block |
| Defaults | `Scripts/Core/SC2EnvironmentDefaults.cs` |

## 3. Data

`info.bdf`'s header is a flat **508-byte buffer** (`InfoModel` wraps it with typed accessors, no per-field storage). `SC2TerrainData.EnvironmentHeader` holds it plus five texture paths
(`EnvironmentMap`, `SkyMap`, `TexturePath3`, `ColorCorrectionCube`, `FogOfWarCorrectionCube`) and their source tags and copy flags. The session's document keeps a **working header** that follows edits at once; the live data catches up when the map is saved.

## 4. Flows

- **Editing**: the field drawer changes an `InfoModel` around the header; the shell takes a snapshot every frame (`MaybeSnapshot`) and writes it to the document through a command, so one Undo steps back a field edit. Sliders have limits (`InfoFieldLimits`); typed
  values are not clamped to them.
- **Viewport**: the sun direction and colour, shadow colour and fog are applied to the viewport lights and materials every tick.
- **Textures**: a pick from the texture browser is resolved to a clean game path at once; its archive origin is kept as a source tag next to the path, with a "copy into the map" flag (default off: a vanilla texture is referenced).
- **Export**: `BuildInfoModel` prefers the document's working header, then the live data, then an imported map's original `info.bdf` when nothing was touched; the five texture paths are resolved (a copied texture gets a map path) and then checked, so
  no editor marker or machine-local path is written.
- **Import/standalone**: an imported map's `info.bdf` is parsed into the header and paths; the standalone editor changes a loaded `InfoModel` with the same field drawer and its own save buttons.

## 5. Invariants

- **The header is 508 bytes**; a shorter or empty header is treated as untouched or default (`EnsureEnvironmentHeader`).
- **The sun direction means the direction to the sun**, and the baker and previews use it that way.
- **The document's working header wins over the live header** until saved; code that reads the live header alone sees stale water or sun values.
- **Texture paths are game paths** (`/textures/...`); a path with an editor marker or drive letter is dropped at export with a warning.
- **Slider limits are not validation**; a field may hold any value the file format can hold.
- **The water level here is the one the water plane, the depth texture and the costs water mask read** (`ExtractWaterElevation`, `GetWaterElevation`).

## 6. Editor and build differences

- The shell uses only plain GUI controls (`SC2EnvFieldGui`), never `EditorGUILayout`, so it looks the same in both. The standalone editor is an overlay in both, since an `EditorWindow` cannot run in a player.

## 7. Extension points and how to change it

- **A new field**: an accessor in `InfoModel` (the format layer), a control in the matching `Draw*Tab`, a limit if it has a slider, the "reset to original" support, the unverified tab if its meaning is not confirmed.
- **A new texture slot**: a path on `SC2TerrainData`, the document, `SC2EnvironmentTextureSlot`, the export block, the sweep.

## 8. Editing, verification and tests

**How to edit it safely.** Read and write through `InfoModel`, never raw offsets from the shell. Keep the working header and the live header in step through the existing snapshot path.

**Tests today**: `EnvironmentCommandTests`, `EnvironmentHeaderCommandTests`, `EnvironmentDocumentBridgeTests`, `EnvironmentDefaultsTests`; the Tools~ project has its own tests. The shell has no unit test.

**Tests a change must add.** A new field: a format round-trip through `InfoModel` and the document. A texture slot: the export scan for markers. A UI change: a scenario that edits a field and reads the header.

## 9. Known limitations and unverified behaviour

- The tab named "Advanced" (the unverified fields) holds fields whose meaning was not confirmed; they are edited as raw values.
- How closely the viewport's lighting follows the game's rendering was not verified in the game.

## 10. Related documents and review

[Water](Water.md), [Lighting bake](LightingBake.md), [Textures](Textures.md), [Export pipeline](ExportPipeline.md), [Document and persistence](DocumentAndPersistence.md).
Interactions with other systems: [Height and water](../Interactions/HeightAndWater.md), [Textures](../Interactions/TexturesAcrossSystems.md), [Save, import, export](../Interactions/SaveImportExport.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
