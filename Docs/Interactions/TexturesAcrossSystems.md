# Textures across systems: from the picker to the exported map

Status: first draft from reading the call sites listed below at commit `17863e4`; not yet reviewed by a second reader. Read [Textures](../Systems/Textures.md) and [Export pipeline](../Systems/ExportPipeline.md) first.

One texture choice crosses five systems: the picker, the data (a material, a pair slot, an environment field, a decal atlas, a water slot), the save, the export and the map's own folder. Each hands the next a **path with a tag**, and the tags are what make the export correct.

## 1. The consumers of texture paths

| Consumer | Slots | Where the path lives | Picker |
|---|---|---|---|
| Materials | Diffuse, Normal, Mask of each `SC2Material` | the `Texture` objects registered with a resource path | `SC2MaterialsPairsMode.DrawTextureField` through `SC2TerrainShell.DrawTexturePicker` |
| Pairs | Ramp, Environment (with source tags and force-export flags) | strings on the pair | the same picker |
| Environment | EnvironmentMap, SkyMap, TexturePath3, ColorCorrectionCube, FogOfWarCorrectionCube | `SC2TerrainData.Environment*` and the document, with `EnvironmentTextureSourceTags` and `EnvironmentTextureForceExport` | `SC2InfoModelFieldDrawer.DrawInfoTextureField` through `SC2TerrainShell.DrawTexturePickerWithPath` |
| Decals | diffuse atlas, normal atlas | content-hashed blobs in the decals document, with source tags and force-copy flags | `SC2DecalsShell` through `SC2TerrainShell.DrawTexturePicker` |
| Water | the `WaterTexturePaths` of the exported water material (slot 6 is the depth texture) | `SC2TerrainData.WaterTexturePaths` | the Water tab of the terrain shell |
| Brushes | a custom brush image (`brush:NAME` or a texture path) | `Custom...BrushTexturePath` | the brush panels |
| Imported terrain | the textures named by an imported `terrain.bdf` | the imported entry | none: resolved from the archive at import; at export those the game does not ship are written into `Textures/<map>/` and the slots rewritten (masks to `maps/<map>/lighting/`) |

**The picker widgets are static methods of the terrain shell** (`DrawTexturePicker`, `DrawTexturePickerWithPath`, `DrawTextureThumb`), and the browser itself is a static overlay (`SC2TextureBrowser`) drawn once per frame by the host. That is how the Decals and Environment shells reach it without referencing the Materials code. The browser identifies the field being edited by a **picker key** string (for materials it includes the data object's instance id); a field calls `Open(key, ...)` and, on later frames, `Consume(key)` to take the pick.
While the browser is open the host masks the panel's events (`BeginPanelEventMask` / `EndPanelEventMask`) so that clicks on a slot behind the overlay do not leak through ([Input and overlays](InputAndOverlays.md)).

## 2. The path and its tag

A texture is identified by a **resource path with an origin tag** kept in `SC2TerrainMaterialFactory` (`RegisterResourcePath`, `TryGetResourcePath`):

- `archive:<entry>`: a file read from the game's archives through the shared reader;
- `file:<path>`: a file on this machine (an import from disk, the custom library);
- a plain game path (`/textures/...`) for a texture that is referenced by the game.

`SC2TerrainMaterialFactory.ResolveGamePath(entry)` turns a catalog entry into a game path (leading slash, no `.win` infix); `LoadTexturePublic` loads by tag.

**The tags never leave the editor.** They are resolved or dropped at export.

## 3. What happens at save, load and export

**Save.** The environment's source tags and force-export flags are copied from the document's working state into the data (`SyncDataForSave`), together with the working header. The save also **captures the textures that exist only on this machine**: `SC2MapshareTextureCapture.Capture(document, blobStore)` is called from the save routine (`SC2RuntimeTerrainPersistence.BuildDocumentSnapshot`) and stores the bytes of every `file:` texture that materials use and of the textures an imported terrain names that the game archive lacks, into the map's own folder.
This is a **player** path (the persistence class is `#if !UNITY_EDITOR`), so the Unity Editor's asset-based save does not capture.

**Load / resolve.** When a `file:` path is not found on this machine, `SC2CustomTextureImporter.TryRestoreFromMapshare` reads the captured bytes back from the map's folder (called from the material factory and from the exporter), so a map opened elsewhere still has its textures.

**Export.** For every slot the exporter decides:

| Source | Decision |
|---|---|
| `archive:` and the owning archive is a **vanilla** one (`SC2VanillaArchiveClassifier`, keyed off the **literal owning `.scd`**, not the catalog's source flag, because that flag is also set for map-local textures extracted from an imported map's own archive) | reference the **game path**; do not copy |
| `archive:` from a non-vanilla archive, or the slot asks to force a copy | **copy** the file into the map (`Textures/...`) and write the map path |
| `file:` | copy it into the map; the slot points at the copy; if the file is missing, try the captured bytes |
| a plain safe game path | keep it |
| anything else | drop it and warn |

Then **the final sweep**: any material slot still holding an editor marker or a machine-local path is emptied with a warning; the five environment textures and the decal and water slots go through the same check (`SC2TexturePathSafety.IsUnexportable`: no `archive:` or `file:` marker, no drive letter, no UNC, also not behind a leading slash). A path that fails is never written to a `.bdf`.

## 4. Where it goes wrong

| Symptom | Cause |
|---|---|
| a texture is missing in the game on another machine | a `file:` texture was neither copied nor captured: it was outside the save path (the Editor's asset save does not capture), or the slot was dropped by the sweep with a warning |
| a vanilla texture is copied unnecessarily | its owner was judged non-vanilla; classification is by the owning archive file |
| an imported map's own texture is treated as vanilla and not copied | the flag-based test was used instead of the archive-owner test |
| a custom texture is referenced by a `/C:/...` game path | the library path was converted to a game path without the map copy |
| the Decals or Environment picker "does nothing" | the picker key collided with another field's key, or the browser's panel event mask was not set up in that frame |
| a catalog search shows stale results | the query engine caches by `SC2TextureCatalog.Version`; an add or remove must bump it (the catalog does) |
| mips are "missing" on a vanilla texture | not a bug: the game's lighting maps have none; do not synthesize them |

## 5. Change checklist

| If you change | Also check |
|---|---|
| a new texture slot anywhere | its picker key, the source tag and force-copy fields (data and document), `SyncDataForSave` for the environment-like case, `SC2MapshareTextureCapture` if it can hold a `file:` path, the exporter's resolution and the **final sweep** |
| the catalog or classifier | `ResolveGamePath`, the vanilla test, the export copy decision |
| the path tag format | every place that parses it (`RegisterResourcePath`, `TryGetResourcePath`, `EntryPathFromTag`, the sweep) |
| the browser overlay | the host's panel event mask and the modal test |

## 6. Tests and verification

`TextureCatalogTests`, `TextureBrowserFilterTests`, `TextureSiblingMatchTests`, `VanillaArchiveClassifierTests`, `TexturePathSafetyTests`, `MapshareTexturesTests`. The chain (pick, save, open on another machine, export, read the paths back) has no test; the verification is to export a map with a vanilla, a custom and an archive texture and scan every written `.bdf` for markers and local paths ([Verification](../Process/Verification.md)).

## 7. Unverified

The export decisions in the table are from the system documents and the classifier's description; the save-side capture is from the call site in `SC2RuntimeTerrainPersistence`. No export was run for this page.
