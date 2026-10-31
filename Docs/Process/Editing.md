# Editing

How to change the code of the editor without breaking it. Read it before the first change. A change of behaviour needs a test: see
[Testing](Testing.md); how to check a change beyond the tests: [Verification](Verification.md). The system documents (planned, see
`Docs/DocumentationPlan.md`) give the details of each system; this page holds what is true for all of them.

## 1. Before you change anything

- Find the system. The project layout is in the README; `Docs/Systems/<System>.md` lists the files of each system and its
  invariants (the index is `Docs/README.md`). Read the invariants before the code.
- Find what depends on it: [Change impact](../Interactions/ChangeImpact.md) lists, per kind of change, the other systems to check, and the
  pages of `Docs/Interactions/` explain the contracts (shared data, events, counters, order of operations). A change that touches a row
  and does not handle its checks is probably unfinished.
- Know the assembly boundary, because it decides where a testable rule can live:
  - in assemblies: `Assets/SC2/Scripts/Core` (`SC2.Core`), `Runtime/BdfIO` (`BdfIO.Core`, `BdfIO.Formats`), `Runtime/SmartExport`,
    `Runtime/GameAssets`, `Scripts/GameAssets`, vendored `Runtime/KopiLua`;
  - in the default assembly (not reachable from EditMode tests): `Scripts/Unified`, `Scripts/TerrainEditor`, `Scripts/TextureBrowser` and the
    files directly in `Scripts`.
  A rule you want to unit test goes into `Scripts/Core` as a plain static class; the UI calls it.
- Find the tests that already cover the code and run them first, so you know the starting state ([Testing](Testing.md), "Known baseline").

## 2. The rules the project keeps

**Code and comments**
- Comments are in English. A comment states the durable reason, what must stay true and why. It carries no date, no name of a work item,
  no quotation of a request and no history of how the change came about.
- Match the surrounding code: comment density, naming, idiom.
- Keep the third-party code (`Runtime/KopiLua`) as it is; it has its own README and licence.

**Commits**
- One change per commit; the first line says what changed, the body says why.
- No attribution trailers (`Co-Authored-By` and the like) on commits or pull requests.
- Stage explicit paths, never everything at once. When a file also holds edits that are not yours, stage only your hunks.
- Do not commit: the game's textures (`Assets/SC2Textures`, `Assets/SC2/Resources/SC2Textures`, `Assets/StreamingAssets/SC2Textures` are
  ignored by the repository, because they are the game's copyrighted files), and the files Unity rewrites as the editor runs
  (`Assets/SC2/Resources/SC2TerrainData.asset`, `Assets/SC2/Resources/SC2TextureCatalog.json`) unless the change is about them.
- A new asset file needs its `.meta` file with a new random 32-digit hexadecimal guid. Never copy the guid of another file.

**User data**
- The editor's save folder is `Application.persistentDataPath/SC2Terrains`; with the project's settings (company `BDFTM`, product
  `SupCom2 map editor`) that is `%USERPROFILE%\AppData\LocalLow\BDFTM\SupCom2 map editor\SC2Terrains`. It holds the maps, their
  backups and, through `PlayerPrefs`, the settings of whoever runs the build. Do not edit it by hand, and do not let a test or a
  scenario write there (see [Verification](Verification.md), "Isolation").

## 3. The editor and a player build differ

The same code runs in the Unity editor and in a standalone player, and a good part of it takes a different path in each.

- `UnityEditorRuntimeShim.cs` supplies stand-ins for editor-only APIs in a player (`#if !UNITY_EDITOR`). Among them
  `EditorUtility.DisplayDialog` **always answers yes** and shows nothing, and `RevealInFinder` does nothing.
- A question the user must be able to cancel therefore uses the runtime confirm in a build:

  ```csharp
  #if UNITY_EDITOR
      if (EditorUtility.DisplayDialog(title, message, "Switch", "Cancel")) Apply();
  #else
      SC2RuntimeConfirm.Open(title, message, "Switch", Apply);   // asks later, runs Apply when the user agrees
  #endif
  ```

  (see `SC2CostsShell.ApplyPlayableWaterToggle`, `SC2GameViewEditorUI.RequestNewTerrain`). The runtime confirm is asynchronous: code
  after `Open` runs before the answer.
- Saving, loading, backups and the list of saved maps in a player live in `SC2RuntimeTerrainPersistence` and `SC2SaveStorage`, compiled
  only in a player. They cannot be exercised by an editor test; they need a player scenario.
- Dropdown lists in a player are `SC2RuntimeDropdown` overlays: a chosen item arrives one frame later through `TryConsumeLabel`.
- After editing a file, compile both: the editor, and a player build. A player build catches errors in `#else` branches the editor never
  compiles.

## 4. Changing saved data

A field that is saved passes through several places; a field missing in one of them is lost silently, or breaks older saves.

1. The data class (for terrain data, `SC2TerrainData` in `Scripts/Core`) and, for the document, its `SC2*Document` class.
2. `SC2MapDocumentSerializer` (`MapDocument/Storage`): write the field, and read it **with a default**, so a save made before the field
   existed loads. A field is added without raising the format version unless the old reading would be wrong; a real format change needs
   a migration (`ISC2DocumentMigration`).
3. Both bridges: `SC2TerrainLegacyBridge(.ScriptableObject)` (live data to document) and `SC2MapDocumentToLegacyBridge(.ScriptableObject)`
   (document to live data).
4. The undo snapshot of the live data (`SC2TerrainUndoStack`, with `SC2TerrainCombinedUndo` for edits that touch two surfaces). Without it
   an undo drops the field. A restored snapshot replaces objects, so references held by identity break (see `SC2SharedMaterials.Sync`).
5. The legacy JSON copy in `SC2RuntimeTerrainPersistence` (`Dto`, `ToDto`, `FromDto`), written while `SC2SaveSettings.WriteLegacyJson` is on
   (it is on by default).

Test: [Testing](Testing.md), "What every kind of change must test".

## 5. Changing the export

- Nothing the game cannot resolve may reach a `.bdf`: no `archive:` or `file:` marker, no drive letter path, no UNC path, also not behind a
  leading slash. `SC2TexturePathSafety.IsUnexportable` is the check; terrain materials get a last sweep before the file is written, and the
  five environment textures of `info.bdf` are checked the same way (`SC2TerrainExporter`).
- A texture reaches a material slot through `RegTex` / `ResolveRawResourcePath` in `SC2TerrainExporter`, which copies the file into the map or
  writes the game path of a vanilla texture (`SC2VanillaArchiveClassifier`). A new slot goes through the same resolver.
- The mask files of a mask group are named after the group, and the background's get a `bg_` prefix: two groups of the same name on the two
  surfaces must never share a file.
- The exporter runs on a worker thread behind the busy overlay; textures and the game archive may only be touched on the main thread (see
  `SC2BusyOverlay.OnMain` in the exporter).

Test: write the map, read the files back, and scan every file for editor markers and local paths ([Verification](Verification.md)).

## 6. Checklists for common additions

Only additions that have been checked against the code are listed. The system documents will add the others.

**A new hotkey**
1. Add the action to `SC2HotkeyActions.All` with its default binding(s). The `Id` is the key of the saved bindings and is never renamed
   afterwards.
2. Call sites ask `SC2Hotkeys.Matches`, `GetKey` or `MatchesMouseDown` with that id. The rebind panel lists the action and offers a second
   slot by itself.
3. A saved binding set overrides the defaults of its action, so a changed default reaches only whoever never changed the action.

**A new marker type** (`SC2MarkerTemplates`)
1. Add the template (name, prop, colour, icon key, `Hint`, `Graph` where it is a path node).
2. A path node type also goes into `ConnectablePathNodeTypes` in `SC2ObjectsShell`, and the path grid maps a graph kind to it
   (`PathNodeTypeFor`).
3. Give it an icon resource (`Resources/EditorIcons/Markers/<icon key>`) and a tile in the marker tile list of `SC2ObjectsShell` (the
   entries with `TemplateNames`), where the Markers tab shows it.

**A new pair or material option**
1. The field on `SC2MaterialPair` or `SC2Material`; its document and bridges (section 4).
2. The Pairs or Materials panel (`SC2MaterialsPairsMode`).
3. The exporter, where it becomes a material flag or a texture slot (section 5), and the live preview (`SC2TerrainShell` builds the pair
   data for the runtime).
4. The core and the background share one material list (`SC2SharedMaterials`): a material is the same object on both surfaces.

## 7. After the change

Run the checks of [Verification](Verification.md), in order. Then the pull request checklist (`.github/pull_request_template.md`).
