# SC2 Unified Map Editor Shell

Menu entry:

```text
SC2 Map Editor > Open
```

This shell is the unified editor cabin for the SC2 map workflow. It is a standalone Unity editor app-style cockpit, not a subtab of the legacy `SC2/...` tool menu. It does not remove the older windows; it bridges to them while the individual panels are migrated.

Implemented in this stage:

- corrected top-level tabs;
- corrected Terrain subtabs without `Splatmap`;
- Costs subtabs including Terrain Types;
- editable Objects filter list;
- shared editor context;
- status bar;
- overlay toggles;
- Diagnostics tab placeholder;
- SceneView bridge;
- Unity-like viewport navigation;
- viewport selection and transform handles.

Scene navigation:

- `Alt + LMB`: orbit;
- `MMB` or `Alt + RMB`: pan;
- `RMB + WASD/QE`: fly;
- mouse wheel: zoom;
- `F`: focus selected object or map root;
- standard Unity SceneView navigation remains available.

The shell intentionally keeps old windows available through bridge buttons:

- `SC2 > Import .scd...`;
- `SC2 > Export .scd...`;
- `SC2 > Terrain Editor`;
- `SC2 > Costs Painter (3D)`;
- `SC2 > Edit Environment (info.bdf)`.

Game View UI:

- `SC2 Map Editor > Open` automatically creates/shows the Game View overlay.
- `SC2 Map Editor > Show Game View UI` creates only the in-scene overlay.
- The overlay is an `ExecuteAlways` component, so it is visible in the Game tab without entering Play mode.

## Build & Run parity

`SC2GameViewEditorUI` (and everything it drives — `SC2TerrainShell` and the
`TerrainEditor` sub-editors) lives under `Scripts/`, not `Editor/`, so it is
compiled into standalone players and works the same way in a `Build And Run`
executable as it does in the Editor's Game tab. Everything under `Editor/`
(the `SC2MapEditorWindow` cockpit, camera navigator, SceneView bridge, bootstrap
menu items, etc.) is Editor-only by design and is stripped from builds — it
exists to configure the scene before you build, not to run inside the build.

Requirements for a build to show the same UI as the Editor Game view:

1. Use `SC2 Map Editor > Show Game View UI` (or `Open`) at least once and
   **save the scene**. The component is a normal serialized `MonoBehaviour`,
   so it travels into the build with the scene — there is no separate runtime
   bootstrap step.
2. `UnityEditorRuntimeShim.cs` provides drop-in replacements for the
   `UnityEditor` IMGUI types (`EditorGUILayout`, `EditorStyles`, `Undo`,
   `Selection`, etc.) so the same drawing code compiles and runs outside the
   Editor. Guard anything that has no runtime equivalent with
   `#if UNITY_EDITOR` / `#else` (see `SC2TerrainShell.DrawTexturePicker` for
   the pattern) rather than adding new unguarded `UnityEditor.*` calls to any
   script under `Scripts/`.
3. Two APIs the Editor provides that do not exist in a player, and how this
   project handles them:
   - **Object/texture picking** (`EditorGUIUtility.ShowObjectPicker`) — no
     Project browser exists at runtime. `SC2TerrainShell` swaps in a small
     in-GUI popup (`DrawRuntimeTexturePickerOverlay`) outside the Editor,
     listing any `Texture2D` already referenced on the loaded data plus
     anything found via `Resources.LoadAll<Texture2D>`. Ship browsable
     textures under a `Resources` folder if you want them selectable in a
     build.
   - **Writing/creating project assets** (`AssetDatabase.CreateAsset`,
     `EditorUtility.SaveFilePanelInProject`, `.scd` export folder browsing)
     — there is no `Assets` folder in a build. These paths already fall back
     to in-memory `ScriptableObject` instances and `Application.persistentDataPath`
     outside the Editor; keep that split when extending export/import panels.
