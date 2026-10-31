# The standalone Info.bdf editor (`Tools~/InfoBdfEditor`)

Status: first draft from reading the project files at commit `17863e4`; not yet reviewed by a second reader. Priority P3. Read [Environment](Environment.md) first.

## 1. Purpose and boundaries

A separate Windows program that opens, edits and saves an `info.bdf` file outside the map editor, with its own window and taskbar icon. It exists so the environment of any map or file can be edited without opening that map in the editor. It does not talk to a running editor: values move between the two as a JSON file.

Not included: the in-editor environment tab ([environment](Environment.md)), which draws the same fields; the `info.bdf` layout ([Info format](../Formats/Info.md)).

## 2. Where the code is

All under `Assets/SC2/Tools~/InfoBdfEditor/` (the `~` hides it from Unity):

| Part | Files |
|---|---|
| Project | `SC2InfoBdfEditorApp.csproj` (`net9.0-windows`, WinExe, x64, unsafe code, `Tests~` excluded from the glob), `README.md`, `AppIcon.ico` |
| Window | `MainForm.cs` (a Win32 window whose whole client area is drawn by Dear ImGui over Direct3D 11), `Program.cs`, `Theme.cs` (the editor's dark palette), `AppSettings.cs` (`settings.json` next to the exe: window bounds and the last opened file; best effort) |
| UI | `InfoBdfEditorUi.cs` (1616 lines: tabs, fields, undo, import and export), `HelpDialog.cs`, `HelpText.cs`, `FieldDescriptions.json` (one explanation per field) |
| Shared source | compiled straight from the package: `Runtime/BdfIO/Core/**`, `Runtime/BdfIO/Formats/Info/**`, `Scripts/TerrainEditor/InfoFieldLimits.cs` (slider ranges), `Scripts/Core/InfoBdfJsonDto.cs` (the JSON shape) |
| Tests | `Tests~/InfoBdfEditorTests/` (`InfoBdfJsonDtoTests`, `InfoBdfRoundTripTests`, `InfoFieldLimitsTests`, `TextureFieldPathTests`, `UiRenderRobustnessTests`, `UndoRedoImportTests`) |
| Editor side | `Scripts/Unified/SC2InfoBdfEditorPanel.cs` (the in-editor overlay with "Open Info.bdf Editor (external)" and "Import JSON...") |

## 3. Data

The working data is an `InfoModel` (the 508-byte header and five texture paths). The tool's own state is the opened path, the undo stack and the window settings. The exchange format is `InfoBdfJsonDto`, a single class compiled into both programs so they cannot drift; it has public **fields**, because Unity's `JsonUtility` ignores properties, so the tool's `System.Text.Json` must be set to `IncludeFields = true`.
The editor presets of the older window (`InfoPreset`) store the whole header as base64 for an exact round trip.

## 4. Flows

1. In the editor, the Tools menu opens the external tool; in the tool, File > Open .bdf (or start from scratch), edit, File > Export for Unity (JSON).
2. Back in the editor, "Import JSON..." in the panel loads the exported file; the preview and "Save to Map" or "Save to File" then work as if the file had been opened directly.
3. The tool reads and writes real `.bdf` files itself with the same BdfIO code as the editor.

**Build**: `dotnet build` in the folder; **publish** with `dotnet publish -c Release -r win-x64 --self-contained`, giving a self-contained **folder**, which is shipped whole in an `InfoBdfEditor` folder next to the player's exe.

## 5. Invariants

- **Do not publish as a single file** (`PublishSingleFile`). The Dear ImGui native libraries (`cimgui.dll` and the backends) are then not found at start (`DllNotFoundException`, confirmed by two crash reports from deployed runs), because the native loader only looks next to the exe, not in the single-file extraction folder. Ship the whole publish folder.
- **The rendering backend is Win32 plus Direct3D 11.** An earlier GLFW plus OpenGL attempt crashed with a native access violation: each Dear ImGui backend DLL keeps its own context pointer, and `SetCurrentContext` has to be called explicitly for every backend module; `MainForm` documents the call order. Do not change the backend without redoing that.
- **No WinForms control is ever placed on the window**; the form exists only to get a window handle and run the message pump.
- **The tool edits exactly the layout the editor does**, because it compiles the same source files; do not copy source into the tool.
- **The JSON shape is shared source**, changed once for both sides.
- **There is no live link to a running editor**, by design.

## 6. Editor and build differences

The tool is not part of the editor or the player build. The editor's panel is a plain GUI overlay in both. The tool is Windows-only.

## 7. Extension points and how to change it

- **A new `info.bdf` field**: the accessor in `InfoModel` first ([Info format](../Formats/Info.md)), then a control in the shared field drawer for the editor and in `InfoBdfEditorUi.cs` for the tool, an entry in `FieldDescriptions.json`, and a limit in `InfoFieldLimits.cs` if it has a slider.
- **A new exchange field**: `InfoBdfJsonDto` (a public field with a default so older files still import) and both sides.

## 8. Editing, verification and tests

**How to edit it safely.** Build and publish the tool and run the published folder, not only the debug build, because the native-library failure shows only in a published copy.

**Tests today**: the six test classes above (`dotnet test` in `Tests~/InfoBdfEditorTests`). They share the vanilla corpus of the standalone suite and are skipped, not failed, when it is missing.

**Tests a change must add.** A field or shape change: a round-trip test of the JSON and of the bdf. A UI change: a case in `UiRenderRobustnessTests` or `UndoRedoImportTests`.

## 9. Known limitations and unverified behaviour

- **`SC2Root` in both tool projects points at the old copy of the repository** under the `Documents\Codex\...\outputs\...\Assets\SC2` folder, not at this repository (`SC2InfoBdfEditorApp.csproj` line 84, `InfoBdfEditorTests.csproj` line 37; the standalone suite's project points at the right folder). A build of the tool today compiles the shared source files from that old copy, so the tool may not contain recent changes to `InfoModel`, `InfoFieldLimits` or the JSON shape. The path is absolute and must be updated, as for the standalone suite.
- The tool was not built or run for this document.
- Behaviour on a non-Windows machine: unsupported.

## 10. Related documents and review

[Environment](Environment.md), [Info format](../Formats/Info.md), [BdfIO](BdfIO.md), [Tests and release](TestsAndRelease.md), [Project and build settings](ProjectAndBuildSettings.md).
Last reviewed: author, project files, commit `17863e4`. Second reader: not yet.
