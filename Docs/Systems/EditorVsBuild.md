# The editor and a player build: what differs

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P1: the user runs the player build, so an assumption that only
holds in the Unity editor ships as a bug. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

The same source files are compiled for the Unity editor and for a standalone Windows player, with `#if UNITY_EDITOR` branches and a file of stand-ins for editor-only APIs. This
document lists what differs and what to do about it. It does not describe the features themselves.

## 2. Where the code is

| Part | Files |
|---|---|
| Stand-ins for editor APIs | `Scripts/UnityEditorRuntimeShim.cs` (namespace `UnityEditor` for a player: `EditorGUILayout`, `EditorUtility`, `Undo`, `AssetDatabase`, `MenuItem`, ...) |
| Runtime replacements | `Scripts/SC2RuntimePopups.cs` (`SC2RuntimeDropdown`, `SC2RuntimeColorPalette`, `SC2RuntimeConfirm`), `SC2RuntimeFileDialog.cs` (native open/save/folder dialogs), `SC2RuntimeObjectPicker.cs`, `SC2RuntimeQuitConfirm.cs`, `SC2RuntimeWindow.cs`, `SC2RuntimeTextureExporter.cs` |
| Build-only persistence | `Scripts/Unified/SC2RuntimeTerrainPersistence.cs` (`#if !UNITY_EDITOR`) |
| Editor-only tools | `Assets/SC2/Editor/` (windows and importers; they never compile into a player) |

## 3. What differs

| Area | Editor | Player |
|---|---|---|
| `EditorUtility.DisplayDialog` | a real dialog | **always answers yes, shows nothing**; the cancelable question is `SC2RuntimeConfirm.Open(title, message, label, callback)`, asynchronous |
| `UnityEditor.Undo` | works | empty methods; the editor's own snapshot and command stacks do the work |
| Save, load, backups, restore, delete | asset database | `SC2RuntimeTerrainPersistence`, files in `persistentDataPath/SC2Terrains` |
| File pickers | editor panels | Win32 common dialogs (`SC2RuntimeFileDialog`); other platforms return null |
| Dropdown lists, colour fields, object picker | editor controls | single-button triggers with floating overlays drawn last (`SC2RuntimeDropdown`...), a chosen item arrives one frame later |
| Texture bytes for export | `AssetDatabase` source files | re-encoded from live pixels (`SC2RuntimeTextureExporter`) |
| Window | the editor's | native title bar, maximize and fullscreen handled by `SC2RuntimeWindow` |
| Closing | the editor's | `wantsToQuit` runs the "save changes?" question (`SC2RuntimeQuitConfirm`, a native message box) |
| Shaders | referenced anywhere | must be reachable from `Resources` or a material, or they are stripped from the build |
| GPU instancing (props, units) | works | broken if instancing variants are stripped by the graphics settings |
| Save folder identity | the project's company and product | the same names; with the project's settings this is the user's real data folder |
| `EditorGUILayout`, `EditorStyles` | the inspector skin | bare stand-ins: do not use them in shared code |

## 4. Flows

- A shared shell draws only with `GUI` and `GUILayout`, so it looks and behaves the same in both.
- A dialog flows: ask (`#if UNITY_EDITOR` `DisplayDialog`, `#else` `SC2RuntimeConfirm.Open`), then continue in the callback, one or more frames later.
- A dropdown flows: `Toggle(id, options, anchor)` opens it, the overlay is drawn at the end of the frame, `TryConsumeLabel(id, out label)` returns the pick on a later frame.

## 5. Invariants

- **Never rely on `EditorUtility.DisplayDialog` to ask a question** in code a player runs.
- **Code after `SC2RuntimeConfirm.Open` runs before the answer.**
- **A rule that exists only for the player (`#if !UNITY_EDITOR`) cannot be exercised by an editor test**; it needs a player scenario.
- **A new shader is placed in `Resources`** or referenced by a material kept in the build, and is checked in a player.
- **A symptom "works in the editor game view, invisible in the player" for content drawn with `Graphics.DrawMesh` or `DrawMeshInstanced` from runtime-only materials is a variant-stripping problem until proven otherwise.** The player has no compiled `INSTANCING_ON` variant when no asset references it, the draw call fires with correct data and draws nothing, and no log line says so. `ProjectSettings/GraphicsSettings.asset` has `m_InstancingStripping: 2` (Keep All) for this reason; check it, and `m_LightmapStripping` and `m_FogStripping`, before shader resolution, mesh data, cameras or lighting ([project and build settings](ProjectAndBuildSettings.md)).
- **Do not use `EditorGUILayout`/`EditorStyles` in shared code**: they resolve to different looks in the two builds.
- **Paths**: `Application.persistentDataPath`, `temporaryCachePath` and `dataPath` differ between the editor and a player; derive, never hard-code.

## 6. Editor and build differences

This document is about them; see section 3.

## 7. Extension points and how to change it

- **A new editor API used in shared code**: add a stand-in to `UnityEditorRuntimeShim` (inside `#if !UNITY_EDITOR`), or use a plain `GUI` control instead.
- **A new question to the user**: copy the pattern of `SC2CostsShell.ApplyPlayableWaterToggle` or `SC2GameViewEditorUI.RequestNewTerrain`.
- **A new build-only feature**: put it behind `#if !UNITY_EDITOR` and add a player scenario for it; mention the difference in its system document.

## 8. Editing, verification and tests

**How to edit it safely.** After any change to a file with `#if` blocks, compile both targets. Compare the behaviour of a dialog, a dropdown and a save in a player, not only in the editor.

**Tests today**: none for this area; the standalone and EditMode tests run without the player branches.

**Tests a change must add.** A player scenario for any `#if !UNITY_EDITOR` change, with a throwaway save folder ([Verification](../Process/Verification.md), "Isolation"). The player build compile is the minimum check for any change to shared code.

## 9. Known limitations and unverified behaviour

- The list of stubs in `UnityEditorRuntimeShim` is long and is not audited here; the table lists only the differences that caused bugs or that the code comments call out.
- Only Windows is a supported player target; file dialogs return null elsewhere.

## 10. Related documents and review

[Architecture](../Architecture.md), [Project and build settings](ProjectAndBuildSettings.md) (the instancing-stripping setting), [Shaders](Shaders.md), [Editor-only tools](EditorOnlyAndLegacy.md), [Document and persistence](DocumentAndPersistence.md), [Editor UI](EditorUi.md), [Editing](../Process/Editing.md), [Verification](../Process/Verification.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
