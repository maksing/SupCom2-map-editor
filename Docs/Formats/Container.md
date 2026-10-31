# The `.bdf` container

Code: `Assets/SC2/Runtime/BdfIO/Core/` (`BdfContainer.cs`, `BdfPlatform.cs`, `BdfPadding.cs`, `BdfReader.cs`, `BdfWriter.cs`, `ZlibCodec.cs`, `Adler32.cs`). System document: [BdfIO](../Systems/BdfIO.md). Shared by all seven `.bdf` formats; the content inside is described on each format's page.

## BdfHeader (32 bytes, start of the file)

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 4 | magic | ASCII `MFDB` = PC, little-endian; `BDFM` = Xbox, big-endian (unverified) |
| 4 | 4 | major version | must be 7 |
| 8 | 4 | minor version | must be 2 |
| 12 | 4 | reserved | must be 0 |
| 16 | 4 | compressed size | bytes of the zlib block |
| 20 | 4 | decompressed size | bytes of the content |
| 24 | 4 | offset table count | number of uint32 entries that follow |
| 28 | 4 | number of files | 1 for most formats, 2 for terrain |

A reader rejects any other version or a non-zero reserved. The magic is plain ASCII; every other number follows the platform's byte order.

## OffsetTable (`uint32[count]`, from offset 32)

Each entry is the position inside the **decompressed content** of a pointer field, listed in the order the fields are written. It lets a loader relocate pointers, and the code also uses it to find structures (the terrain's geometry header is `OffsetTable[last] - 16`).
Every pointer field is listed, including one that holds `0xFFFFFFFF` ("none"). A writer calls `BdfWriter.Mark()` in exactly the layout order; nothing re-sorts the table.

## Padding after the table, and trailing data

After the table comes zero padding, then the zlib block. The padding length depends on the format's mode (below). Terrain and decals hide up to 8 bytes of their own data in this padding (the **trailing data**): terrain stores `[0, GeometryHeaderStart]`, decals the start of their geometry sub-file.
A reader returns trailing data only if the padding is not all zeros.

| Mode | Pad for `rem = position % 32` | Formats |
|---|---|---|
| `Align32` | `(32 - rem) % 32`, 0 to 31 | collision2, info, decals |
| `MinPad32` | `32 - rem`, 1 to 32, never 0 | hfield, costs, mapobjs |
| `Align32Min8` | like `Align32`, but if that gives 0 to 7, add 32 | terrain (so the 8 trailing bytes always fit) |

`MinPad32` is required for mapobjs: `Align32` failed to find the zlib block on 5 of 56 vanilla mapobjs, exactly those whose table ends on a 32-byte boundary. Reader and writer must use the same mode; the formula does not depend on whether trailing data is present.

## Compressed block

A zlib stream: 2-byte header (`78 9C` in every examined file), raw deflate, and a 4-byte Adler-32 trailer in **big-endian** even in a little-endian file.

**Padding after the block**: in all 56 vanilla files of every format the file ends with plain `Align32` zeros (0 to 31 bytes, so the file length is a multiple of 32), whatever the mode of the padding before the block. `BdfContainer.Write` applies the format's own mode to this trailing padding too, so for `MinPad32` and `Align32Min8` it can write up to 32 bytes more than vanilla does when the block ends near a boundary. Readers locate the block from the header sizes, so this has no effect on reading; whether the game minds the extra bytes was not verified.

## Rules

- Compare `Data` and `OffsetTable` after a fresh read, never the raw file: the editor's deflate output differs from the original's.
- The platform decides the byte order of the offset table and of every number in the content.
- Strings inside content are null-terminated ASCII; see [String pool](StringPool.md).

## Checked against the vanilla files

The header facts above (magic, version 7.2, reserved 0, zlib header `78 9C`, big-endian Adler-32 equal to the Adler-32 of the content, decompressed size, number of files 2 for terrain and decals and 1 for the others, padding before the block per format, `Align32` after it) were checked with a scratch script against all 56 `terrain`, `hfield`, `costs`, `collision2`, `info`, `mapobjs` and `decals` files of the local vanilla copy; every file agreed.
