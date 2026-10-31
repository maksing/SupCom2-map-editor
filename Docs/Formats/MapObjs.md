# `mapobjs.bdf` (props, markers, graphs)

Code: `Assets/SC2/Runtime/BdfIO/Formats/MapObjs/MapObjsBdf.cs`. Container: [Container](Container.md), padding `MinPad32`, 1 file. Built by `SC2ObjectsExport.BuildMapObjsModel`; imported by `SC2ObjectsImport`. The editor's objects (units, armies, areas and chains go to the Lua files instead) are in [Objects](../Systems/Objects.md).

## Structure, in order

| Section | Size | Notes |
|---|---|---|
| MapObjectsHeader | 28 | |
| MapProp[] | 44 each | |
| blueprint path pool | variable | deduplicated, strings 4-byte aligned |
| MapMarker[] | 36 each | |
| name and marker-type pool | variable | one shared deduplicated pool for both string kinds |
| MapGraphHeader[] | 12 each | |
| GraphEntry[] | 8 each | grouped per graph, in graph order |
| graph name pool | variable | deduplicated |

## MapObjectsHeader (28 bytes)

| Offset | Field |
|---:|---|
| 0 | `FileVersion` (int, 4) |
| 4 | prop count |
| 8 | prop list offset (pointer) |
| 12 | marker count |
| 16 | marker list offset (pointer) |
| 20 | graph count |
| 24 | graph list offset (pointer) |

## MapProp (44 bytes)

| Offset | Size | Field |
|---:|---:|---|
| 0 | 16 | orientation quaternion, four floats (`Quat`, identity is 1, 0, 0, 0) |
| 16 | 12 | position, three floats |
| 28 | 4 | scale (float, 1) |
| 32 | 4 | blueprint path (pointer into the blueprint pool) |
| 36 | 4 | `Unknown9` (float, 1; meaning not confirmed) |
| 40 | 4 | `Flags` (uint) |

## MapMarker (36 bytes)

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | name (pointer) |
| 4 | 4 | `AlwaysOne` (float, 1; always 1 on the maps examined) |
| 8 | 4 | `Unk2` (int) |
| 12 | 4 | `Unk3` (int) |
| 16 | 4 | `Unk4` (int) |
| 20 | 12 | position, three floats |
| 32 | 4 | marker type (pointer) |

The marker's name and type are strings such as `ARMY_1` and `Mass`; the editor's marker types and how it fills the unknown fields are in [Objects](../Systems/Objects.md).

## MapGraphHeader (12 bytes) and GraphEntry (8 bytes)

| Offset | Field |
|---:|---|
| 0 | graph name (pointer) |
| 4 | entry count |
| 8 | entries offset (pointer to this graph's first entry) |

A `GraphEntry` is two int32 values, a pair of **marker indices** (an edge of the graph between two markers of the same file).

## Empty lists

In the vanilla files an empty list has its offset field set to `0xFFFFFFFF`: measured on all 56 maps, 5 have no props (prop offset `0xFFFFFFFF`), 20 have no graphs (graph offset `0xFFFFFFFF`), none has no markers. The offset-table entry of the field is still present. The editor's serializer always writes real offsets (28 for the prop list, the end of the marker data for an empty graph list); whether the game minds this was not verified.

## Offset table

For the vanilla map examined (628 props, 412 markers, 4 graphs: 1463 entries), confirmed byte-for-byte:

1. each prop's blueprint path pointer (positions of the field at +32 of each prop);
2. the header's prop list offset field (position 8);
3. each marker's name pointer, for all markers; then each marker's type pointer, for all markers;
4. the header's marker list offset field (position 16);
5. for each graph in order: the graph name pointer, then the entries pointer;
6. the header's graph list offset field (position 24).

## Rules

- All three string pools are deduplicated and 4-byte aligned.
- Padding before the block is `MinPad32`; `Align32` fails to locate the zlib stream on 5 of the 56 vanilla files. The offset-table count `props + 1 + 2 * markers + 1 + 2 * graphs + 1` held on all 56 files.
- Graph entries refer to markers by index, so the marker order must not change between writing the markers and the graphs. The editor resolves its graph references when it builds the model (`SC2ObjectGraphRefs`).
