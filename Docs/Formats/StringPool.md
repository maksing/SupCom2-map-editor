# String pool and pointers

Code: `StringPoolWriter` in `Assets/SC2/Runtime/BdfIO/Core/BdfWriter.cs`; `BdfReader.ReadCStringAt`. Used by terrain, info, mapobjs and decals. Hfield, costs and collision2 have no strings.

## Pointer

A pointer is a `uint32` position inside the decompressed content. `0xFFFFFFFF` means "none" (an optional string absent, a missing child node). Every pointer field is also an entry of the container's [offset table](Container.md).

## String

Null-terminated ASCII, then zero bytes up to the next 4-byte boundary (0 to 3 filler bytes separate one entry's terminator from the next entry; confirmed byte-for-byte on vanilla terrain data).

## Pool behaviour per format

| Format | Deduplication | Where the pool is |
|---|---|---|
| terrain | **none**: identical texture paths are written again, each pointer owns its copy (that is how the game's own tool wrote them) | after the shader setup items, between the materials and the geometry header |
| mapobjs | yes, three separate pools: blueprint paths, marker names plus marker types (one shared pool), graph names | after the props, after the markers, after the graph entries |
| info | yes | right after the 508-byte header (offset 508) |
| decals | not a pool: two paths written one after the other, each on a 4-byte boundary | right after the 16-byte header |

## Rules

- A writer must keep the order of strings stable between layout and writing, because pointer values depend on it. For terrain the order may differ from the original file; a re-parse is semantically identical (`TerrainBdf` documents this).
- `Add` returns the offset **within the pool**; the caller adds the pool's base offset in the content.
