# Sculpt and paint brushes, symmetry and stamps

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

The tools that change the terrain with the mouse: the **Geometry** brush (sculpting heights), the **Paint Materials** brush in its two sub-modes (**Assign** a pair to triangles, **Blend** the mask),
the custom brush images and stamp placement, the radius gesture, the pair locks, the heightmap import and export, and the **symmetry** that mirrors every dab.

Not included: what a blend mask is ([blend mask](BlendMask.md)), pairs ([materials and pairs](MaterialsAndPairs.md)), symmetry of objects ([objects](Objects.md)), which surface a dab reaches ([background](BackgroundTerrain.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Sculpt | `TerrainEditor/SC2GeometryMode.cs` (tools: Raise, Lower, Smooth, Flatten, Noise, SetHeight; state `SC2GeometryToolState`) |
| Paint | `TerrainEditor/SC2PaintMode.cs` (sub-modes Assign and RNM/Blend; GPU brush via `Resources/TerrainEditorShaders/SC2PaintBrush.shader`) |
| Brush images and stamps | `SC2BrushLibrary.cs` (built-in `brush:NAME` images), `SC2StampPlacement.cs`, `SC2StampPreview.cs`, `SC2BrushPresets` |
| Radius gesture | `Scripts/Unified/SC2BrushRadiusGesture.cs` (hold the hotkey and move the mouse) |
| Locks | `Scripts/Core/SC2PairLockSet.cs` |
| Symmetry | `SC2TerrainData.GetSymmetricPositions`, `SymmetryMode`, `SC2RadialSymmetryMath`, `Scripts/SC2Symmetry.cs` (grid cells for Costs), `SC2SymmetrySettings.cs`; the top-bar controls in `SC2GameViewEditorUI` |
| Heightmap files | `TerrainEditor/SC2HeightmapExporter.cs` (own 16-bit PNG reader and writer) |

## 3. Data

Brush state (tool, radius, strength, hardness, flatten and set-height targets, custom brush path, stamp options) is shell state, captured with the undo snapshot as `SC2GeometryToolState`, not saved with the map.
Saved with the map: `Symmetry`, `RadialCount`, `SymmetryCenter` (the point the mirror or rotation is about; presets 256, 512, 1024 and Center X/Z fields in the top bar), `UseCustomBrush` and `CustomBrushTexturePath`.

## 4. Flows

- **A dab**: the mouse ray finds a ground point on the active surface; `GetSymmetricPositions(pos)` gives the mirrored positions; each position is applied. Sculpting changes `Heights` (CPU) and the chunk meshes; painting is
  **one GPU blit per dab** into the pair map or the blend render texture, with no per-pixel CPU work.
- **Modifiers**: Ctrl with the wheel changes strength, Alt with the wheel changes hardness (Geometry), the radius gesture resizes, Ctrl erases in Blend (paints the first material), Alt+click samples a height for SetHeight. All of them are
  remappable hotkeys (`SC2HotkeyActions`: `Brush.*`).
- **Stamps**: a custom brush in "place as stamp" mode anchors on press, sizes and turns while dragging, and is placed once on release by replaying an ordinary first dab with the chosen size and angle, so symmetry, locks, undo and the
  background behave as for any dab.
- **Stroke end (mouse up)**: sculpting syncs heights and marks the session dirty; Assign syncs the pair map to the CPU and refreshes outlines; Blend flushes every touched surface ([blend mask](BlendMask.md)).
- **Heightmap**: export writes a 16-bit PNG built by hand (Unity's own encoder wrote 8 bits); import resamples and scales a PNG onto the grid.

## 5. Invariants

- **A dab is applied to every symmetry slot** and, for a mask, to every surface the brush reaches; a new brush feature that skips one of them breaks symmetry or the seam.
- **Symmetry is about `SymmetryCenter`**, which defaults to the middle of the map; the mirror of a point is computed from it, not from a hard-coded 512.
- **Painting uses the GPU, so CPU data lags**; read pair or mask data only after a sync or flush.
- **A brush stroke is one undo step** (one checkpoint on the first dab).
- **The pair locks belong to the surface being edited**; the other surface of a seam stroke paints every pair.
- **A brush's shape settings (custom image, random rotation) are core-only and apply to both surfaces** (one state, not two).

## 6. Editor and build differences

- Brush input is the same IMGUI event code in both; the custom brush image picker uses the runtime file dialog in a player.

## 7. Extension points and how to change it

- **A new sculpt tool**: an enum value, its panel controls, its per-vertex rule applied for each symmetry slot, the undo checkpoint at the first dab.
- **A new brush option**: state in the mode, a shader property, the call; add the hotkey as an action if it has a modifier.
- **A new symmetry mode**: `SymmetryMode` (keep existing ordinals: saved values are ordinals), `GetSymmetricPositions`, the cell version in `SC2Symmetry`, the Objects and lobby code that share the math (`SymmetryRotationTests` pin it).

## 8. Editing, verification and tests

**How to edit it safely.** Keep the enum ordinals of `SymmetryMode` (an old save with an ordinal in 4 to 7 is remapped by the legacy loader). Test a stroke with each symmetry mode and with undo.

**Tests today**: `SymmetryMirrorTests`, `SymmetryRotationTests`, `SymmetrySlotYawTests`, `SymmetryTurnTests`, `FlattenMathTests`, `StampGestureTests`, `BrushPresetsTests`, `PairLockSetTests`, `HeightModeTests`, `ImageResampleTests`, `AxisMoveTests`, `AxisRotationTests`.
The modes themselves have no unit test.

**Tests a change must add.** A per-vertex rule: a pure function with a test (extract it from the mode). A symmetry change: a case in the symmetry tests for every mode. A stroke-level change: a scenario that applies strokes with a mode and compares data.

## 9. Known limitations and unverified behaviour

- The relation between brush strength and the resulting height change is tuned by eye and not recorded.
- Performance of a large radius on the largest maps was not measured here.

## 10. Related documents and review

[Terrain data and runtime](TerrainDataAndRuntime.md), [Blend mask](BlendMask.md), [Materials and pairs](MaterialsAndPairs.md), [Background terrain](BackgroundTerrain.md), [Objects](Objects.md), [Editor UI](EditorUi.md).
Interactions with other systems: [Life of an edit](../Interactions/LifeOfAnEdit.md), [Materials, masks, lighting](../Interactions/MaterialsMasksLighting.md), [Undo](../Interactions/UndoAcrossSystems.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
