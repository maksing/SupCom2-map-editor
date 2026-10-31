# Import pipeline (`.scd`, `terrain.bdf`, objects, decals)

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

Brings existing maps and loose game files into the editor. One flow, "Import Map": open a `.scd` (file or folder) plus its sibling archives as one composite view, find the real maps inside, and import the chosen one domain
by domain. Also loose imports: a `terrain.bdf` as an **imported terrain object**, custom geometry, a collision mesh, map objects, a `decals.bdf`. And the capture that lets a map keep the textures only this machine has.

Not included: the textures and archive access ([textures](Textures.md)), what the editor does with imported terrain at export ([export](ExportPipeline.md)), parsing the formats (`Runtime/BdfIO`).

## 2. Where the code is

| Part | Files |
|---|---|
| Orchestration | `Scripts/SC2MapImportOrchestrator.cs` (`OpenComposite`, `FindCandidates`, `ImportSelected`), `Scripts/Unified/SC2MapImportPanel.cs` (the panel), `Scripts/SC2ScdEntryPointResolver.cs` (finds `scenario.lua` candidates, bytecode aware) |
| Containers | `MapDocument/Domain/SC2SourceContainer.cs` (zip or directory readers, sibling archive discovery, composite reader), `SC2MapSource.cs`, `SC2SourceCatalogFingerprint.cs`, `SC2GameInstall.cs` (the game install setting and shared reader) |
| Domain importers | `Scripts/SC2TerrainBdfImporter.cs` (mesh and materials), `Scripts/Core/SC2ObjectsImport.cs` (props, markers, armies, units, chains, areas), `Scripts/SC2CostsBdfImporter.cs`, `Scripts/SC2DecalsBdfImporter.cs`, `SC2ScenarioLegacyBridge` (scenario fields and Lua passthrough) |
| Imported terrain objects | `Scripts/SC2ImportedTerrainExportData.cs`, `SC2ImportedTerrainPivot.cs`, `Scripts/Core/SC2ImportedPivot.cs`, `MapDocument/Domain/Terrain/SC2ImportedTerrainEntryDocument.cs`, the imported-terrain handling in `SC2GameViewEditorUI` |
| Texture capture | `Scripts/Unified/SC2MapshareTextureCapture.cs`, `MapDocument/Storage/SC2MapshareTextures.cs` |
| Tabs | the Import main tab: Existing .scd, Terrain BDF, Custom Geometry, Collision Mesh |

## 3. Data

Import produces a live `SC2TerrainData` and a session document: terrain materials, pairs and heights (or an **imported terrain object** that stays a mesh and is merged at export), objects, the environment header and texture paths,
costs blocks, decals state (the original bdf kept as a blob), the scenario with `_save.lua` and `_script.lua` kept as passthrough blobs. An imported terrain object is recorded in the document as an entry (`ImportedEntries`) and carries its pivot, transform and optional
imported masks (`ImportedRnm`).

## 4. Flows

1. `OpenComposite` opens the chosen path and scanned siblings (a real install splits one map over `maps.scd`, `lua.scd`, `textures.scd`) as one view; `FindCandidates` lists every scenario in it.
2. `ImportSelected` extracts only the selected map's small files (bdf, lua; never textures) to a temporary folder, runs each domain importer in turn, and removes the folder. Collision2 is parsed for reference only; export always regenerates it from geometry.
3. Textures are not extracted eagerly: the terrain importer pulls the specific files a material references from the still-open reader.
4. A loose `terrain.bdf` import creates an imported terrain object with a pivot you can move; a loose decals file becomes a merged mesh.
5. On save, `SC2MapshareTextureCapture.Capture` stores the textures that live only on this machine (files imported from disk, textures an imported bdf names that the game archive lacks) in the map's own folder, so the map has them elsewhere.
6. The import panel reports each step and the diagnostics tab lists failures.

## 5. Invariants

- **The game files are never bundled or committed**; textures extracted from a game install go to ignored folders (README).
- **Export reads the real game archive to decide what to copy**: a vanilla texture is referenced by game path, a map-local one is copied ([export](ExportPipeline.md)).
- **Imported `_save.lua` and `_script.lua` are kept as passthrough** and only regenerated blocks replace their text.
- **No `UnityEditor` dependency** in the importers, so they run in a player.
- **Candidate choice is explicit**: a scenario is chosen by the user among those found, never guessed from the container's name.

## 6. Editor and build differences

- The file and folder pickers are the runtime dialogs in a player (`SC2RuntimeFileDialog`); the importers themselves are the same code.
- The older editor-only importers (`Editor/ScdImportWindow`, `SC2VanillaImportDiagnostics`) remain for the editor.

## 7. Extension points and how to change it

- **A new imported domain**: an importer that reads from the temporary folder or the reader, a call in `ImportSelected`, the matching document fields and bridges, a diagnostics line.
- **A new loose file import**: a function in the importer family and a button in the Import tab.

## 8. Editing, verification and tests

**How to edit it safely.** Do not extract textures eagerly; keep temporary files in a folder that is removed on every exit path. Test with an archive, a folder and a missing sibling.

**Tests today**: `MapImportServiceTests`, `ImportSessionBlobStoreTests`, `SourceContainerTests`, `ScdEntryPointResolverTests`, `MapshareTexturesTests`, `ImportedPivotTests`, `TestCorpus*` (fixtures). The orchestrator and panel have no unit test.

**Tests a change must add.** A new domain importer: a fixture from the corpus and the parsed result. A container rule: a case in `SourceContainerTests`. An end-to-end change: a scenario that imports a small corpus map and exports it.

## 9. Known limitations and unverified behaviour

- Editing support for existing (vanilla) maps is limited (README).
- The set of vanilla maps that import cleanly was surveyed in code comments (56 maps); not re-run for this document.

## 10. Related documents and review

[Textures](Textures.md), [Export pipeline](ExportPipeline.md), [Objects](Objects.md), [Decals](Decals.md), [Document and persistence](DocumentAndPersistence.md), the README.
Interactions with other systems: [Save, import, export](../Interactions/SaveImportExport.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
