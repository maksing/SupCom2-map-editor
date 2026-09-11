# SupCom2 Map Editor

A Unity-based map editor for **Supreme Commander 2**, built around the game's real `.scd`/`.bdf` file formats. It reads assets directly from your own installed copy of the game rather than shipping any copyrighted game data — this repository contains only editor code.

## For map makers

This editor ships as a standalone Windows executable (a Unity "Build & Run" output) — you don't need Unity or any development tools to use it.

### Getting started

1. Run the built `.exe`.
2. On first launch, the startup screen asks you to point it at your Supreme Commander 2 `gamedata` folder.
3. Create a new map from the startup screen's map list. Opening an existing/vanilla map is also possible, but editing support for existing maps is limited.

### Features

- **Terrain**: procedural heightfield sculpting/painting, material pairs, RNM lighting bake, simplification, water, and import of real vanilla `terrain.bdf` geometry.
- **Objects**: props, markers, unit placement, pathing graphs, zones/areas, with brush tools and symmetry support.
- **Decals, Costs (movement types), Collision2**: dedicated painting/editing tabs mirroring the game's own data layers.
- **Environment/Scenario**: info.bdf field editing (sun/fog/water/environment maps) and scenario/save Lua round-tripping.
- **Texture browser**: browses your own game install's archive live, plus a user-imported custom texture library. Importing a vanilla map also copies its referenced textures onto local disk — see [Working with game textures](#working-with-game-textures) below.
- **Import/Export**: reads real vanilla `.scd` archives and writes real `terrain.bdf`/`info.bdf`/`mapobjs.bdf`/`costs.bdf`/`collision2.bdf`/`hfield.bdf` output compatible with the game.

## For developers

### Requirements

- Unity **6000.0.77f1** (see `ProjectSettings/ProjectVersion.txt`).
- A local installation (or extracted copy) of Supreme Commander 2's `gamedata` — the editor points at this at first launch and never bundles it.
- .NET SDK (for the embedded standalone sub-projects under `Assets/SC2/Tools~` and `Assets/SC2/Tests~` — see below).

### Building from source

1. Open the project folder in Unity Hub with editor version `6000.0.77f1`.
2. `File → Build Settings`, target Windows Standalone, and Build (or Build And Run).

### Project layout

- `Assets/SC2/Scripts/Core` — terrain/material/object data model.
- `Assets/SC2/Scripts/TerrainEditor` — terrain sculpting, painting, RNM lighting bake, background terrain.
- `Assets/SC2/Scripts/MapDocument` — the document/command/undo architecture used for map persistence.
- `Assets/SC2/Scripts/TextureBrowser` — the live archive-scanning texture picker.
- `Assets/SC2/Scripts/Unified` — the unified in-Game-View editor UI (all tabs/shells).
- `Assets/SC2/Runtime/BdfIO` — standalone binary readers/writers for the game's own `.bdf` formats.
- `Assets/SC2/Runtime/KopiLua` — a vendored, patched copy of the third-party [KopiLua](https://github.com/Myndale/KopiLua) library, used to read the game's compiled `.lua` scenario/save files — see [Third-party components](#third-party-components) below.
- `Assets/SC2/Tools~/InfoBdfEditor` — a separate standalone .NET tool for inspecting/editing `info.bdf` files directly (outside Unity). Build with `dotnet build` from that folder.
- `Assets/SC2/Tests`, `Assets/SC2/Tests~` — Unity EditMode tests and a standalone (non-Unity) characterization test suite (`dotnet test`).

### Working with game textures

Importing a vanilla map (or any `.scd`) copies the textures it references out of your `gamedata` install into local, loose files under `Assets/SC2Textures/`, `Assets/SC2/Resources/SC2Textures/`, and `Assets/StreamingAssets/SC2Textures/` so the Editor and a Build can both load them. These are the game's own copyrighted textures, not editor assets — all three paths are gitignored and must never be committed.

### Third-party components

- **[KopiLua](https://github.com/Myndale/KopiLua)** (`Assets/SC2/Runtime/KopiLua`) — a line-for-line C# port of the Lua 5.1 reference implementation. MIT-licensed; see [`COPYRIGHT`](Assets/SC2/Runtime/KopiLua/COPYRIGHT).
- **Lua** is MIT-licensed

## License

MIT — see [LICENSE](LICENSE).
