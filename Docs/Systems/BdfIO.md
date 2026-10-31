# BdfIO: the game's `.bdf` container and formats (`Runtime/BdfIO`)

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P1 (a wrong byte makes the game reject or misrender a map). Read [Export pipeline](ExportPipeline.md) first. The layouts themselves are in [Formats](../Formats/README.md).

## 1. Purpose and boundaries

Reads and writes the game's binary map files. Two assemblies: `BdfIO.Core` (the container shared by all formats, byte-order aware reading and writing, zlib, padding) and `BdfIO.Formats` (one parser and serializer per file: terrain, hfield, costs, collision2, info, mapobjs, decals).
Both are plain C#, have no Unity dependency and compile as source inside the Unity project, so the editor, a player build and the standalone test project all use the same code.

Not included: deciding what goes into a file (the [export pipeline](ExportPipeline.md) and the shells), building a terrain model from the editor's data (`SC2TerrainLayerBuilder`), the decals geometry (clipping is in the document layer; only the spatial tree is here).

## 2. Where the code is

| Part | Files in `Assets/SC2/Runtime/BdfIO/` |
|---|---|
| Container | `Core/BdfContainer.cs` (header, offset table, padding, zlib block; `Read`, `Write`), `Core/BdfPlatform.cs` (`BdfPlatform` Pc/Xbox, `BdfMagic`), `Core/BdfPadding.cs` (`Align32`, `MinPad32`, `Align32Min8`) |
| Numbers and strings | `Core/BdfReader.cs` (sequential `Read*`, random-access `Peek*At`, `ReadCStringAt`), `Core/BdfWriter.cs` (`Write*`, `Mark` for the offset table, `PadTo`, a string pool builder), `Core/HalfConvert.cs` |
| Compression | `Core/ZlibCodec.cs` (a hand-built zlib wrapper over raw deflate), `Core/Adler32.cs` |
| Formats | `Formats/Terrain/` (`TerrainBdf`, `TerrainModel`, `TerrainLayout`, `TerrainBspBuilder` with `EdbTreeBuilder`), `Formats/Hfield/HfieldBdf.cs`, `Formats/Costs/CostsBdf.cs`, `Formats/Collision2/Collision2Bdf.cs`, `Formats/Info/InfoBdf.cs`, `Formats/MapObjs/MapObjsBdf.cs`, `Formats/Decals/DecalsBdf.cs` and `DecalsSpatialBuilder.cs` |
| Build plumbing | `Core/BdfIO.Core.asmdef`, `Formats/BdfIO.Formats.asmdef` (references Core), `CompilerFeaturesPolyfill.cs` in both (supplies `IsExternalInit` for older compilers), `README_SourceLayout.md` |
| Tests | `Tests~/StandaloneCharacterization/BdfFormatRoundTripTests.cs` (every format against the vanilla corpus), format-specific tests for costs, hfield, collision2, decals, terrain |

## 3. Data

A file is `BdfContainer`: a 32-byte header, an **offset table**, padding, one zlib block, padding. The header holds the magic, version (7.2), reserved (0), compressed size, decompressed size, offset table count, and the number of logical sub-files (1, or 2 for terrain). `Data` is the decompressed
content; `OffsetTable` lists the positions inside `Data` of every pointer field (so the loader can relocate them); `TrailingData` is up to 8 bytes hidden in the padding (terrain and decals keep the start of their geometry sub-file there). Each format has a **model** class (`TerrainModel`, `HfieldModel`, `CostsModel`, `Collision2Model`,
`InfoModel`, `MapObjsModel`, `DecalsModel`) and static `Parse(BdfContainer)` and `Serialize(model, platform)` functions. `InfoModel` is special: it wraps a flat 508-byte header with typed accessors instead of one field per member. Details of each layout: [Formats](../Formats/README.md).

## 4. Flows

- **Read**: detect the platform from the magic (`MFDB` little-endian PC, `BDFM` big-endian Xbox), read the header, read the offset table, compute the padding for the file's mode, take the trailing data out of the padding if it is not all zero, inflate, then the format's `Parse` walks `Data` with `Peek*At` and follows pointers.
- **Write**: the format's `Serialize` writes `Data` and calls `Mark()` for each pointer field in layout order, which builds the offset table; `BdfContainer.Write` compresses, pads, and assembles the file. The result is checked against the size it computed.
- **Round trip**: re-reading what was written must give the same `Data` and `OffsetTable`; the compressed bytes need not equal the original (the deflate output differs), so tests compare the decompressed content, not the file.
- **Terrain BSP**: `TerrainBdf.RebuildAllBsp` / `RebuildLayerBsp` build the BSP forest from the triangles first, then repack each leaf's geometry (deduplicating vertices), so vertex and triangle counts of a layer can change and every later layer's cumulative counts shift.

## 5. Invariants

- **Platform decides the byte order of every numeric field**, header and content alike. Only PC was verified against real files; Xbox is supported in code and unverified.
- **The version must be 7.2 with reserved 0**; anything else is rejected on read.
- **Padding mode is per format and must match between `Read` and `Write`**: hfield, costs and mapobjs use `MinPad32` (a full 32 bytes even when already aligned; `Align32` failed to find the zlib block on 5 of 56 vanilla mapobjs), collision2, info and decals use `Align32`, terrain uses `Align32Min8` (at least 8 bytes so its trailing data fits).
- **Vanilla files end with plain `Align32` padding after the zlib block**, in every format; the writer applies the format's own mode there too, which differs from vanilla by up to 32 bytes at a boundary (harmless for readers, unverified for the game).
- **The offset table order is the layout order.** `Mark()` must be called in exactly the order the format's pointer fields are written, and for each pointer field even when its value is the "none" value `0xFFFFFFFF`. Nothing reorders it.
- **Terrain serialization is positionally exact**: the offset table, the data length and every byte outside the string pool equal the original for an unmodified model; the string pool's order may differ, but a re-parse is semantically identical.
- **The packed normal, tangent and binormal are three signed 10-bit fields per uint32 with divisor 51.1.** Writing float32 here gives a garbage tangent basis in the game (diffuse is unaffected, specular and environment reflections are not). Vertices read from a file keep their raw words so re-serialization is byte-identical; built vertices are packed at serialize time.
- **UV1 is a half-float pair, UV2 an int16 pair scaled by 32767.** `HalfConvert` exists because `System.Half` is not available in the target framework.
- **Collision2, terrain and decals share the 108-byte EDH and 56-byte EDB tree layout.**
- **No `UnityEngine` and no editor API** in either assembly, so the standalone project and a player can compile them.

## 6. Editor and build differences

- None: both assemblies are the same source in the editor, in a player and in the test project. A format change cannot be a player-only difference.
- The Unity copy is compiled with an older C# language level than the original .NET projects; avoid newer syntax that the project's compiler rejects (the layout note lists file-scoped namespaces, `required`, `init`, primary constructors, record structs and collection expressions as not allowed). Some files already use features the note names, so confirm with a compile before relying on it.

## 7. Extension points and how to change it

- **A new field in an existing format**: add it to the model, to `Parse` and `Serialize` at its offset, to the offset table if it is a pointer, and to the round-trip test; keep the unknown fields (named `Unknown9`, `Unk2` to `Unk4`, `ReservedZero236` and the like) exactly as read, without guessing their meaning.
- **A new format**: a model, a static class with `Padding`, `Parse`, `Serialize`, a `BdfContainer` wrapper if useful, a `Compile Include` in the standalone project, a corpus round-trip test.
- **A different compression or layout**: only in `ZlibCodec` and `BdfContainer`; every format depends on both.

## 8. Editing, verification and tests

**How to edit it safely.** Never change the layout of an existing field to match what the editor wants; the game reads these files. Verify against the vanilla corpus: parse every map, serialize, re-parse, compare content and offset tables. For a changed writer, also compare the bytes outside any intentionally regenerated section.

**Tests today**: `BdfFormatRoundTripTests` (one structural round trip per format, terrain, hfield, costs, collision2, info, mapobjs and decals, over every vanilla map). Related: `Uv1UnwrapTests`, `Collision2FloorTests`, `DecalsAuthoringTests`, `RnmGapFillTests`. **These characterizations read the vanilla corpus from a local game-data folder and are skipped when it is missing** ([Tests and release](TestsAndRelease.md)).

**Tests a change must add.** A format field: a round trip including a value that is not the default. A padding or offset-table change: a case that lands on a 32-byte boundary. A BSP builder change: a vertex and triangle count comparison against a known map.

## 9. Known limitations and unverified behaviour

- The Xbox variant is not verified against real files.
- Several fields have no confirmed meaning (`ReservedZero236` and the reserved texture path in info, `Unk2..4` and `AlwaysOne` in map markers, `Unknown9` in props, the 28-byte tail of collision2, `LongInts` of costs blocks); they are preserved, not understood.
- The deflate output differs from the original files, so a byte comparison of whole files is not a valid check.
- The standalone project's coverage of these formats is only as good as the corpus on the machine that runs it.

## 10. Related documents and review

[Formats](../Formats/README.md), [Export pipeline](ExportPipeline.md), [Import pipeline](ImportPipeline.md), [Smart export](SmartExport.md), [Collision2](Collision2.md), [Tests and release](TestsAndRelease.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
