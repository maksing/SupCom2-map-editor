# The map document: domains, commands, validators and the session

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P1. This is the catalogue companion of [Document and persistence](DocumentAndPersistence.md), which explains how the document is saved, undone and backed up. Read that first.

## 1. Purpose and boundaries

`Scripts/MapDocument` is the pure-data model of a map and the machinery around it. This page lists **every domain document, its bridge to the live data, its validator and its commands**, the validation codes, the session layer, the import service contract, the texture-browser model and the small shared types. Use it to find where a piece of saved data lives and which commands may change it.

Not included: the blob store, the manifest serializer, migrations and autosave ([document and persistence](DocumentAndPersistence.md)); the live data ([terrain data and runtime](TerrainDataAndRuntime.md)).

## 2. Where the code is

`Assets/SC2/Scripts/MapDocument/` (in the default assembly; the pure files are compiled directly into the standalone test project):

| Folder | Content |
|---|---|
| `Domain/` | the root `SC2MapDocument` and one sub-folder per domain, plus shared types |
| `Commands/` | `ISC2DocumentCommand`, `SC2CommandHistory`, `SC2CompositeCommand`, `SC2ShellUndoCoordinator` |
| `Changes/` | `SC2DocumentChangeSet` (what a change touched, with `DirectDirtyFlags`) |
| `Session/` | `SC2MapSession` and its parts |
| `Storage/` | blob stores, serializer, migrations, `SC2MapshareTextures`, `SC2Json` |
| `Import/` | the two-phase import service contract and its result types |
| `TextureBrowser/` | the pure model, query engine, sibling matcher and virtual grid of the texture browser |

## 3. Data

### The root

`SC2MapDocument` holds a `DocumentId`, `FormatVersion` (1), a `Revision` that rises with every change, and the sub-documents: `Identity`, `Source`, `EntryPoint`, `Archive`, `Environment`, `Collision2`, `Costs`, `Hfield`, `Objects`, `Terrain`, `Scenario`, `Decals`. `RaiseChanged(changeSet)` bumps the revision and notifies. The mission graph lives inside the scenario document.
A domain called "Assets" was planned and is **not** in the root.

### The domains

| Domain | Document | Legacy bridge | Validator | Commands |
|---|---|---|---|---|
| Terrain | `SC2TerrainDocument` (materials `SC2MaterialDocument`, pairs, `SC2TerrainGrid` heights as a blob, `SC2TrianglePairMap`, `SC2TerrainLayerDocument`, `SC2TerrainSettings`, `SC2TextureReference`, `SC2BlendDocument`, `SC2RnmDocument` named mask blobs, `SC2ImportedRnmMasks`, `SC2ImportedTerrainEntryDocument`) | `SC2TerrainLegacyBridge` | `SC2TerrainValidator` | `SC2SetMaterialNameCommand`, `SC2SetMaterialPairLinkCommand`, `SC2SetMaterialPairNameCommand`, `SC2SetMaterialTextureCommand`, `SC2SetTerrainHeightsCommand`, `SC2SetTrianglePairAssignmentsCommand` |
| Hfield | `SC2HfieldDocument` | `SC2HfieldLegacyBridge` | `SC2HfieldValidator` | `SC2SetHfieldTerrainTypesCommand` |
| Costs | `SC2CostsDocument` | `SC2CostsLegacyBridge` | `SC2CostsValidator` | `SC2SetCostsLayerGridCommand` |
| Collision2 | `SC2CollisionDocument` | `SC2CollisionLegacyBridge` | `SC2CollisionValidator` | `SC2SetCollision2ExtentsCommand`, `...PolygonBudgetCommand`, `...VerticalOffsetCommand` |
| Objects | `SC2ObjectsDocument` (props, markers, graphs, armies, units, zones, chains, symmetry groups, reference units, original Lua texts) | `SC2ObjectsLegacyBridge` | `SC2ObjectsValidator`, `SC2ArmySymmetryValidator`, `SC2MassSymmetryValidator` | `SC2SetArmyNameCommand`, `SC2SetChainNameCommand`, `SC2SetGraphNameCommand`, `SC2SetMarkerPositionCommand`, `SC2SetPropPositionCommand`, `SC2SetSymmetryGroupKindCommand`, `SC2SetUnitPositionCommand`, `SC2SetZoneRectCommand` |
| Environment | `SC2EnvironmentDocument` (working header, `SC2EnvironmentTextureExport`) | `SC2EnvironmentLegacyBridge` | `SC2EnvironmentValidator` | `SC2SetEnvironmentHeaderCommand`, `SC2SetEnvironmentTexturePathCommand`, `SC2SetEnvironmentTextureExportCommand` |
| Scenario | `SC2ScenarioDocument` (known fields, `SC2LobbyStartPosition`, the mission) | `SC2ScenarioLegacyBridge` | `SC2ScenarioValidator` | `SC2SetScenarioNameCommand`, `SC2SetLobbyStartPositionsCommand` |
| Decals | `SC2DecalsDocument` | `SC2DecalsLegacyBridge` | `SC2DecalsValidator` | `SC2ReplaceDecalsStateCommand`, `SC2SetDecalsAtlasPathCommand` |
| Mission (inside Scenario; uncommitted work) | `SC2MissionDocument` | none | `SC2MissionValidator`, `SC2LuaSyntaxCheck` | `SC2ReplaceMissionCommand` |

Each bridge has a `...ScriptableObject` partial that does the work against the live `SC2TerrainData` (kept apart so the document layer stays free of Unity objects). The whole-document pair is `SC2MapDocumentToLegacyBridge` (`BuildFromLegacy`, `ApplyToLegacy`).

**Every command declares `DirectDirtyFlags`**, the export domains it touches (Collision2, Costs, Decals, Info, HfieldTerrainTypes, Objects, ScenarioLua, ScriptLua), which feeds `SC2MapSession.PendingDirtyFlags` ([smart export](SmartExport.md)).

### Derived-state markers

`SC2DerivedState` (`Current`, `Dirty`, `Stale`) is shared by the derived domains (Hfield, Costs, Collision2, Scenario, Decals). **Dirty** means the user changed the domain directly; **Stale** means a source it depends on changed and it has not been regenerated. They are kept distinct on purpose: stale is a decision for the user. `MarkExported` resets both.

### Shared and small types

| Type | Role |
|---|---|
| `SC2MapIdentity` | a stable internal `MapId` independent of the file name; renaming a map is a separate transaction not implemented yet |
| `SC2MapSource` (`SC2MapSourceKind`: NewProcedural, ScdArchiveFile, ScdDirectory, TerrainBdf, BlenderTerrainBdf, RecoveredAutosave) | where the map came from, with a fingerprint: SHA-256 of an archive file, or a stable hash over the sorted (normalised path, length, content SHA-256) of a directory; computed once at import, never implicitly again, so a re-import can detect a changed source |
| `SC2MapEntryPoint` | the chosen `scenario.lua` and the map, save, script and bdf paths resolved from it; no separate byte-order field because `BdfPlatform` already says it |
| `SC2ArchiveDocument` (`SC2ArchiveEntry`, `SC2ArchiveEntryRole`: ScenarioLua, SaveLua, ScriptLua, the seven bdf kinds, MapTexture, CustomAsset, ForeignMapEntry) | every entry of the source container with its hash and an immutable blob of its original bytes, so unknown files are preserved verbatim |
| `SC2ScenarioCandidate` | one `scenario.lua` found by a probe; exactly one is ever imported, never "the first found" |
| `SC2MeshBlobReference` | a mesh as two blobs (vertices, indices) plus counts; used by the collision document |
| `SC2Hex` | hex string of a hash; `Convert.ToHexString` is not available at this project's API level (found by a real compile error) |
| `SC2SafePath` | `NormalizeRelativeOrThrow` (forward slashes, no leading slash; **throws** on `..`, a drive prefix or a UNC path; `.` segments are allowed) and `ResolveContainedOrThrow` (resolves against a root and verifies the full path stays inside it, which also catches symlink and junction escapes). Written to close two zip-slip findings: an unzip that joined a temp dir to a zip entry name, and `SC2ExportFileState.NormalizeRelativePath` which strips slashes but never rejects `..` |
| `SC2MapShotFrameMask` | the alpha frame every vanilla `_PC_mapshot.dds` carries: a 7-pixel quadratic feather, three softly rounded corners and a diagonally cut bottom-left corner, extracted pixel-for-pixel from `SC2_MP_001_PC_mapshot.dds` (448 x 206) |
| `SC2GameInstall`, `SC2SourceContainer` | see [textures](Textures.md) and [import pipeline](ImportPipeline.md) |

### Validation

`SC2ValidationReport` collects entries of severity `Info`, `Warning` or `Error` with a code, a domain, an object id, a message and a suggested fix.

| Code | Severity | Meaning |
|---|---|---|
| `COL001` | Error | collision polygon budget above 10 000 |
| `COST001` | Error | duplicate Costs layer id |
| `COST002` | Error | a Costs layer has no grid data |
| `DEC001` | Warning | decal geometry but the diffuse atlas path is empty |
| `DEC002` | Error | decal atlas dimensions invalid |
| `DEC003` | Error | a decal catalog entry has a missing or duplicate id |
| `DEC004` | Error | an atlas rectangle is outside the atlas or has zero size |
| `DEC005` | Error | a placement references a missing catalog entry |
| `DEC006` | Error | a placement has non-positive width, length or projection depth |
| `DEC007` | Error | edited decals need regenerated spatial data |
| `ENV001` | Warning | environment map path empty |
| `ENV002` | Warning | sky map path empty |
| `HFD001` | Error | terrain types have no data |
| `OBJ001` | Error | duplicate object id (ids are unique across props, markers, graphs, armies, units, zones, chains and symmetry groups) |
| `OBJ002` | Warning | a chain refers to a marker name that does not exist |
| `OBJ003` | Warning | a zone has a degenerate rectangle |
| `OBJ004` | Error | duplicate marker name (names are the keys in `_save.lua`) |
| `OBJ005` | Error | a marker duplicates another at the same position with the same type and prop |
| `OBJ006` | Warning | a Mass marker has no symmetric twin for the map's symmetry (`SC2MassSymmetryValidator`) |
| `OBJ007` | Warning | an army start has no symmetric twin (`SC2ArmySymmetryValidator`) |
| `SCN001` | Warning | scenario name empty |
| `SCN002`, `SCN003`, `SCN004` | Error | the `.scmap`, `_save.lua` or `_script.lua` path is empty |
| `TER001` | Error | duplicate material id |
| `TER002`, `TER003` | Error | a pair refers to a missing first or second material |

### The session

`SC2MapSession` (`Document`, `History`, `BlobStore`, `PendingDirtyFlags`, `MarkExported`) is the one object that pairs a document with its undo history and storage. Its parts `SC2WorkspaceState` (active tab, overlay toggles, camera pivot), `SC2SelectionState` (selection by stable object id and a kind string),
`SC2SceneProjection` (what renders the document in the viewport) and `SC2OperationState` (progress of the one long-running operation at a time) exist as named **seams: the first three are not written or read by the editor yet** (the legacy fields of the host and shells are authoritative); `SC2OperationState` receives reports of `SC2OperationProgress`.

### The import service contract

`ISC2MapImportService` is two-phase on purpose: `ProbeScdAsync` imports nothing and returns an `SC2ContainerProbeResult` (source path, container kind, the candidate list, **no open reader**, so import must reopen the source and recheck its fingerprint); `ImportSelectedScenarioAsync` needs an explicit selected scenario entry and returns an
`SC2MapImportResult` (a document and diagnostics, not a scene). `SC2ImportOptions` is intentionally empty. `SC2MapImportService` implements it, but **no editor code calls it today** (a search finds only the interface, the class, comments and the tests); the editor's Import Map panel runs `SC2MapImportOrchestrator` instead ([import pipeline](ImportPipeline.md)).

### The texture-browser model

Pure, Unity-free parts so the logic is tested without Unity: `SC2TextureCatalogModel` (`SC2TextureSource`: Vanilla, Map, Custom, Generated; `SC2TextureUsage`; the entry type), `SC2TextureCatalogQueryEngine` (search with a cache keyed by version), `SC2TextureSiblingMatcher` (the core of the sibling suggestion; the catalog's `TryFindSiblingMatch`
and `FindAllSiblingMatches` wrap it), `SC2TextureVirtualGrid` (which rows of a large grid are visible). See [Textures](Textures.md).

## 4. Flows

A shell edit becomes an `ISC2DocumentCommand`, is pushed into `SC2CommandHistory`, mutates the domain document and the live data through the bridge, raises `Changed` with a change set carrying `DirectDirtyFlags`, and the session accumulates the flags. `SC2CompositeCommand` groups several commands as one undo step. Saving serializes the document, the blob store holds the heavy data
([document and persistence](DocumentAndPersistence.md)).

## 5. Invariants

- **Identity by stable id, never by list index** (the pair map, the selection, graph references): an index reused as identity silently repoints unrelated items after a deletion or reorder.
- **Heavy data is a blob reference**, not inline: heights, masks, meshes, original file bytes, so the manifest stays small.
- **Every command declares the dirty flags it raises.** A command that omits them hides a change from the export planning.
- **An object id is unique across all object kinds** (`OBJ001`).
- **A path from a container or a map's data goes through `SC2SafePath`** before it is joined to a folder.
- **Probe and import are separate**; the import re-checks the fingerprint taken at the probe.
- **The session seams are not authoritative yet.** Do not read selection or workspace state from them.

## 6. Editor and build differences

None in this layer (no editor API). The `ScriptableObject` bridge partials need Unity but run in a player too.

## 7. Extension points and how to change it

- **A new saved field**: the domain document class, the bridge in both directions, the serializer default (and a migration if the meaning changed), a command if it must undo, validator rules, a round-trip test. An old save without the field must load.
- **A new domain**: a document class added to `SC2MapDocument`, bridge, validator, commands with `DirectDirtyFlags`, a derived-state entry in the session, and the export file kind and masks in [smart export](SmartExport.md).
- **A new validation rule**: a code with the next free number of its prefix, a message and a fix text, and a test.

## 8. Editing, verification and tests

**How to edit it safely.** Keep the document free of Unity types. Never add a field to the legacy data only. Register every new command with its flags.

**Tests today**: per domain, `*CommandTests`, `*DocumentBridgeTests` (terrain, costs, hfield, collision, objects, scenario, decals, environment), `MapDocumentSerializerTests`, `MapDocumentToLegacyBridgeTests`, `CommandHistoryTests`, `CompositeCommandTests`, `SessionTests`, `ShellUndoCoordinatorTests`, `SafePathTests`, `BlobStoreTests`, `BlobStoreGarbageCollectorTests`,
`MapImportServiceTests`, `ImportSessionBlobStoreTests`, `SourceContainerTests`, `TextureCatalogTests`, `TextureBrowserFilterTests`, `TextureSiblingMatchTests`, `MapShotFrameMaskTests`.

**Tests a change must add.** A new field: a bridge round trip and a serializer round trip with an old-format file. A new command: do, undo and redo restore the data, and its dirty flags. A validator rule: one passing and one failing document.

## 9. Known limitations and unverified behaviour

- The Assets domain was planned and not built; the workspace, selection and projection seams are unused.
- `SC2ImportOptions` has no options.
- Terrain brush sculpting and painting bypass the command layer and call `SC2MapSession.MarkDirty` instead; see [smart export](SmartExport.md) and [Interactions](../Interactions/README.md).
- Several class comments in this folder describe an earlier stage ("nothing calls this outside tests", "read-only snapshot"); some are out of date. The code, not the comment, is the reference.

## 10. Related documents and review

[Document and persistence](DocumentAndPersistence.md), [Smart export](SmartExport.md), [Import pipeline](ImportPipeline.md), [Terrain data and runtime](TerrainDataAndRuntime.md), [Objects](Objects.md), [Costs](Costs.md), [Collision2](Collision2.md), [Decals](Decals.md), [Environment](Environment.md), [Scenario and mission](ScenarioAndMission.md).
Interactions with other systems: [Save, import, export](../Interactions/SaveImportExport.md), [Mechanisms](../Interactions/Mechanisms.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
