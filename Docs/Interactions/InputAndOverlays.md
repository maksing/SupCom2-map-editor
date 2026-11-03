# Input, overlays and the order of a frame

Status: first draft from reading `SC2GameViewEditorUI.OnGUI` and the handlers it calls at commit `17863e4`; not yet reviewed by a second reader. Read [Editor UI](../Systems/EditorUi.md) first.

In IMGUI, **order is the contract**: whoever runs first sees an event first and may consume it. Almost every "my click did nothing" or "my click did two things" bug in the editor is an ordering problem. This page records the order and the reasons recorded in the code.

## 1. One frame of `OnGUI`, in order

1. **Is input blocked?** `_menuOverlayBlocksInput` is true while a menu, a runtime overlay (dropdown, colour palette, object picker) or the **busy plate** is active, and the event is neither `Repaint` nor `Layout`.
2. **Global hotkeys first**: `HandleGlobalUndoRedoSaveHotkey()` (Ctrl+Z, Ctrl+Y, Ctrl+S) runs **before** the header and the main panel. Reason: a focused numeric field's native `TextField` consumes Ctrl+Z internally while it draws itself, which happens inside the main panel, so a check made after the panels never saw it.
3. **A modal masks the panels.** If any modal is open (`AnyModalOpen`), the panels are drawn against an **`Ignore` event** (the real event is put back before the menus and the modals are drawn). `GUI.enabled = false` is not enough because many of the editor's own controls (thumbnails, foldout headers, clickable rows) read the event themselves. The texture browser is the one modal that keeps the panels' **texture slots** live: a click on another slot moves the browser there (`BeginPanelEventMask`, `TryTakePanelClick`, `EndPanelEventMask`).
4. **A stale `hotControl` is released** on a left press, because a press whose release never reached its control (a modal or a mask took it) would otherwise make every other control, a menu slider included, ignore the next press.
5. **Hover under a menu is masked**: hover is worked out in `Repaint` from the mouse position alone, so while the mouse is over a menu the panels behind it see the mouse far away; the real position is restored just before the menus are drawn.
6. **The panels** (`DrawMainPanel`): the active tab's shell draws its embedded GUI; sub-tabs, the status bar.
7. **Menus, then overlays**, in this order: the mission panel, the runtime texture picker overlay, the hotkeys panel, the theme panel, the Costs guide, the update notice, the runtime dropdown, the runtime colour palette, the runtime object picker, the runtime confirm, the map import panel, the Info.bdf panel, the lobby panel. Later is drawn on top and gets the event first.
8. **The busy overlay is drawn last** (`SC2BusyOverlay.Draw`), above everything.

**The modals** (`AnyModalOpen`): the texture browser, the map import panel, the Info.bdf panel, the lobby panel, the mission panel, the hotkeys panel, the theme panel, the Costs guide viewer, the update notice, the busy plate, and in a player the runtime confirm. **A new modal must be added to this test**, or clicks fall through to the viewport.

## 2. Viewport input, in order

When no modal blocks, the host runs the viewport handlers in a fixed order each frame:

| # | Handler | Notes |
|---|---|---|
| 0 | `RenderTexture.active = null` | the paint calls blit into render textures and a leaked active target would corrupt the brush cursor drawn first |
| 1 | `HandleMeasureToolGameViewInput` | consumes its own clicks (`e.Use()`) so the tabs' handlers do not also act while Measure is active |
| 2 | `HandleReferenceUnitsGameViewInput` | **before the tabs**: placement, select and drag consume their own click only in the cases that matter and fall through otherwise; when this ran last, Objects' click-to-select swallowed a placement click meant for a reference unit |
| 3 | `DrawMapFrameOverlay` | |
| 4 | `HandleTerrainGameViewInput` | **skipped while Measure is active**: it draws only the Geometry or Paint brush ring, so skipping it hides just that cursor |
| 5 | `SyncTerrainOverlayFlags` | |
| 6 | `HandleCostsGameViewInput` | **not skipped during Measure**: it draws passive content (the Costs tint) from any tab |
| 7 | `PushBuildableOverlay` | pushes the reference building's buildable colouring into the terrain |
| 8 | `HandleCollision2GameViewInput` | |
| 9 | `HandleObjectsGameViewInput` | **not skipped during Measure**: it draws props, markers, path graphs and areas from any tab; skipping it once made path graphs vanish when Measure turned on |
| 10 | `HandleDecalsGameViewInput` | |
| 11 | `HandleImportedTerrainGameViewInput`, `DrawSelectedImportedTerrainFrame` | |
| 12 | `HandleHotkeys` | the remaining actions, **after** every panel and tool |
| 13 | `HandleGameViewNavigation` | consumes navigation events over the UI so the Scene view does not take clicks on buttons as navigation |
| 14 | `DrawPreviewModeBar` | painted last so lines the tools drew above do not show through it |

The shells' own `allowInput` flags and tab-active tests gate the actual brush cursors, which is why the passive handlers (Costs, Objects) can run every frame. The Costs and Objects handlers (and at least one more) wrap their work in a try/catch so an exception in one tool cannot stop the others from running; do the same in a new handler.

**The camera's left button.** The tools (steps before 13) see a mouse press before `HandleGameViewNavigation` does, and the camera skips an event a tool has already used. A camera action can be bound to the left button (`Camera.Orbit`, `Camera.Pan`, `Camera.Fly` in Edit Hotkeys, for example Alt+Left Mouse), so the Costs, Reference Units, Objects and Decals handlers add `!CameraOwnsLeftButton(Event.current)` to their `allowInput`: a left press that matches a camera binding, and the drag and release that follow it, never reach the tool. A new tool that acts on a left press must be gated the same way. The Terrain brushes and the imported-terrain tools do not use this: they test `!e.alt` themselves, so a camera action bound to Alt+Left Mouse still collides with Alt+click there (not checked).

## 3. Interactions between the layers

| Interaction | What the code does |
|---|---|
| **Hotkeys and text fields** | `HandleHotkeys` has a stuck-focus guard so typing into a field does not also fire the field's key as an action; Ctrl+Z, Ctrl+Y, Ctrl+S are handled earlier, in the global handler |
| **A gesture and a hotkey on the same key** | the brush-radius gesture (default F, rebindable, action `Brush.RadiusResizeGesture`) runs inside the terrain handler, **before** `HandleHotkeys`, which also binds F to `Global.FocusCamera`; the gesture calls `e.Use()` and the hotkey handler checks the event type, so they do not collide, whatever either is bound to |
| **A camera drag's mouse-up** | the camera handler does not `Use()` its mouse-up, so it falls through to the tools; a tool must gate its mouse-up work on its own active stroke (the sculpt once paid for a full heightmap copy on every camera release) |
| **Clicks on overlays vs the viewport** | `SC2ObjectsShell.OverlayOccluder` is a static rectangle that panels such as an open picker add to, so the viewport does not select through them |
| **The busy plate** | while it is active, input is swallowed, so nothing can change the data a worker is reading, and Ctrl+Z, Ctrl+S and the tools are not seen; autosave, export and bake share the one plate ([Save, import, export](SaveImportExport.md)) |
| **Pointer position under UI scale** | the UI scale changes the numbers (fonts, widths) and never uses `GUI.matrix`, because viewport raycasting assumes raw screen pixels; a control with a hard-coded size breaks at a scale other than 1 |
| **The player's dialogs** | the runtime confirm, dropdown, colour palette and file dialog are overlays in the list above; `EditorUtility.DisplayDialog` always agrees in a player |
| **Texture browser and panel slots** | see step 3; the picker key identifies which field gets the pick |

## 4. Rules

1. **A tool that must not fire while another is active consumes its event** (`e.Use()`) and runs earlier, or checks the state of the other tool; it does not rely on being later.
2. **A passive overlay is drawn by a handler that always runs**; a tool cursor is drawn by a handler gated on its tab.
3. **Anything that draws a modal registers in `AnyModalOpen`**, and is drawn in the overlay list at the position that matches its stacking.
4. **A tool's mouse-up must check that its own stroke is active.**
5. **Do not read `Input` for viewport actions**; use the hotkey lookup by action id ([Editor UI](../Systems/EditorUi.md)).
6. **A long job goes through `SC2BusyOverlay.RunInBackground`**, never a blocking call in `OnGUI`.

## 5. Tests and verification

`BusyGateTests` and `TypographyTests` cover two pieces. Frame order, event consumption and modal masking have no test; the verification is the harness: send real events to the window and read back which handler ran ([Verification](../Process/Verification.md)).

## 6. Unverified

The overlay order (step 7) is from the order of calls in `OnGUI`; whether each overlay's own draw is gated on its open state was not read for all of them.
