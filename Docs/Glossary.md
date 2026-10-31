# Glossary

Terms as the code uses them. Status: first draft from reading the code at commit `17863e4`.

| Term | Meaning |
|---|---|
| **Map document** (`SC2MapDocument`) | The pure-data model of a map, one sub-document per domain. What is saved and validated. |
| **Session** (`SC2MapSession`) | The document plus its command history, blob store and accumulated dirty flags. Its workspace, selection and scene-projection parts are named seams that nothing fills yet. |
| **Live data / legacy data** (`SC2TerrainData`) | The `ScriptableObject` the shells edit directly. "Legacy" because the document was added later; both layers are still in use. |
| **Bridge** | The code that converts between the two: `*LegacyBridge.BuildFromLegacy` (live to document) and `SC2MapDocumentToLegacyBridge` (document to live). |
| **Shell** | The class that draws one tab and handles its viewport input (`SC2TerrainShell`, `SC2CostsShell`, ...). |
| **Blob** | A large binary payload (a grid, a mask) stored by content hash outside the manifest; the document holds a reference. |
| **Manifest** | `manifest.json`, the document without its blobs. |
| **Map share / map folder** | `<Map>.sc2mapshare`, the self-contained folder of one saved map (manifest, blobs, optional JSON copy). |
| **Backup** | A self-contained snapshot of a map folder kept under `Backups/<Map>/<timestamp>`; the last N are kept. |
| **Core / background** | The playable terrain, and the optional decorative terrain generated around it (a second `SC2TerrainData` with the core's rectangle carved out). |
| **Surface** | Either the core or the background, as a brush target. |
| **Seam** | The border between the core and the background; a brush near it paints both. |
| **Pair** (`SC2MaterialPair`) | Two materials plus the settings of one terrain layer of the game: shader flags, tiling, mask group name, lighting. Every triangle is assigned to one pair. |
| **Material** (`SC2Material`) | A named set of textures (diffuse, normal, mask) with a colour. The core and the background share one list of them. |
| **Pair map** | The render texture that holds the pair index of every cell. |
| **Blend mask** | One alpha plane per mask group: 0 shows the pair's first material, 255 its second. |
| **Mask group** | The set of pairs that share a `RnmMaskName`; one blend mask and one set of lighting files per group. |
| **Blend layer** | An editing layer of a blend mask (value and coverage per pixel); layers are flattened into the one alpha plane. Not the same as a terrain layer. |
| **Terrain layer** | One pair's part of the exported `terrain.bdf` (a material and its triangles). |
| **RNM** | Radiosity normal mapping: three baked lighting images (one per basis direction) plus a shadow alpha, stored per mask group as `_0`, `_1`, `_2` files. |
| **Bake** | Computing the RNM images (and the point cloud) from terrain geometry and the sun; run by the user, never by an export. |
| **Costs block** | One movement-cost grid of `costs.bdf` (a navmesh per unit class). A map has 3 (land) or 5 (land and water) blocks. |
| **Derived block** | A costs block that is computed from block 0 and is not painted. |
| **Playable water** | The map setting that selects the 5-block costs set and the four path graphs. |
| **Hfield** | The height field the game uses for pathing height, projected from the exported mesh. |
| **Collision2** | The low-polygon collision mesh built from the height field. |
| **Path graph** | A set of path-node markers and the edges between them, one graph per movement kind (Land, Air, Naval, Amphibious). |
| **Marker** | A named point object (start position, resource, path node, area marker, ...); `SC2ObjMarker`. |
| **Army** | A player slot with its start marker, its pre-placed units and its alliances. |
| **Symmetry** | A map-wide setting (mirror, rotation) that the brushes and the object tools apply to every placement. |
| **Reference unit** | A unit placed only as a visual aid; never exported. |
| **Environment header** | The 508-byte buffer of `info.bdf` (sun, fog, water, ...); the texture paths of the environment are stored next to it. |
| **Game look** | The preview settings that imitate the game's own shading (shadow amount, glow, specular). |
| **UI scale** | The user's multiplier on every size of the interface (`SC2UiScale`). |
| **Player build / player** | The standalone Windows executable, as opposed to running in the Unity editor. |
| **Container** (`BdfContainer`) | The shared outer layer of every `.bdf`: a 32-byte header, an offset table, padding, one zlib block ([Container](Formats/Container.md)). |
| **Offset table** | The list of positions inside a `.bdf`'s content that hold pointers, written in layout order. |
| **Platform** (`BdfPlatform`) | PC (`MFDB`, little-endian, the only verified one) or Xbox (`BDFM`, big-endian); decides every number's byte order. |
| **Padding mode** | `Align32`, `MinPad32` or `Align32Min8`: how many zero bytes surround the zlib block; fixed per format ([Container](Formats/Container.md)). |
| **Trailing data** | Up to 8 bytes hidden in the padding after the offset table (terrain and decals keep the start of their geometry there). |
| **EDH / EDB** | The 108-byte header and 56-byte nodes of the spatial (BSP) tree used by terrain, collision2 and decals ([Spatial tree](Formats/SpatialTree.md)). |
| **Packed TBN** | Normal, tangent and binormal stored as three signed 10-bit fields in one uint32 each, divided by 51.1. |
| **Raw height unit** | A `uint16` height in 1/128 of a world unit. |
| **Passthrough** | Writing a file's original bytes unchanged, or keeping the original text of a Lua file and replacing only the regenerated blocks. |
| **Smart export** | The planner that decides per file between passthrough, patched and regenerated output; present in code, not reachable from the UI ([SmartExport](Systems/SmartExport.md)). |
| **Dirty / stale** | Dirty: the user changed a domain directly. Stale: a source it derives from changed and it has not been regenerated. |
| **Command** (`ISC2DocumentCommand`) | A document change that can be undone and that declares the export domains it dirties (`DirectDirtyFlags`). |
| **Seam (document)** | A named empty hook in the session layer that a later stage is meant to fill (workspace, selection, scene projection). Not the terrain seam. |
| **Characterization test** | A test that pins a fact measured on the real vanilla maps rather than a design intent; it reads a local copy of the game data and is skipped when it is missing. |
| **Corpus** | The local copy of the vanilla maps the characterization tests read (`SC2_VANILLA_MAPS_DIR`); never in the repository. |
| **Scenario harness** | A reproducible headless scenario that drives the editor and reads the result back; planned for the repository, today outside it. |
| **Shim** (`UnityEditorRuntimeShim`) | Stand-in types for editor APIs so shared files compile in a player. |
| **Runtime confirm / dropdown** | The player's replacements for `EditorUtility.DisplayDialog` and for editor popups and colour fields. |
| **Variant stripping** | Unity removing shader variants nothing references at build time; the reason instanced draws need `m_InstancingStripping: 2` ([Project and build settings](Systems/ProjectAndBuildSettings.md)). |
| **Resources shader** | A shader under `Resources/`, loaded by name, so a player always includes it ([Shaders](Systems/Shaders.md)). |
| **Point cloud** (`pcloud_up`, `pcloud_down`) | Two 256 x 256 x 8 volumes that light units from the map: sky light from above, ground light from below, and the sun shadow ([Texture files](Formats/TexturesAndMasks.md)). |
| **SCM / blueprint (`.bp`)** | The game's model file and the Lua-table description of a unit or prop ([Game assets](Systems/GameAssets.md)). |
| **Effect template / emitter** | A named group of particle emitters an Effect marker plays; read from the game's Lua and emitter blueprints. |
| **Instanced draw** | One `Graphics.DrawMeshInstanced` call for all instances of a blueprint, instead of an object per instance. |
| **Fence** (`-- SCED:begin ... hash=`) | The comment lines around a generated region of the mission script; the hash tells a hand-edited region from an intact one. |
| **Start position / lobby position** | The real `ARMY_n` marker, and the point drawn on the lobby minimap and written to `StartPositions`; they are separate data. |
| **Scene workflow** | The older flow that imports a map as a Unity scene of marker, prop and graph components and exports from it ([Editor-only tools](Systems/EditorOnlyAndLegacy.md)). |
| **Startup screen** | The first screen, shown until a game install is set and a map is opened or created. |
| **Hotkey alternative** | Any binding of an action after the first; all bindings of an action work and are rebindable. |
| **Top view mirror** | The helpers that make the top-down view use the game's convention (+Z down the screen) instead of Unity's ([Core rules](Systems/CoreRules.md)). |
| **Hub (terrain shell)** | The shell that owns the live data, hosts the Water and Lighting Bake tabs and the texture picker widgets, runs the per-frame synchronisation and raises the cross-shell events ([Interactions](Interactions/README.md)). |
| **Host-wired event** | An event of the terrain shell (`DataChanged`, `HeightBrushApplied`, `ImportedTerrainMoved`) that the host connects to other shells, because shells hold no references to each other ([Mechanisms](Interactions/Mechanisms.md)). |
| **Live height grid** (`SC2LiveHeightGrid`) | The single, debounced heightmap shared by Costs, placement, decals and the HUD; the same projection the export's hfield uses, so it includes props marked "include in heightmap" and imported terrain ([Height and water](Interactions/HeightAndWater.md)). |
| **Working header** | The document's copy of the environment header, kept current every frame; it wins over the data's header until a save commits it. |
| **Carry-across** | Copying document-only data (lobby positions, mission, decals, imported entries and masks) into the freshly built document at save, and back into the session at load. |
| **Candidate (undo)** | One of the eight entries of the global undo dispatcher: a shell's availability test and the timestamp of its last action ([Undo](Interactions/UndoAcrossSystems.md)). |
| **Polled counter** | A version number that consumers compare with the one they last used (`TerrainGeometryVersion`, `RnmBakeGeneration`, `Document.Revision`). |
